import { COMPOSE, dc, sh, check, finish, sleep, waitFor, rest, newUser, newSession, ask, messages, jobs, hub, stub, peek, publishRaw, useReplicas, queues, depth, purgeAll, sql, cleanDb, healthy, apiLogs, R1, R2 } from '../lib.mjs';

// ---- Phase 18: async AI replies, against the real Docker stack: 2 API replicas, real RabbitMQ, Redis, MySQL; a stub stands in for the Python service ----
const jobRow = (id) => { const [status, attempts, hasReply] = sql(`SELECT Status, Attempts, AssistantMessageId IS NOT NULL FROM AiReplyJobs WHERE Id='${id}'`).split('\t'); return { status, attempts: Number(attempts), hasReply: hasReply === '1' }; };
const count = (table, where = '1=1') => Number(sql(`SELECT COUNT(*) FROM ${table} WHERE ${where}`));
const healthyBoth = async () => (await healthy(R1)) && (await healthy(R2));

await useReplicas(['api', 'api2']); // these checks assume exactly two replicas
cleanDb(); purgeAll();
await waitFor(healthyBoth, 60000, 500);
await stub.control({ reset: true, delay_ms: 0, fail_next: 0, fail_always: false });
check('P18-01', 'two API replicas, RabbitMQ, Redis, MySQL and the stub AI service are up; async replies are on (Chat:AsyncReplies=true)', (await healthyBoth()) && (await stub.stats()).calls === 0);

const alice = await newUser('Alice'), bob = await newUser('Bob');
const sess = await newSession(alice);

// =============== A: the happy path ===============
console.log('\n--- A: the send request answers at once; the reply arrives later over SignalR (on the other replica) ---');
await stub.control({ delay_ms: 3000 });
const hubAlice = await hub(alice, R2, 'alice@r2');
const t0 = Date.now();
const r1 = await ask(alice, sess, 'What is DNS?', R1);
check('P18-02', 'POST a message: 202 with replyStatus "pending" and a job id, in well under the 3 s the AI takes (the request does not wait)', r1.status === 202 && r1.json?.replyStatus === 'pending' && !!r1.json?.jobId && r1.ms < 1500, `status ${r1.status} in ${r1.ms} ms`);
const job1 = r1.json?.jobId;
const afterAsk = await messages(alice, sess);
const openJobs = await jobs(alice, sess);
check('P18-03', 'the question is saved at once (alone), and the chat lists the reply as pending', afterAsk.length === 1 && afterAsk[0].role === 'user' && openJobs?.length === 1 && openJobs[0].status === 'pending' && openJobs[0].jobId === job1, `messages=${afterAsk.length}, open jobs=${JSON.stringify(openJobs?.map(j => j.status))}`);
const replied = await hubAlice.wait('AssistantReplied', p => p.jobId === job1);
const took = Date.now() - t0;
check('P18-04', "Alice's browser (connected to the OTHER replica) receives AssistantReplied with the job, chat, question id and the reply", !!replied && replied.p.sessionId === sess && replied.p.userMessageId === r1.json.userMessage.id && replied.p.assistantMessage?.content === 'stub reply to: What is DNS?', replied ? `after ${took} ms: "${replied.p.assistantMessage.content}"` : 'no event');
check('P18-05', 'the reply took about as long as the AI did (3 s), proving the work happened in the background', took >= 2800, `${took} ms`);
const j = jobRow(job1);
check('P18-06', 'database: the job is done after 1 attempt and points at the reply; the chat has 2 messages and 1 usage log', j.status === 'done' && j.attempts === 1 && j.hasReply && count('Messages', `SessionId='${sess}'`) === 2 && count('UsageLogs', `SessionId='${sess}'`) === 1, JSON.stringify(j));
const after = await messages(alice, sess);
check('P18-07', 'reading the chat returns question then reply, and no open jobs remain', after.length === 2 && after[1].role === 'assistant' && (await jobs(alice, sess)).length === 0);
await stub.control({ delay_ms: 0 });
const r2 = await ask(alice, sess, 'And what is DHCP?', R2);
await hubAlice.wait('AssistantReplied', p => p.jobId === r2.json?.jobId);
const st = await stub.stats();
check('P18-08', 'the second question sent the first exchange as context (history of 2) and the AI was called exactly once per job', st.histories[st.histories.length - 1] === 2 && st.calls === 2, `histories=${JSON.stringify(st.histories)}, calls=${st.calls}`);
check('P18-09', 'the queues are empty afterwards (acknowledged; nothing in retry or dead-letter)', ['ai.reply', 'ai.dead'].every(n => depth(n) === 0) && Object.keys(queues()).filter(n => n.startsWith('ai.retry')).every(n => depth(n) === 0), JSON.stringify(queues()));

