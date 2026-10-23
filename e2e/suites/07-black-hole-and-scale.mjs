import { COMPOSE, useReplicas, dc, sh, check, finish, sleep, waitFor, rest, newUser, newSession, ask, messages, jobs, hub, stub, mgmt, queues, depth, purgeAll, sql, cleanDb, healthy, apiLogs } from '../lib.mjs';

// ---- Gap closing: a black-holed broker and Redis, three replicas, a 3,000-event backlog, a slow AI call ----
const PORTS = [18081, 18082, 18083];
const R1 = PORTS[0], R2 = PORTS[1], R3 = PORTS[2];
const count = (table, where = '1=1') => Number(sql(`SELECT COUNT(*) FROM ${table} WHERE ${where}`));
const jobRow = (id) => { const [status, attempts, hasReply] = sql(`SELECT Status, Attempts, AssistantMessageId IS NOT NULL FROM AiReplyJobs WHERE Id='${id}'`).split('\t'); return { status, attempts: Number(attempts), hasReply: hasReply === '1' }; };
const allHealthy = async () => (await Promise.all(PORTS.map(healthy))).every(Boolean);
const newConv = async (a, b, port = R1) => (await rest(port, '/api/direct/conversations', { method: 'POST', token: a.token, body: { userId: b.id } })).json.id;
const dsend = (user, conv, content, port = R1) => rest(port, `/api/direct/conversations/${conv}/messages`, { method: 'POST', token: user.token, body: { content } });
const TAP = 'test.tap';
const tapPeek = async (n = 1000) => (await mgmt(`/queues/%2F/${TAP}/get`, { method: 'POST', body: { count: n, ackmode: 'ack_requeue_true', encoding: 'auto' } })).json ?? [];
const tapCount = async () => (await mgmt(`/queues/%2F/${TAP}`)).json?.messages ?? 0;
async function pool(items, size, fn) { const out = []; let i = 0; await Promise.all(Array.from({ length: size }, async () => { while (i < items.length) { const k = i++; out[k] = await fn(items[k], k); } })); return out; }
const start = new Date().toISOString();
const logLines = (re) => apiLogs(start).split('\n').filter(l => re.test(l));

await waitFor(async () => { try { return (await mgmt('/overview')).status === 200; } catch { return false; } }, 90000, 1000);
await useReplicas(['api', 'api2', 'api3']);
cleanDb(); purgeAll();
await stub.control({ reset: true, delay_ms: 0, fail_next: 0, fail_always: false });
await waitFor(allHealthy, 90000, 1000);
await mgmt(`/queues/%2F/${TAP}`, { method: 'DELETE' });
await mgmt(`/queues/%2F/${TAP}`, { method: 'PUT', body: { durable: true, auto_delete: false } });
await mgmt(`/bindings/%2F/e/direct.events/q/${TAP}`, { method: 'POST', body: { routing_key: '#' } });
check('P20-01', 'three API replicas (ports 18081, 18082, 18083) are up and healthy behind one MySQL, Redis and RabbitMQ', await allHealthy());

const alice = await newUser('Alice'), bob = await newUser('Bob');
const sess = await newSession(alice);
const conv = await newConv(alice, bob);

