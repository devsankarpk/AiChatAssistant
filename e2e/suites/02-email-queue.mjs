import { bringUp, COMPOSE, dc, sh, check, finish, sleep, waitFor, rest, newUser, apiLogs, queues, depth, purgeAll, cleanDb, mgmt, mgmtReady, useReplicas, healthy, R1 } from '../lib.mjs';

// ---- Phase 17: password-reset email through RabbitMQ, against a real broker (one API replica) ----
const T0 = () => new Date().toISOString();
const dlq = async () => (await mgmt('/queues/%2F/email.dead/get', { method: 'POST', body: { count: 10, ackmode: 'ack_requeue_true', encoding: 'auto' } })).json ?? [];
async function reconfigure(vars) {
  const pre = Object.entries(vars).map(([k, v]) => `${k}='${v}'`).join(' ');
  if (!await bringUp('api', R1, { env: pre })) throw new Error('api did not become healthy');
  await sleep(2000);
}
const mkUser = (tag) => newUser(`Mailer ${tag}`);
const forgot = async (email) => { const t = Date.now(); const r = await rest(R1, '/api/auth/forgot-password', { method: 'POST', body: { email } }); return { ...r, ms: Date.now() - t }; };
const linkFor = (logs, email) => logs.split('\n').find(l => l.includes('[DEV EMAIL') && l.includes(`<${email}>`))?.match(/Link: (\S+)/)?.[1];

await mgmtReady();
await useReplicas(['api']);
cleanDb(); purgeAll();

// ======== S1: console provider, broker on: the happy path through the queue ========
console.log('\n--- S1: queue on, console provider (delivery succeeds) ---');
await reconfigure({ T_PROVIDER: 'console' });
let t = T0();
const known = await mkUser('s1'); const unknown = `nobody-${Date.now()}@example.com`;
const rKnown = await forgot(known.email); const rUnknown = await forgot(unknown);
check('E-01', 'same status and body for a registered and an unregistered email', rKnown.status === 200 && rUnknown.status === 200 && JSON.stringify(rKnown.json) === JSON.stringify(rUnknown.json), `${rKnown.status} / ${rUnknown.status}: ${rKnown.json?.message}`);
const got = await waitFor(() => linkFor(apiLogs(t), known.email), 15000);
check('E-02', "the worker delivered the registered user's email (via the queue)", !!got);
await sleep(1500);
check('E-03', 'no email was produced for the unregistered address', !apiLogs(t).includes(`<${unknown}>`));
const token = got ? decodeURIComponent(got.split('token=')[1]) : '';
const reset = await rest(R1, '/api/auth/reset-password', { method: 'POST', body: { token, newPassword: 'NewPassw0rd!y' } });
const loginNew = await rest(R1, '/api/auth/login', { method: 'POST', body: { email: known.email, password: 'NewPassw0rd!y' } });
const loginOld = await rest(R1, '/api/auth/login', { method: 'POST', body: { email: known.email, password: 'Passw0rd!x' } });
check('E-04', 'end to end: the emailed link resets the password (new works, old fails)', reset.status === 200 && loginNew.status === 200 && loginOld.status === 401, `reset ${reset.status}, new ${loginNew.status}, old ${loginOld.status}`);
check('E-05', 'queues are empty afterwards (acknowledged; nothing stuck in retry or dead-letter)', ['email.send', 'email.dead', 'email.retry.10s', 'email.retry.60s'].every(n => depth(n) === 0), JSON.stringify(queues()));
check('E-06', 'the worker holds exactly one consumer on email.send', queues()['email.send']?.c === 1);