// =============== B: failures, retries, dead letter, manual retry ===============
console.log('\n--- B: the AI fails: retries after 2 s and 3 s, then failure, dead-letter, and a manual "Send again" ---');
await stub.control({ reset: true, delay_ms: 0, fail_next: 1 });
const rb = await ask(alice, sess, 'flaky question', R1);
const flaky = await hubAlice.wait('AssistantReplied', p => p.jobId === rb.json.jobId);
const jb = jobRow(rb.json.jobId);
check('P18-10', 'one failed AI call is retried by itself: the reply still arrives, attempt 2, no failure shown to anyone', !!flaky && jb.status === 'done' && jb.attempts === 2 && hubAlice.of('AssistantFailed', p => p.jobId === rb.json.jobId).length === 0 && (await stub.stats()).calls === 2, JSON.stringify(jb));

await stub.control({ reset: true, fail_always: true });
const tFail = Date.now();
const rc = await ask(alice, sess, 'doomed question', R1);
const failedEvt = await hubAlice.wait('AssistantFailed', p => p.jobId === rc.json.jobId, 30000);
const failAfter = (Date.now() - tFail) / 1000;
check('P18-11', 'when every attempt fails (waits of 2 s and 3 s) Alice is told with AssistantFailed, carrying a safe message and no internals', !!failedEvt && failAfter >= 5 && failedEvt.p.error?.code === 'ai_service_unavailable' && !/stub|502|upstream/i.test(failedEvt.p.error.message), failedEvt ? `after ${failAfter.toFixed(1)} s: "${failedEvt.p.error.message}"` : 'no event');
const jc = jobRow(rc.json.jobId);
check('P18-12', 'database: the job is failed after exactly 3 attempts (the first try plus two retries), with no reply; the AI was called 3 times', jc.status === 'failed' && jc.attempts === 3 && !jc.hasReply && (await stub.stats()).calls === 3, JSON.stringify(jc));
const dead = await peek('ai.dead');
check('P18-13', 'the dead-letter queue holds that one job, and the message contains only the job id (no question, no answer)', dead.length === 1 && JSON.parse(dead[0].payload).JobId === rc.json.jobId && Object.keys(JSON.parse(dead[0].payload)).length === 1, dead[0]?.payload);
const open = await jobs(alice, sess);
check('P18-14', 'after a reload the chat still shows that question with a failed reply (it survives the page)', open.some(x => x.jobId === rc.json.jobId && x.status === 'failed' && !!x.error));
const userMsgsBefore = count('Messages', `SessionId='${sess}' AND Role='user'`);
await stub.control({ reset: true, fail_always: false });
const retry = await rest(R2, `/api/chat/jobs/${rc.json.jobId}/retry`, { method: 'POST', token: alice.token });
const retried = await hubAlice.wait('AssistantReplied', p => p.jobId === rc.json.jobId);
const jr = jobRow(rc.json.jobId);
check('P18-15', 'Send again (retry): 202, the SAME job is run again and succeeds with no new question; attempts start over at 1', retry.status === 202 && !!retried && jr.status === 'done' && jr.attempts === 1 && count('Messages', `SessionId='${sess}' AND Role='user'`) === userMsgsBefore, JSON.stringify(jr));
check('P18-16', 'retrying a reply that is already delivered is 409; someone else retrying it is 404', (await rest(R1, `/api/chat/jobs/${rc.json.jobId}/retry`, { method: 'POST', token: alice.token })).status === 409 && (await rest(R1, `/api/chat/jobs/${rc.json.jobId}/retry`, { method: 'POST', token: bob.token })).status === 404);