// =============== A: RabbitMQ is black-holed (paused: connections open, nobody answers) ===============
console.log('\n--- A: RabbitMQ is black-holed ---');
dc('pause rabbitmq');
const timed = async (fn) => { const t = Date.now(); const r = await fn(); return { ...r, took: Date.now() - t }; };
const msgsBefore = count('Messages', `SessionId='${sess}'`);
const holeAsk = await timed(() => ask(alice, sess, 'sent into a black hole', R1));
check('P20-02', 'async send with the broker black-holed: a prompt 503 "ai_service_unavailable" (not a 500, not a 20 s wait), the question stays saved, no orphan job', holeAsk.status === 503 && holeAsk.json?.error?.code === 'ai_service_unavailable' && holeAsk.took < 12000 && count('Messages', `SessionId='${sess}'`) === msgsBefore + 1 && count('AiReplyJobs') === 0, `status ${holeAsk.status} after ${holeAsk.took} ms`);
const knownForgot = await timed(() => rest(R2, '/api/auth/forgot-password', { method: 'POST', body: { email: alice.email } }));
const unknownForgot = await timed(() => rest(R2, '/api/auth/forgot-password', { method: 'POST', body: { email: 'nobody-here@example.com' } }));
check('P20-03', 'forgot-password with the broker black-holed: the SAME generic 200 for a registered and an unknown email, promptly (no 500 that would reveal accounts)', knownForgot.status === 200 && unknownForgot.status === 200 && JSON.stringify(knownForgot.json) === JSON.stringify(unknownForgot.json) && knownForgot.took < 12000, `known ${knownForgot.status} in ${knownForgot.took} ms, unknown ${unknownForgot.status} in ${unknownForgot.took} ms`);
const holeSends = await pool(Array.from({ length: 30 }, (_, i) => i), 5, (i) => timed(() => dsend(alice, conv, `chat during the black hole ${i}`, PORTS[i % 3])));
check('P20-04', 'direct chat keeps working while the broker is black-holed: 30 messages all 201, each fast (the outbox only writes to MySQL)', holeSends.every(r => r.status === 201) && Math.max(...holeSends.map(r => r.took)) < 3000, `slowest ${Math.max(...holeSends.map(r => r.took))} ms`);
check('P20-05', 'their 30 events wait in the database (undispatched) and every replica stays healthy', count('OutboxMessages', 'DispatchedAt IS NULL') >= 30 && await allHealthy(), `waiting events: ${count('OutboxMessages', 'DispatchedAt IS NULL')}`);
dc('unpause rabbitmq');
const drainedA = await waitFor(() => count('OutboxMessages', 'DispatchedAt IS NULL') === 0, 120000, 1000);
check('P20-06', 'broker answers again: every waiting event is published by itself (no restart)', !!drainedA);
const consumersBack = await waitFor(() => { try { return queues()['ai.reply']?.c === 3; } catch { return false; } }, 90000, 1000);
const hubA = await hub(alice, R2);
const okAsk = await ask(alice, sess, 'after the black hole', R1);
check('P20-07', 'all three replicas re-attach their reply workers, and a new question is answered normally', !!consumersBack && okAsk.status === 202 && !!(await hubA.wait('AssistantReplied', p => p.jobId === okAsk.json.jobId, 30000)), JSON.stringify(queues()['ai.reply']));

// =============== B: Redis is black-holed ===============
console.log('\n--- B: Redis is black-holed ---');
dc('pause redis');
const redisSend = await timed(() => dsend(alice, conv, 'while redis is black-holed', R1));
const redisList = await timed(() => rest(R3, '/api/direct/conversations', { token: alice.token }));
check('P20-08', 'Redis black-holed: a direct message is still saved (201) and the list still loads, each within a few seconds (2 s Redis timeouts), never hanging', redisSend.status === 201 && redisList.status === 200 && redisSend.took < 10000 && redisList.took < 10000, `send ${redisSend.took} ms, list ${redisList.took} ms`);
const redisAsk = await ask(alice, sess, 'answered while redis is black-holed', R2);
const savedReply = await waitFor(() => redisAsk.json?.jobId && jobRow(redisAsk.json.jobId).status === 'done', 40000, 500);
check('P20-09', 'Redis black-holed: an async reply is still produced and saved (only the live push is lost)', !!savedReply);
check('P20-10', 'Redis black-holed: /health stays 200 on all three replicas', await allHealthy());
dc('unpause redis');
const tUnpause = Date.now();
await sleep(2000);
const hubB1 = await hub(bob, R3);
let pushBack = null;
for (let i = 0; i < 20 && !pushBack; i++) {
  const text = `push probe ${i}`;
  await dsend(alice, conv, text, R1);
  pushBack = await hubB1.wait('MessageReceived', p => p.content === text, 3000);
}
const recoverSecs = (Date.now() - tUnpause) / 1000;
check('P20-11', 'Redis answers again: pushes cross replicas again with no restart and no reconnect, within 30 s of it answering (the keep-alive ping notices a silent connection in seconds, not the library\'s default minute)', !!pushBack && recoverSecs <= 30, pushBack ? `first cross-replica push ${recoverSecs.toFixed(1)} s after Redis was unpaused` : 'no push within 60 s');
await hubB1.close(); await hubA.close();

