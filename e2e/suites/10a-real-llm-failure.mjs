import { COMPOSE, dc, sh, check, finish, sleep, waitFor, rest, newUser, newSession, ask, messages, jobs, hub, depth, purgeAll, sql, cleanDb } from '../lib.mjs';
// ---- Real LLM failure: the real Python service with an INVALID DeepInfra key (DeepInfra answers 401), then the right key ----
const W = 8080; // everything goes through nginx
const count = (table, where = '1=1') => Number(sql(`SELECT COUNT(*) FROM ${table} WHERE ${where}`));
const jobRow = (id) => { const [status, attempts, hasReply] = sql(`SELECT Status, Attempts, AssistantMessageId IS NOT NULL FROM AiReplyJobs WHERE Id='${id}'`).split('\t'); return { status, attempts: Number(attempts), hasReply: hasReply === '1' }; };
const apiReady = async () => !!(await waitFor(async () => (await fetch('http://localhost:8080/api/chat/sessions').catch(() => null))?.status === 401, 120000, 1000)); // 401 = the API is up and wants a token
// start from a stack whose provider key is INVALID (DeepInfra will answer 401) and whose replies are asynchronous
sh(`DEEPINFRA_API_KEY=invalid-key-for-test CHAT_ASYNC_REPLIES=true ${COMPOSE} up -d --force-recreate ai-service api 2>&1 | tail -1`);
await apiReady(); await sleep(12000);
cleanDb(); purgeAll();
const alice = await newUser('Alice', W); const sess = await newSession(alice, W);
const h = await hub(alice, W);
console.log('--- async mode, real Python service, INVALID DeepInfra key ---');
const t0 = Date.now();
const r = await ask(alice, sess, 'Reply with exactly the word: pong', W);
check('P20F-01', 'async send answers 202 at once even though the real AI provider will reject the request', r.status === 202 && r.ms < 2000, `status ${r.status} in ${r.ms} ms`);
const failed = await h.wait('AssistantFailed', p => p.jobId === r.json.jobId, 120000);
const secs = (Date.now() - t0) / 1000;
const j = jobRow(r.json.jobId);
check('P20F-02', "the provider's real rejection (401) is retried (5 s, then 20 s) and then reported as AssistantFailed with a safe message", !!failed && secs >= 25 && failed.p.error.code === 'ai_service_unavailable' && !/401|key|deepinfra|bearer|invalid/i.test(failed.p.error.message), failed ? `after ${secs.toFixed(0)} s: "${failed.p.error.message}"` : 'no event');
check('P20F-03', 'the job is failed after 3 attempts, no reply was invented, and the job id sits in the dead-letter queue', j.status === 'failed' && j.attempts === 3 && !j.hasReply && depth('ai.dead') === 1, JSON.stringify(j));
const errText = sql(`SELECT Error FROM AiReplyJobs WHERE Id='${r.json.jobId}'`);
check('P20F-04', 'what is stored for the operator does not contain the API key or any secret', !/Bearer|sk-|DEEPINFRA_API_KEY/i.test(errText), errText.slice(0, 120));
console.log('--- the key is fixed: Send again ---');
sh(`env -u DEEPINFRA_API_KEY CHAT_ASYNC_REPLIES=true ${COMPOSE} up -d --force-recreate ai-service 2>&1 | tail -1`); // the real key from docker.env again
await waitFor(async () => (await fetch('http://localhost:8080/api/chat/sessions', { headers: { authorization: `Bearer ${alice.token}` } }).then(x => x.status).catch(() => 0)) === 200, 60000, 1000);
await sleep(15000);
const questionsBefore = count('Messages', `SessionId='${sess}' AND Role='user'`);
const retry = await rest(W, `/api/chat/jobs/${r.json.jobId}/retry`, { method: 'POST', token: alice.token });
const ok = await h.wait('AssistantReplied', p => p.jobId === r.json.jobId, 90000);
check('P20F-05', 'with the right key, Send again (retry) gets a REAL reply from the real LLM for the same question, with no duplicate question', retry.status === 202 && !!ok && /pong/i.test(ok.p.assistantMessage.content) && count('Messages', `SessionId='${sess}' AND Role='user'`) === questionsBefore, ok ? `"${ok.p.assistantMessage.content.slice(0, 40)}"` : 'no reply');
await h.close();
console.log('--- synchronous mode (default), invalid key ---');
sh(`DEEPINFRA_API_KEY=invalid-key-for-test CHAT_ASYNC_REPLIES=false ${COMPOSE} up -d --force-recreate ai-service api 2>&1 | tail -1`);
await waitFor(async () => (await fetch('http://localhost:8080/api/chat/sessions', { headers: { authorization: `Bearer ${alice.token}` } }).then(x => x.status).catch(() => 0)) === 200, 90000, 1000);
await sleep(10000);
const sync = await ask(alice, sess, 'sync question with a bad key', W);
check('P20F-06', 'default mode with a bad key: the answer is 503 "ai_service_unavailable" (never a 500), the question is saved, and the message tells the person to try again', sync.status === 503 && sync.json?.error?.code === 'ai_service_unavailable' && count('Messages', `SessionId='${sess}' AND Content='sync question with a bad key'`) === 1, `status ${sync.status} in ${sync.ms} ms`);
sh(`env -u DEEPINFRA_API_KEY ${COMPOSE} up -d --force-recreate ai-service api 2>&1 | tail -1`);
await waitFor(async () => (await fetch('http://localhost:8080/api/chat/sessions', { headers: { authorization: `Bearer ${alice.token}` } }).then(x => x.status).catch(() => 0)) === 200, 90000, 1000);
await sleep(10000);
const back = await ask(alice, sess, 'Reply with exactly the word: pong', W);
check('P20F-07', 'with the right key restored the default mode answers normally again (200, real reply)', back.status === 200 && /pong/i.test(back.json?.assistantMessage?.content ?? ''), `status ${back.status}`);
finish();