// =============== C: idempotency and bad input ===============
console.log('\n--- C: duplicate and bad messages ---');
await stub.control({ reset: true });
const assistantBefore = count('Messages', `SessionId='${sess}' AND Role='assistant'`);
const dupPublish = await publishRaw('ai', 'reply', JSON.stringify({ JobId: rc.json.jobId }));
await sleep(3000);
check('P18-17', 'DUPLICATE DELIVERY: the broker delivers an already-finished job again: no second AI call, no second reply, no second usage log', dupPublish.status === 200 && (await stub.stats()).calls === 0 && count('Messages', `SessionId='${sess}' AND Role='assistant'`) === assistantBefore && hubAlice.of('AssistantReplied', p => p.jobId === rc.json.jobId).length === 1);
const deadBefore = depth('ai.dead');
await publishRaw('ai', 'reply', 'this is not json');
const garbage = await waitFor(() => depth('ai.dead') === deadBefore + 1, 8000, 250);
check('P18-18', 'an unreadable message is moved to the dead-letter queue, and the worker keeps going', !!garbage);
await publishRaw('ai', 'reply', JSON.stringify({ JobId: '11111111-2222-3333-4444-555555555555' }));
await sleep(1500);
check('P18-19', 'a message for a job that does not exist is dropped quietly (not retried forever, not dead-lettered)', depth('ai.reply') === 0 && depth('ai.dead') === deadBefore + 1);
const rd = await ask(alice, sess, 'still working?', R1);
check('P18-20', 'the worker is still healthy after those: a normal question gets its reply', !!(await hubAlice.wait('AssistantReplied', p => p.jobId === rd.json.jobId)));
check('P18-21', "another person cannot see this chat's jobs (404)", (await rest(R1, `/api/chat/sessions/${sess}/jobs`, { token: bob.token })).status === 404);

// =============== D: load across two replicas ===============
console.log('\n--- D: 40 questions at once from 8 people, spread over both replicas ---');
await stub.control({ reset: true, delay_ms: 1500 });
purgeAll();
const people = [];
for (let i = 0; i < 8; i++) { const u = await newUser(`Load${i}`); people.push({ u, s: await newSession(u), h: await hub(u, i % 2 ? R1 : R2) }); }
const jobIds = [];
await Promise.all(people.flatMap((p, pi) => Array.from({ length: 5 }, (_, k) => ask(p.u, p.s, `load ${pi}-${k}`, (pi + k) % 2 ? R1 : R2).then(r => { if (r.status === 202) jobIds.push({ id: r.json.jobId, who: pi }); }))));
const allDone = await waitFor(() => count('AiReplyJobs', "Status='done'") >= 40 + 5 /* earlier jobs: 5 done in A..C */, 90000, 500);
await sleep(1000);
const stD = await stub.stats();
check('P18-22', 'all 40 requests were accepted (202) and all 40 replies were produced', jobIds.length === 40 && !!allDone, `accepted ${jobIds.length}, done ${count('AiReplyJobs', "Status='done'")} in total`);
check('P18-23', 'every person received exactly one AssistantReplied per question (5 each), none missing, none doubled', people.every(p => p.h.of('AssistantReplied').length === 5 && new Set(p.h.of('AssistantReplied').map(e => e.p.jobId)).size === 5));
check('P18-24', 'the AI was called exactly 40 times: one call per job even with two competing workers', stD.calls === 40, `calls=${stD.calls}`);
check('P18-25', 'backpressure: never more than 8 AI calls at once (prefetch 4 per replica x 2), and more than 4 (both replicas worked)', stD.max_concurrent <= 8 && stD.max_concurrent > 4, `max concurrent = ${stD.max_concurrent}`);
check('P18-26', 'database: 40 more done jobs, each pointing at its own reply; replies = jobs; no job done twice', count('Messages', "Role='assistant' AND Content LIKE 'stub reply to: load %'") === 40 && count('UsageLogs') >= 40 && Number(sql("SELECT COUNT(*) FROM (SELECT AssistantMessageId FROM AiReplyJobs WHERE AssistantMessageId IS NOT NULL GROUP BY AssistantMessageId HAVING COUNT(*) > 1) d")) === 0);
for (const p of people) await p.h.close();