// =============== C: three replicas ===============
console.log('\n--- C: three replicas working together ---');
await stub.control({ reset: true, delay_ms: 1500 });
const folks = [];
for (let i = 0; i < 6; i++) { const u = await newUser(`Tri${i}`); folks.push({ u, s: await newSession(u), h: await hub(u, PORTS[i % 3]) }); }
const triJobs = [];
await Promise.all(folks.flatMap((p, pi) => Array.from({ length: 10 }, (_, k) => ask(p.u, p.s, `tri ${pi}-${k}`, PORTS[(pi + k) % 3]).then(r => triJobs.push(r.json.jobId)))));
const triDone = await waitFor(() => count('AiReplyJobs', "Status='done' AND AssistantMessageId IS NOT NULL") >= 60 + 2, 120000, 500);
await sleep(1500);
const triStats = await stub.stats();
check('P20-12', '60 questions over three replicas: all answered, the AI called exactly 60 times, never more than 12 at once (prefetch 4 x 3) and more than 8 (all three replicas worked)', !!triDone && triStats.calls === 60 && triStats.max_concurrent <= 12 && triStats.max_concurrent > 8, `calls ${triStats.calls}, max concurrent ${triStats.max_concurrent}`);
check('P20-13', 'every person received exactly one AssistantReplied per question (10 each), on whichever replica they were connected to', folks.every(p => p.h.of('AssistantReplied').length === 10 && new Set(p.h.of('AssistantReplied').map(e => e.p.jobId)).size === 10));
for (const p of folks) await p.h.close();

const crowd = [];
for (let i = 0; i < 15; i++) { const u = await newUser(`Crowd${i}`); await newConv(alice, u); crowd.push(u); }
const hubAlice3 = await hub(alice, R1);
const tabs = await Promise.all(crowd.flatMap(u => PORTS.map(port => hub(u, port))));
await sleep(3000);
const onlineCounts = crowd.map(u => hubAlice3.of('PeerPresenceChanged', p => p.userId === u.id && p.online).length);
check('P20-14', '15 people each open a tab on all three replicas at the same instant: exactly ONE "came online" announcement per person', onlineCounts.every(n => n === 1), `counts: ${[...new Set(onlineCounts)].join('/')}`);
await Promise.all(tabs.map(t => t.close()));
await sleep(3500);
const offCounts = crowd.map(u => hubAlice3.of('PeerPresenceChanged', p => p.userId === u.id && !p.online).length);
check('P20-15', 'and exactly ONE "went offline" announcement per person after all three tabs close at the same instant', offCounts.every(n => n === 1), `counts: ${[...new Set(offCounts)].join('/')}`);
await hubAlice3.close();

await mgmt(`/queues/%2F/${TAP}/contents`, { method: 'DELETE' });
const rowsBeforeTri = count('OutboxMessages');
await pool(Array.from({ length: 150 }, (_, i) => i), 15, (i) => dsend(i % 2 ? alice : bob, conv, `tri direct ${i}`, PORTS[i % 3]));
await waitFor(() => count('OutboxMessages', 'DispatchedAt IS NULL') === 0, 60000, 500);
await sleep(2500);
const tapTri = await tapPeek(1000);
const idsTri = tapTri.map(m => Number(m.properties.message_id));
check('P20-16', '150 direct messages sent through three replicas: three dispatchers publish every event exactly once (no row published twice, none missing)', new Set(idsTri).size === idsTri.length && idsTri.length === count('OutboxMessages') - rowsBeforeTri, `${idsTri.length} published, ${new Set(idsTri).size} distinct, ${count('OutboxMessages') - rowsBeforeTri} new rows`);
check('P20-17', 'the subscriber, competing across three replicas, saw no repeats', logLines(/seen before/).length === 0 && depth('direct.events.dead') === 0);

await stub.control({ reset: true, delay_ms: 6000 });
const killJobs = [];
for (let k = 0; k < 9; k++) killJobs.push((await ask(alice, sess, `three-way crash ${k}`, PORTS[k % 3])).json.jobId);
await waitFor(async () => (await stub.stats()).in_flight >= 3, 15000, 100);
dc('kill api2');
const killDone = await waitFor(() => killJobs.every(id => jobRow(id).status === 'done'), 150000, 1000);
check('P20-18', 'one of three replicas is killed in the middle of AI calls: the other two finish all 9 jobs, each with exactly one reply', !!killDone && killJobs.every(id => count('AiReplyJobs', `Id='${id}' AND AssistantMessageId IS NOT NULL`) === 1) && count('Messages', "Role='assistant' AND Content LIKE 'stub reply to: three-way crash %'") === 9);
dc('start api2');
await waitFor(allHealthy, 90000, 1000);

