import { dc, sh, check, finish, sleep, waitFor, rest, newUser, newSession, ask, messages, jobs, newConv, dsend, hub, stub, mgmt, mgmtReady, queues, depth, purgeAll, sql, count, cleanDb, healthy, useReplicas, waitHealthy, refreshLb, redisUp, LB, PORTS, R1 } from '../lib.mjs';

// ---- Phase 20: combined and sequential failures on the whole system (three API replicas behind a balancer) ----
const jobRow = (id) => { const [status, attempts, hasReply] = sql(`SELECT Status, Attempts, AssistantMessageId IS NOT NULL FROM AiReplyJobs WHERE Id='${id}'`).split('\t'); return { status, attempts: Number(attempts), hasReply: hasReply === '1' }; };
const consumers = () => { try { return queues()['ai.reply']?.c; } catch { return -1; } };
const settle = async () => { await mgmtReady(); await redisUp(); await waitHealthy(PORTS, 120000); await waitFor(() => consumers() === 3, 120000, 1000); };
/** The invariants that must hold after ANY of the failures above. */
function invariants(label) {
  const dupReplies = Number(sql("SELECT COUNT(*) FROM (SELECT AssistantMessageId FROM AiReplyJobs WHERE AssistantMessageId IS NOT NULL GROUP BY AssistantMessageId HAVING COUNT(*) > 1) d"));
  const doneNoReply = count('AiReplyJobs', "Status='done' AND AssistantMessageId IS NULL");
  const replyNoDone = count('AiReplyJobs', "AssistantMessageId IS NOT NULL AND Status<>'done'");
  const stuck = count('AiReplyJobs', "Status IN ('pending','processing')");
  const undispatched = count('OutboxMessages', 'DispatchedAt IS NULL');
  return { ok: dupReplies === 0 && doneNoReply === 0 && replyNoDone === 0 && stuck === 0 && undispatched === 0 && depth('ai.dead') === 0 && depth('direct.events.dead') === 0, text: `${label}: duplicate replies ${dupReplies}, done-without-reply ${doneNoReply}, reply-but-not-done ${replyNoDone}, jobs still open ${stuck}, events waiting ${undispatched}, ai.dead ${depth('ai.dead')}, direct.events.dead ${depth('direct.events.dead')}` };
}

await mgmtReady();
await useReplicas(['api', 'api2', 'api3']);
await refreshLb();
cleanDb(); purgeAll();
await stub.control({ reset: true, delay_ms: 0, fail_next: 0, fail_always: false });
await settle();
const alice = await newUser('Alice', LB), bob = await newUser('Bob', LB);
const sess = await newSession(alice, LB);
const conv = await newConv(alice, bob, LB);
const hubA = await hub(alice, LB);
check('X-01', 'three replicas, RabbitMQ, Redis and MySQL are up, every reply worker is attached, and the balancer answers', await waitFor(async () => consumers() === 3, 60000, 500) && (await fetch(`${LB}/`).then(r => r.status)) === 200);

// =============== C1: RabbitMQ stops while the AI is mid-call ===============
console.log('\n--- C1: RabbitMQ stops in the middle of an AI call, then returns ---');
await stub.control({ reset: true, delay_ms: 6000 });
const c1 = await ask(alice, sess, 'broker stops mid reply', LB);
await waitFor(async () => (await stub.stats()).in_flight >= 1, 10000, 100);
dc('stop rabbitmq');
const c1done = await waitFor(() => jobRow(c1.json.jobId).status === 'done', 40000, 500);
check('X-02', 'RabbitMQ stopped while the AI was answering: the worker still saves the reply (job done in the database) although it cannot acknowledge the message', !!c1done && (await messages(alice, sess, LB)).some(m => m.content === 'stub reply to: broker stops mid reply'));
dc('start rabbitmq');
await settle();
await sleep(5000);
const stC1 = await stub.stats();
check('X-03', 'RabbitMQ back: the unacknowledged message is delivered again and does nothing: still exactly ONE AI call and ONE reply', stC1.calls === 1 && count('Messages', "Role='assistant' AND Content='stub reply to: broker stops mid reply'") === 1, `AI calls ${stC1.calls}`);
check('X-04', 'Alice received the reply push exactly once, and the queues are empty', hubA.of('AssistantReplied', p => p.jobId === c1.json.jobId).length === 1 && depth('ai.reply') === 0 && depth('ai.dead') === 0);