// =============== E: a replica dies while it holds jobs ===============
console.log('\n--- E: a replica is killed (SIGKILL) while it is in the middle of AI calls ---');
await stub.control({ reset: true, delay_ms: 8000 });
const hubE = await hub(alice, R2, 'alice@r2');
const eJobs = [];
for (let k = 0; k < 6; k++) { const r = await ask(alice, sess, `crash test ${k}`, k % 2 ? R1 : R2); eJobs.push(r.json.jobId); }
await waitFor(async () => (await stub.stats()).in_flight >= 2, 15000, 100);
dc('kill api');
const tKill = Date.now();
const recovered = await waitFor(() => eJobs.every(id => jobRow(id).status === 'done'), 120000, 1000);
const replies = eJobs.map(id => jobRow(id));
check('P18-27', 'killing one replica mid-work loses nothing: all 6 jobs still finish (the survivor takes over after the lease)', !!recovered, `${((Date.now() - tKill) / 1000).toFixed(0)} s after the kill`);
check('P18-28', 'each job has exactly ONE reply (no duplicates) even though some were started twice', eJobs.every(id => count('AiReplyJobs', `Id='${id}' AND AssistantMessageId IS NOT NULL`) === 1) && count('Messages', "Role='assistant' AND Content LIKE 'stub reply to: crash test %'") === 6, `attempts: ${replies.map(r => r.attempts).join(',')}`);
await waitFor(() => hubE.of('AssistantReplied', p => eJobs.includes(p.jobId)).length === 6, 10000, 100); // the push follows the database commit by a moment
check('P18-29', 'at least one job was taken over (attempts > 1) and Alice still got all 6 events', replies.some(r => r.attempts > 1) && hubE.of('AssistantReplied', p => eJobs.includes(p.jobId)).length === 6, `events ${hubE.of('AssistantReplied', p => eJobs.includes(p.jobId)).length}`);
dc('start api');
await waitFor(() => healthy(R1), 60000, 500);
await hubE.close();

// =============== F: the broker is down when someone sends ===============
console.log('\n--- F: RabbitMQ is down at the moment of sending ---');
await stub.control({ reset: true, delay_ms: 0 });
dc('stop rabbitmq');
const msgsBefore = count('Messages', `SessionId='${sess}'`), jobsBefore = count('AiReplyJobs');
const down = await ask(alice, sess, 'sent while the broker is down', R1);
check('P18-30', 'broker down: the answer is 503 "ai_service_unavailable" (not 500), and the question stays saved', down.status === 503 && down.json?.error?.code === 'ai_service_unavailable' && count('Messages', `SessionId='${sess}'`) === msgsBefore + 1, `status ${down.status} in ${down.ms} ms`);
check('P18-31', 'broker down: no orphan job is left behind', count('AiReplyJobs') === jobsBefore);
check('P18-32', 'broker down: the rest of the app works (reading the chat, /health)', (await messages(alice, sess)).length === msgsBefore + 1 && (await healthyBoth()));
dc('start rabbitmq');
const consumers = await waitFor(() => { try { return queues()['ai.reply']?.c === 2; } catch { return false; } }, 90000, 1000);
check('P18-33', 'broker back: both replicas re-attach their workers by themselves', !!consumers, JSON.stringify(queues()['ai.reply']));
const hubF = await hub(alice, R2, 'alice@r2');
const afterBroker = await ask(alice, sess, 'broker is back', R1);
check('P18-34', 'broker back: a new question is queued and answered normally', afterBroker.status === 202 && !!(await hubF.wait('AssistantReplied', p => p.jobId === afterBroker.json.jobId)));