// =============== D: a large backlog ===============
console.log('\n--- D: 3,000 events pile up while RabbitMQ is down, then drain ---');
await mgmt(`/queues/%2F/${TAP}/contents`, { method: 'DELETE' });
await stub.control({ reset: true, delay_ms: 0 });
dc('stop rabbitmq');
const t0 = Date.now();
const big = await pool(Array.from({ length: 3000 }, (_, i) => i), 30, (i) => dsend(i % 2 ? alice : bob, conv, `backlog ${i}`, PORTS[i % 3]));
const sendSecs = (Date.now() - t0) / 1000;
const waiting = count('OutboxMessages', 'DispatchedAt IS NULL');
check('P20-19', 'RabbitMQ down: 3,000 messages are all accepted (201) while their events wait in the database', big.every(r => r.status === 201) && waiting >= 3000, `201s: ${big.filter(r => r.status === 201).length} in ${sendSecs.toFixed(0)} s, events waiting: ${waiting}`);
check('P20-20', 'the API stays healthy with that backlog', await allHealthy());
dc('start rabbitmq');
const t1 = Date.now();
const drained = await waitFor(() => count('OutboxMessages', 'DispatchedAt IS NULL') === 0, 300000, 2000);
const drainSecs = (Date.now() - t1) / 1000;
await sleep(5000);
const idsBig = [];
let seenTap = 0;
const tapTotal = await tapCount();
for (let round = 0; round < 6 && idsBig.length < tapTotal; round++) {
  const r = await mgmt(`/queues/%2F/${TAP}/get`, { method: 'POST', body: { count: 1000, ackmode: 'ack_requeue_false', encoding: 'auto' } });
  for (const m of r.json ?? []) idsBig.push(Number(m.properties.message_id));
}
check('P20-21', 'RabbitMQ back: the whole backlog drains by itself, none lost, none published twice', !!drained && new Set(idsBig).size === idsBig.length && idsBig.length >= 3000, `${idsBig.length} events (${new Set(idsBig).size} distinct) in about ${drainSecs.toFixed(0)} s`);
const handledBig = new Set(logLines(/Direct event direct\.message\.sent #\d+/).filter(l => !/seen before/.test(l)).map(l => l.match(/#(\d+)/)[1]));
check('P20-22', 'the subscriber handled every one of them (log lines carry ids only), and its queues are empty', handledBig.size >= 3000 && depth('direct.events.log') === 0 && depth('direct.events.dead') === 0, `${handledBig.size} distinct events logged`);

// =============== E: a slow AI call (longer than .NET's 60 s timeout) ===============
console.log('\n--- E: the AI answers after 65 s, longer than the 60 s the API waits ---');
await mgmt(`/queues/%2F/${TAP}`, { method: 'DELETE' });
sh(`T_LEASE=90 T_R0=2 T_R1=3 ${COMPOSE} up -d --force-recreate api api2 api3 2>&1 | tail -1`);
await waitFor(allHealthy, 120000, 1000);
await waitFor(() => { try { return queues()['ai.reply']?.c === 3; } catch { return false; } }, 60000, 1000);
await stub.control({ reset: true, delay_ms: 65000 });
const hubE = await hub(alice, R3);
const slow = await ask(alice, sess, 'a very slow question', R1);
const tSlow = Date.now();
const failedSlow = await hubE.wait('AssistantFailed', p => p.jobId === slow.json.jobId, 330000);
const slowSecs = (Date.now() - tSlow) / 1000;
const jSlow = jobRow(slow.json.jobId);
const stSlow = await stub.stats();
check('P20-23', 'an AI call that outlasts the 60 s timeout counts as a failed attempt: after 3 attempts the person gets AssistantFailed (about 3 minutes)', !!failedSlow && jSlow.status === 'failed' && jSlow.attempts === 3 && slowSecs > 170, `after ${slowSecs.toFixed(0)} s, attempts ${jSlow.attempts}`);
check('P20-24', 'with the lease (90 s) longer than the AI timeout (60 s) no other worker steals the job: the AI was called exactly 3 times, once per attempt, and no reply was saved', stSlow.calls === 3 && !jSlow.hasReply, `AI calls ${stSlow.calls}`);
await hubE.close();
await stub.control({ reset: true, delay_ms: 0 });

await mgmt(`/queues/%2F/${TAP}`, { method: 'DELETE' });
finish();