// =============== C2: Redis AND RabbitMQ stop together ===============
console.log('\n--- C2: Redis and RabbitMQ both stop during a reply and while chatting, then return ---');
await stub.control({ reset: true, delay_ms: 4000 });
const c2 = await ask(alice, sess, 'both brokers stop', LB);
await sleep(1000);
dc('stop rabbitmq redis');
const during = [];
for (let i = 0; i < 10; i++) during.push(await dsend(alice, conv, `chat while both are down ${i}`, PORTS[i % 3]));
const askDown = await ask(alice, sess, 'asked while both are down', LB);
await sleep(8000);
check('X-05', 'both brokers down: a new async question is refused with a prompt 503 (question kept), direct chat still works (10 of 10 saved), every replica stays healthy', askDown.status === 503 && during.every(r => r.status === 201) && (await Promise.all(PORTS.map(healthy))).every(Boolean), `ask ${askDown.status} in ${askDown.ms} ms; sends ${during.map(r => r.status).join('')}`);
check('X-06', 'the reply that was in flight was still produced and saved while both were down', jobRow(c2.json.jobId).status === 'done');
dc('start rabbitmq redis');
await settle();
await waitFor(() => count('OutboxMessages', 'DispatchedAt IS NULL') === 0, 90000, 1000);
check('X-07', 'both back: the 10 chat events waiting in the database are all published, reply workers re-attach, and the invariants hold', invariants('after C2').ok, invariants('after C2').text);
const afterBoth = await ask(alice, sess, 'after both returned', LB);
check('X-08', 'both back: a new question is answered and the live push works again', !!(await hubA.wait('AssistantReplied', p => p.jobId === afterBoth.json.jobId, 30000)));

// =============== C3: rolling restart of every replica under load ===============
console.log('\n--- C3: all three replicas are restarted one after another while questions and chat messages keep coming ---');
await stub.control({ reset: true, delay_ms: 1200 });
const accepted = { jobs: [], direct: [] }; const failed = [];
let stop = false;
const load = (async () => {
  let i = 0;
  while (!stop) {
    i++;
    const [r1, r2] = await Promise.all([ask(alice, sess, `load ${i}`, LB).catch(e => ({ status: 0, err: String(e) })), dsend(i % 2 ? alice : bob, conv, `load direct ${i}`, LB).catch(e => ({ status: 0, err: String(e) }))]);
    if (r1.status === 202) accepted.jobs.push(r1.json.jobId); else failed.push(`ask:${r1.status}`);
    if (r2.status === 201) accepted.direct.push(r2.json.id); else failed.push(`direct:${r2.status}`);
    await sleep(250);
  }
})();
for (const svc of ['api', 'api2', 'api3']) {
  dc(`restart ${svc}`);
  await waitFor(() => healthy({ api: PORTS[0], api2: PORTS[1], api3: PORTS[2] }[svc]), 90000, 500);
  await sleep(3000);
}
await sleep(3000);
stop = true; await load;
await settle();
const allJobsDone = await waitFor(() => accepted.jobs.every(id => jobRow(id).status === 'done'), 180000, 1000);
await waitFor(() => count('OutboxMessages', 'DispatchedAt IS NULL') === 0, 120000, 1000);
const lost = accepted.jobs.filter(id => !jobRow(id).hasReply).length;
check('X-09', `rolling restart under load: every ACCEPTED question (${accepted.jobs.length}) still gets its reply, none lost`, !!allJobsDone && lost === 0, `failed requests during the restarts: ${failed.length} (${[...new Set(failed)].join(', ') || 'none'})`);
check('X-10', 'and each accepted job has exactly one reply (no duplicates), even though workers were killed mid-call', Number(sql("SELECT COUNT(*) FROM (SELECT AssistantMessageId FROM AiReplyJobs WHERE AssistantMessageId IS NOT NULL GROUP BY AssistantMessageId HAVING COUNT(*) > 1) d")) === 0 && count('AiReplyJobs', "Status='done'") >= accepted.jobs.length, `AI calls ${(await stub.stats()).calls} for ${accepted.jobs.length} jobs`);
check('X-11', `every accepted direct message (${accepted.direct.length}) was saved, and every one has a published "sent" event`, accepted.direct.every(id => count('DirectMessages', `Id=${id}`) === 1) && count('OutboxMessages', "Type='direct.message.sent' AND DispatchedAt IS NOT NULL") >= accepted.direct.length);
check('X-12', 'after the rolling restart all invariants hold (no stuck jobs, no waiting events, empty dead-letter queues)', invariants('after C3').ok, invariants('after C3').text);