// ======== S2: sender always fails: retries then dead-letter ========
console.log('\n--- S2: SMTP unconfigured (every send fails): retries after 2 s then 3 s, then dead-letter ---');
await reconfigure({ T_PROVIDER: 'smtp', T_MAIL_D0: 2, T_MAIL_D1: 3 });
t = T0();
const failUser = await mkUser('s2');
const start = Date.now();
const rFail = await forgot(failUser.email);
check('E-07', 'the request is not slowed or failed by the mail failure: 200 generic answer right away', rFail.status === 200 && rFail.ms < 1500, `${rFail.status} in ${rFail.ms} ms`);
const dead = await waitFor(() => depth('email.dead') === 1, 30000, 250);
const took = (Date.now() - start) / 1000;
check('E-08', 'after 3 failed attempts (waits 2 s and 3 s) the email lands in the dead-letter queue', !!dead && took >= 5, `dead-lettered after ${took.toFixed(1)} s (waits sum to 5 s)`);
const lg = apiLogs(t);
check('E-09', 'logs show attempt 1 of 3, attempt 2 of 3 retrying, then giving up on attempt 3 of 3', /attempt 1 of 3\); retrying in 2s/.test(lg) && /attempt 2 of 3\); retrying in 3s/.test(lg) && /attempt 3 of 3\); giving up/.test(lg));
const m0 = (await dlq())[0];
check('E-10', 'the dead-letter message carries the attempt count and the error, and the other queues are empty', !!m0 && m0.properties?.headers?.['x-attempt'] === 3 && /Smtp:Username/.test(m0.properties?.headers?.['x-error'] ?? '') && depth('email.send') === 0 && depth('email.retry.2s') === 0 && depth('email.retry.3s') === 0, m0 ? `x-attempt=${m0.properties.headers['x-attempt']}` : 'no message read');
check('E-11', 'the dead-letter message is persistent and holds the original payload', !!m0 && m0.properties?.delivery_mode === 2 && /"ToEmail":"/.test(m0.payload ?? ''));
check('E-12', 'the retry queues are named after their delays (email.retry.2s / email.retry.3s)', 'email.retry.2s' in queues() && 'email.retry.3s' in queues());
purgeAll();

// ======== S3: broker down / back ========
console.log('\n--- S3: broker stopped then started ---');
await reconfigure({ T_PROVIDER: 'console' });
const u3 = await mkUser('s3');
dc('stop rabbitmq');
t = T0();
const rDown = await forgot(u3.email); const rDownUnknown = await forgot(`ghost-${Date.now()}@example.com`);
check('E-13', 'broker down: the answer is still the same 200 generic message (no 500, no account revealed)', rDown.status === 200 && rDownUnknown.status === 200 && JSON.stringify(rDown.json) === JSON.stringify(rDownUnknown.json), `known ${rDown.status} in ${rDown.ms} ms, unknown ${rDownUnknown.status} in ${rDownUnknown.ms} ms`);
await sleep(500);
check('E-14', 'broker down: the failure is logged for the operator (by user id, not email)', /Could not send or queue the password reset email for user \d+/.test(apiLogs(t)) && !apiLogs(t).includes(u3.email.split('@')[0] + '@'));
check('E-15', 'broker down: the rest of the app keeps working (login 200)', (await rest(R1, '/api/auth/login', { method: 'POST', body: { email: u3.email, password: u3.password } })).status === 200);
dc('start rabbitmq');
await mgmtReady();
await waitFor(() => { try { return queues()['email.send']?.c === 1; } catch { return false; } }, 90000, 1000);
check('E-16', 'broker back: the worker reconnects by itself (a consumer is attached again, no API restart)', queues()['email.send']?.c === 1, JSON.stringify(queues()['email.send']));
t = T0();
await forgot(u3.email);
check('E-17', 'broker back: a new request is queued and delivered normally', !!(await waitFor(() => linkFor(apiLogs(t), u3.email), 20000)));

// ======== S4: durability ========
console.log('\n--- S4: a message waiting in a retry queue survives a broker restart and an API restart ---');
await reconfigure({ T_PROVIDER: 'smtp', T_MAIL_D0: 20, T_MAIL_D1: 20 });
purgeAll();
const u4 = await mkUser('s4');
await forgot(u4.email);
await waitFor(() => depth('email.retry.20s') === 1, 15000, 250);
check('E-18', 'after the first failure the email waits in email.retry.20s', depth('email.retry.20s') === 1, JSON.stringify(queues()));
dc('restart rabbitmq');
await mgmtReady();
await waitFor(() => { try { return depth('email.retry.20s') >= 0; } catch { return false; } }, 90000, 1000);
check('E-19', 'broker restarted: the waiting email is still there (durable queue, persistent message)', depth('email.retry.20s') + depth('email.send') + depth('email.dead') === 1, JSON.stringify(queues()));
dc('stop api'); await sleep(1000); dc('start api');
await waitFor(() => healthy(R1), 90000, 1000);
const s4dead = await waitFor(() => { try { return depth('email.dead') === 1; } catch { return false; } }, 90000, 1000);
check('E-20', 'API restarted too: the message is still processed to the end (nothing lost, nothing duplicated)', !!s4dead && depth('email.send') === 0 && depth('email.retry.20s') === 0, JSON.stringify(queues()));
purgeAll();

// ======== S5: message TTL ========
console.log('\n--- S5: a message older than its time-to-live is dropped, not sent (TTL 1 minute, retries after 30 s then 60 s) ---');
await reconfigure({ T_PROVIDER: 'smtp', T_MAIL_D0: 30, T_MAIL_D1: 60, T_MAIL_TTL: 1 });
t = T0();
const u5 = await mkUser('s5');
await forgot(u5.email);
const dropped = await waitFor(() => /Dropping an expired password reset email/.test(apiLogs(t)), 130000, 2000);
check('E-21', 'the stale message is dropped by the worker with a log line, never sent', !!dropped);
check('E-22', 'and nothing is left in any queue, including the dead-letter queue', ['email.send', 'email.dead', 'email.retry.30s', 'email.retry.60s'].every(n => depth(n) === 0), JSON.stringify(queues()));

// ======== S6: 40 at once ========
console.log('\n--- S6: 40 requests at the same time (console provider) ---');
await reconfigure({ T_PROVIDER: 'console' });
t = T0();
const emails = [];
for (let i = 0; i < 40; i += 8) emails.push(...await Promise.all(Array.from({ length: 8 }, (_, j) => mkUser(`s6-${i + j}`))));
const t6 = Date.now();
const answers = await Promise.all(emails.map(e => forgot(e.email)));
check('E-23', '40 simultaneous requests: all answered 200 with the same message', answers.every(a => a.status === 200 && a.json?.message === answers[0].json?.message), `slowest ${Math.max(...answers.map(a => a.ms))} ms`);
await waitFor(() => emails.every(e => linkFor(apiLogs(t), e.email)), 40000, 500);
const lg6 = apiLogs(t);
const delivered = emails.filter(e => lg6.split('\n').filter(l => l.includes('[DEV EMAIL') && l.includes(`<${e.email}>`)).length === 1).length;
check('E-24', 'every one of the 40 emails was delivered exactly once', delivered === 40, `${delivered}/40 delivered once each, in ${((Date.now() - t6) / 1000).toFixed(1)} s`);
check('E-25', 'queues drain to empty', ['email.send', 'email.dead'].every(n => depth(n) === 0), JSON.stringify(queues()));

// ======== S7: no broker configured ========
console.log('\n--- S7: no broker configured (RabbitMq:Host empty): inline path, exactly as before the broker ---');
await reconfigure({ T_PROVIDER: 'console', T_RABBIT_HOST: '', T_ASYNC: 'false' });
t = T0();
const u7 = await mkUser('s7');
const r7 = await forgot(u7.email);
const l7 = await waitFor(() => linkFor(apiLogs(t), u7.email), 10000);
check('E-26', 'no broker: the email is still produced (inline) and the answer is the same generic 200', r7.status === 200 && !!l7, `${r7.status} in ${r7.ms} ms`);
check('E-27', 'no broker: no worker starts and the app never tries to connect to RabbitMQ', !/Email worker/.test(apiLogs(t)) && !/RabbitMQ/i.test(apiLogs(t)));

// ======== S8: fail fast ========
console.log('\n--- S8: broker host set but password blank: fail fast ---');
sh(`T_PROVIDER=console T_RABBIT_PASS='' ${COMPOSE} up -d --force-recreate --no-deps api 2>&1 | tail -1`, { allowFail: true });
await sleep(12000);
const lg8 = apiLogs();
check('E-28', 'the API refuses to start with a clear message naming the missing setting', /RabbitMq__Password/.test(lg8) && !(await healthy(R1)), lg8.split('\n').find(l => /RabbitMq__Password/.test(l))?.slice(0, 160) ?? '');

// ======== S9: the broker deadline (a paused broker, found after Phase 18) ========
console.log('\n--- S9: a paused broker (connections open, nobody answers) ---');
await reconfigure({ T_PROVIDER: 'console' });
const u9 = await mkUser('s9');
dc('pause rabbitmq');
const hole = await forgot(u9.email);
const holeUnknown = await forgot(`ghost-${Date.now()}@example.com`);
dc('unpause rabbitmq');
check('E-29', 'a paused broker: forgot-password still gives the same generic 200 (never a 500 that would reveal accounts), within the deadline, not 20 s', hole.status === 200 && holeUnknown.status === 200 && JSON.stringify(hole.json) === JSON.stringify(holeUnknown.json) && hole.ms < 12000, `known ${hole.status} in ${hole.ms} ms, unknown ${holeUnknown.status} in ${holeUnknown.ms} ms`);
await mgmtReady();
await waitFor(() => { try { return queues()['email.send']?.c === 1; } catch { return false; } }, 90000, 1000);
const t9 = T0();
await forgot(u9.email);
check('E-30', 'and after it answers again the worker is attached and a new reset email is delivered', !!(await waitFor(() => linkFor(apiLogs(t9), u9.email), 20000)));

await reconfigure({ T_PROVIDER: 'smtp' }); // leave the stack in its normal configuration
finish();