// =============== G: Redis is down, so the push cannot be delivered ===============
console.log('\n--- G: Redis is down while a reply is produced: the push is lost, the reply is not ---');
dc('stop redis');
const gj = await ask(alice, sess, 'redis is down', R1);
await waitFor(() => jobRow(gj.json.jobId).status === 'done', 20000, 500);
await sleep(1500);
check('P18-35', 'Redis down: the reply is produced and saved (job done) even though no push can reach the browser', jobRow(gj.json.jobId).status === 'done' && hubF.of('AssistantReplied', p => p.jobId === gj.json.jobId).length === 0);
check('P18-36', 'Redis down: reading the chat returns the reply (this is what the page\'s 15 s safety net and reconnect refetch rely on)', (await messages(alice, sess)).some(m => m.content === 'stub reply to: redis is down'));
dc('start redis');
await waitFor(async () => sh(`${COMPOSE} exec -T redis redis-cli ping`, { allowFail: true }) === 'PONG', 30000, 500);
await sleep(6000);
const gj2 = await ask(alice, sess, 'redis is back', R1);
check('P18-37', 'Redis back: pushes work again without a reconnect', !!(await hubF.wait('AssistantReplied', p => p.jobId === gj2.json.jobId, 20000)));
await hubF.close();

// =============== H: flag off, flag on without a broker ===============
console.log('\n--- H: configuration ---');
const stubBefore = (await stub.stats()).calls;
sh(`T_ASYNC=false ${COMPOSE} up -d --force-recreate api api2 2>&1 | tail -1`);
await waitFor(healthyBoth, 90000, 1000);
const jobsHBefore = count('AiReplyJobs');
const sync = await ask(alice, sess, 'synchronous again', R1);
check('P18-38', 'Chat:AsyncReplies=false: the old behaviour is untouched: 200 with both messages in the same response, no job row, one AI call', sync.status === 200 && sync.json?.assistantMessage?.content === 'stub reply to: synchronous again' && count('AiReplyJobs') === jobsHBefore && (await stub.stats()).calls === stubBefore + 1);
check('P18-39', 'Chat:AsyncReplies=false: the open-jobs list is just empty and no AI-reply worker runs', (await jobs(alice, sess)).filter(x => x.status === 'pending').length === 0 && !/AI reply worker is consuming/.test(logsSince()));
function logsSince() { return dc('logs --no-log-prefix --since 40s api api2', { allowFail: true }); }
sh(`T_ASYNC=true T_RABBIT_HOST='' ${COMPOSE} up -d --force-recreate --no-deps api2 2>&1 | tail -1`, { allowFail: true });
await sleep(14000);
const refused = dc('logs --no-log-prefix --tail 40 api2', { allowFail: true });
check('P18-40', 'Chat:AsyncReplies=true without RabbitMq:Host: the API refuses to start and says why', /Chat:AsyncReplies is on but RabbitMq:Host is not set/.test(refused), refused.split('\n').find(l => /Chat:AsyncReplies/.test(l))?.slice(0, 140) ?? 'message not found');
sh(`${COMPOSE} up -d --force-recreate api api2 2>&1 | tail -1`, { allowFail: true });
await waitFor(healthyBoth, 90000, 1000);

finish();