// =============== C4: MySQL goes away ===============
console.log('\n--- C4: MySQL stops for 15 s while a reply is being produced, then returns ---');
await refreshLb();
await stub.control({ reset: true, delay_ms: 3000 });
const c4 = await ask(alice, sess, 'mysql stops during this', LB);
await sleep(1000);
dc('stop mysql');
const dbDown = await ask(alice, sess, 'asked while mysql is down', LB);
const dbDownList = await rest(LB, '/api/chat/sessions', { token: alice.token });
check('X-13', 'MySQL down: requests fail with the standard error shape (not a hang, not an HTML page) within a bounded time, and /health stays 200', [dbDown, dbDownList].every(r => r.status >= 500 && !!r.json?.error?.code && r.ms < 45000) && (await Promise.all(PORTS.map(healthy))).every(Boolean), `ask ${dbDown.status} ${dbDown.json?.error?.code} in ${dbDown.ms} ms; list ${dbDownList.status} in ${dbDownList.ms} ms`);
await sleep(8000);
dc('start mysql');
await waitFor(async () => (await rest(LB, '/api/chat/sessions', { token: alice.token })).status === 200, 120000, 2000);
await settle();
const c4done = await waitFor(() => jobRow(c4.json.jobId).status === 'done', 120000, 1000);
check('X-14', 'MySQL back: the job that was in flight is finished by itself, with exactly one reply', !!c4done && count('Messages', "Role='assistant' AND Content='stub reply to: mysql stops during this'") === 1, JSON.stringify(jobRow(c4.json.jobId)));
const afterDb = await dsend(alice, conv, 'chat after mysql returned', LB);
await waitFor(() => count('OutboxMessages', 'DispatchedAt IS NULL') === 0, 60000, 1000);
check('X-15', 'MySQL back: chat works, the outbox dispatcher is still alive and publishes the new event, and the invariants hold', afterDb.status === 201 && invariants('after C4').ok, invariants('after C4').text);

// =============== C5: everything restarts at once ===============
console.log('\n--- C5: every API replica, RabbitMQ and Redis restart at the same moment with work in flight ---');
await stub.control({ reset: true, delay_ms: 5000 });
const bigJobs = [];
for (let i = 0; i < 6; i++) bigJobs.push((await ask(alice, sess, `whole restart ${i}`, LB)).json.jobId);
for (let i = 0; i < 20; i++) await dsend(i % 2 ? alice : bob, conv, `whole restart direct ${i}`, LB);
await sleep(1500);
dc('restart api api2 api3 rabbitmq redis');
await settle();
const bigDone = await waitFor(() => bigJobs.every(id => jobRow(id).status === 'done'), 240000, 1000);
await waitFor(() => count('OutboxMessages', 'DispatchedAt IS NULL') === 0, 120000, 1000);
check('X-16', 'everything restarted at once: all 6 in-flight questions are answered, each exactly once', !!bigDone && bigJobs.every(id => count('AiReplyJobs', `Id='${id}' AND AssistantMessageId IS NOT NULL`) === 1));
check('X-17', 'and all invariants hold afterwards (nothing stuck, nothing waiting, nothing dead-lettered)', invariants('after C5').ok, invariants('after C5').text);
await refreshLb();
const hubB = await hub(bob, LB);
const live = await dsend(alice, conv, 'live push after the big restart', LB);
check('X-18', 'presence and live pushes work again after the big restart (a message reaches Bob over SignalR)', live.status === 201 && !!(await hubB.wait('MessageReceived', p => p.content === 'live push after the big restart', 30000)));
await hubA.close().catch(() => { }); await hubB.close().catch(() => { });

// =============== final consistency ===============
const finalInv = invariants('final');
check('X-19', 'FINAL CONSISTENCY after every failure above: no duplicate replies, no done job without a reply, no stuck job, no waiting event, empty dead-letter queues', finalInv.ok, finalInv.text);
const eventsPerMessage = Number(sql("SELECT COUNT(*) FROM DirectMessages m WHERE NOT EXISTS (SELECT 1 FROM OutboxMessages o WHERE o.Type='direct.message.sent' AND JSON_EXTRACT(o.Payload, '$.messageIds[0]') = m.Id)"));
check('X-20', 'FINAL: every direct message that exists has its "sent" event (the outbox never lost one, whatever failed)', eventsPerMessage === 0, `messages without a sent event: ${eventsPerMessage}`);
finish();
