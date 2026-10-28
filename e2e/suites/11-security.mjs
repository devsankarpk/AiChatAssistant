import fs from 'node:fs';
import path from 'node:path';
import { sh, dc, check, finish, sleep, waitFor, rest, newUser, newSession, newConv, hub, redisCli, cleanDb, launch, openPage, WEB, ROOT } from '../lib.mjs';

// ---- Phase 21: security hardening on the real, normal stack (one API behind the real nginx, Redis on, real AI service) ----
// Sections: A nginx headers  B the CSP in a real browser  C rate limit, real client IP, shared counter  D chat quota  E account lockout
//           F the login page under lockout  G typing flood  H clean-up. Limits are lowered per section by recreating the API with other
//           values (the same settings an operator would use) and put back to "effectively off" at the end for the suites that follow.
const OFF = { RATE_AUTH_PER_MINUTE: 100000, RATE_CHAT_PER_MINUTE: 100000, LOCKOUT_MAX_ATTEMPTS: 100000, LOCKOUT_MINUTES: 15 };
const shotsDir = path.join(ROOT, 'e2e', 'shots'); fs.mkdirSync(shotsDir, { recursive: true });

/** Starts the API again with different limits, then makes sure nginx points at the new container. */
async function recreateApi(env) {
  const envStr = Object.entries({ ...OFF, ...env }).map(([k, v]) => `${k}=${v}`).join(' ');
  sh(`${envStr} docker compose --env-file docker.env up -d --force-recreate api 2>&1 | tail -1`, { allowFail: true });
  sh('docker compose --env-file docker.env restart web 2>&1 | tail -1', { allowFail: true });
  return !!(await waitFor(async () => (await fetch(WEB + '/api/nothing-here').then(r => r.status).catch(() => 0)) === 404, 120000, 1000));
}
const raw = async (p, { method = 'GET', body, headers = {}, token } = {}) => {
  const r = await fetch(WEB + p, { method, headers: { 'content-type': 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}), ...headers }, body: body ? JSON.stringify(body) : undefined });
  let json = null; try { json = await r.json(); } catch { /* no body */ }
  return { status: r.status, json, h: r.headers };
};
const login = (email, password, headers) => raw('/api/auth/login', { method: 'POST', body: { email, password }, headers });

cleanDb(); redisCli('flushall');
const stamp = Date.now();
// People are created now, while the limits are effectively off, so the sections below can lower them and still have tokens to use.
const alice = await newUser('Sec Alice', WEB), bob = await newUser('Sec Bob', WEB), carol = await newUser('Sec Carol', WEB), dave = await newUser('Sec Dave', WEB);
const carolSession = await newSession(carol, WEB);
const conv = await newConv(alice, bob, WEB);

console.log('\n--- A: security headers from the real nginx ---');
const home = await raw('/');
const csp = home.h.get('content-security-policy') ?? '';
check('S21-01', 'the home page carries a Content-Security-Policy that allows scripts only from this site, forbids framing, plugins and foreign forms', /script-src 'self'(;|$)/.test(csp) && /frame-ancestors 'none'/.test(csp) && /object-src 'none'/.test(csp) && /form-action 'self'/.test(csp) && !/script-src[^;]*unsafe-inline/.test(csp), csp.slice(0, 160));
check('S21-02', 'the other headers are there: nosniff, X-Frame-Options DENY, a referrer policy, a permissions policy, same-origin opener policy', home.h.get('x-content-type-options') === 'nosniff' && home.h.get('x-frame-options') === 'DENY' && /strict-origin/.test(home.h.get('referrer-policy') ?? '') && /camera=\(\)/.test(home.h.get('permissions-policy') ?? '') && home.h.get('cross-origin-opener-policy') === 'same-origin');
check('S21-03', 'nginx does not announce its version (Server: nginx, no number)', /^nginx$/i.test(home.h.get('server') ?? ''), `Server: ${home.h.get('server')}`);
const assetName = (await (await fetch(WEB + '/')).text()).match(/src="(main-[^"]+\.js)"/)?.[1];
const asset = await fetch(`${WEB}/${assetName}`), deep = await fetch(WEB + '/reset-password?token=x');
check('S21-04', 'the headers are also on static files and on SPA deep links (locations that set their own Cache-Control do not inherit server-level headers)', asset.headers.get('x-content-type-options') === 'nosniff' && /script-src 'self'/.test(asset.headers.get('content-security-policy') ?? '') && /script-src 'self'/.test(deep.headers.get('content-security-policy') ?? ''), `asset ${assetName}`);
const api = await raw('/api/chat/sessions', { token: alice.token });
check('S21-05', 'API answers carry each header once (nginx hides the API\'s copies and sets its own) and are never cached', api.h.get('x-content-type-options') === 'nosniff' && api.h.get('x-frame-options') === 'DENY' && /no-store/.test(api.h.get('cache-control') ?? '') && /script-src 'self'/.test(api.h.get('content-security-policy') ?? ''), `x-content-type-options: "${api.h.get('x-content-type-options')}", cache-control: "${api.h.get('cache-control')}"`);
const http = await raw('/api/chat/sessions', { token: alice.token }), https = await raw('/api/chat/sessions', { token: alice.token, headers: { 'X-Forwarded-Proto': 'https' } });
check('S21-06', 'HSTS is sent only when the request came in over HTTPS at a proxy in front (plain local HTTP is never pinned to HTTPS)', !http.h.get('strict-transport-security') && /max-age=31536000/.test(https.h.get('strict-transport-security') ?? ''), `http: "${http.h.get('strict-transport-security')}", via https proxy: "${https.h.get('strict-transport-security')}"`);
const t = dc(`exec -T web sh -c 'nginx -t 2>&1'`, { allowFail: true }); // nginx prints its verdict on stderr; sh() only returns stdout on success
check('S21-07', 'the nginx configuration in the running container is valid (nginx -t)', /test is successful/.test(t), t.split('\n').pop());

console.log('\n--- B: the policy in a real browser ---');
const browser = await launch();
const page = await openPage(browser, { bypassCsp: false });
await page.evaluateOnNewDocument(() => { window.__csp = []; document.addEventListener('securitypolicyviolation', e => window.__csp.push(`${e.violatedDirective} ${e.blockedURI}`)); });
const consoleCsp = []; page.on('console', m => { if (/Content Security Policy|Refused to/i.test(m.text())) consoleCsp.push(m.text().slice(0, 200)); });
const reg = { name: 'Csp Browser', email: `csp-${stamp}@example.com` };
await page.goto(WEB + '/register'); await page.type('input[type=text]', reg.name); await page.type('input[type=email]', reg.email); await page.type('#register-password', 'Passw0rd!x');
await page.click('button[type=submit]'); await page.waitForSelector('#composer-input', { timeout: 20000 });
const regUser = JSON.parse(await page.evaluate(() => localStorage.getItem('aichat.auth.user')));
await page.goto(WEB + '/messages'); await sleep(2500);
await page.goto(WEB + '/appointments'); await sleep(800);
await page.goto(WEB + '/'); await page.waitForSelector('#composer-input'); await sleep(800);
const violations = await page.evaluate(() => window.__csp);
check('S21-08', 'registering, the chat page, Messages and Appointments all load and work under the policy with no CSP violation (scripts, styles, fonts, REST)', violations.length === 0 && consoleCsp.length === 0, [...violations, ...consoleCsp].join(' | ').slice(0, 200) || 'no violations');
check('S21-09', 'the SignalR WebSocket connects under connect-src (the hub logged this person\'s connection)', new RegExp(`Hub connected: user ${regUser.id},`).test(dc('logs --no-log-prefix api', { allowFail: true })), `user ${regUser.id}`);
const injected = await page.evaluate(() => { const s = document.createElement('script'); s.textContent = 'window.__pwned = 1'; document.body.appendChild(s); return window.__pwned === 1; });
await sleep(300);
const after = await page.evaluate(() => window.__csp);
check('S21-10', 'negative control: a script injected into the page does NOT run, and the browser reports the violation (the policy is really enforced)', injected === false && after.some(v => v.startsWith('script-src')), after.join(' | ').slice(0, 160));
const attr = await page.evaluate(() => new Promise(res => { const b = document.createElement('button'); b.setAttribute('onclick', 'window.__clicked = 1'); document.body.appendChild(b); b.click(); setTimeout(() => res(window.__clicked === 1), 100); }));
check('S21-11', 'negative control: an inline event handler (onclick="...") does not run either', attr === false);
const framer = await browser.newPage();
await framer.setContent(`<iframe id="f" src="${WEB}/login" width="400" height="300"></iframe>`); await sleep(2500);
const framed = framer.frames().find(f => f !== framer.mainFrame());
const framedOk = framed ? await framed.evaluate(() => document.body?.innerText ?? '').catch(() => '') : '';
check('S21-12', 'the app cannot be put in a frame on another site (clickjacking): the framed page stays empty / blocked', !/Log in/.test(framedOk), `framed text: "${framedOk.slice(0, 40)}"`);
await framer.close();
await page.close();
await browser.close(); // a fresh browser is started for section F: this one would sit idle for minutes while the API is restarted

console.log('\n--- C: rate limit on sign-in (5 per minute here), the real client address, a counter shared through Redis ---');
redisCli('flushall');
const lowAuth = await recreateApi({ RATE_AUTH_PER_MINUTE: 5 });
check('S21-13', 'the API restarts with a limit of 5 auth requests per minute', lowAuth);
const statuses = [];
let sixth = null;
for (let i = 1; i <= 6; i++) {
  // A bot that rotates a fake X-Forwarded-For on every request, hoping to look like a new client each time.
  const r = await login(alice.email, 'wrong-password', { 'X-Forwarded-For': `1.2.3.${i}` });
  statuses.push(r.status); if (i === 6) sixth = r;
}
check('S21-14', 'five wrong sign-ins are 401, the sixth is 429 (rate_limited) even though every request pretended to come from a different address', statuses.slice(0, 5).every(s => s === 401) && statuses[5] === 429 && sixth.json?.error?.code === 'rate_limited', `statuses: ${statuses.join(' ')}`);
const retryAfter = Number(sixth.h.get('retry-after'));
check('S21-15', 'the 429 has the standard error shape, a Retry-After of at most a minute, and the limit headers', retryAfter >= 1 && retryAfter <= 60 && sixth.h.get('ratelimit-limit') === '5' && sixth.h.get('ratelimit-remaining') === '0' && /Try again in/.test(sixth.json?.error?.message ?? ''), `Retry-After ${retryAfter}s: "${sixth.json?.error?.message}"`);
const keys = redisCli(`--scan --pattern 'ratelimit:auth:*'`).split('\n').filter(Boolean);
const webIp = sh(`docker inspect aichat-web-1 -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}'`, { allowFail: true }).trim();
check('S21-16', 'the counter lives in Redis, under the real client address (not "unknown", not nginx\'s own address, not the spoofed one)', keys.length === 1 && !/unknown|1\.2\.3\./.test(keys[0]) && (!webIp || !keys[0].endsWith(webIp)), `${keys.join(', ')} (nginx is ${webIp})`);
check('S21-17', 'its value is the number of requests and it expires by itself within the window', Number(redisCli(`get ${keys[0]}`)) >= 6 && Number(redisCli(`pttl ${keys[0]}`)) > 0 && Number(redisCli(`pttl ${keys[0]}`)) <= 60000, `count ${redisCli(`get ${keys[0]}`)}, ttl ${redisCli(`pttl ${keys[0]}`)} ms`);
const stillBlocked = await recreateApi({ RATE_AUTH_PER_MINUTE: 5 });
const afterRestart = stillBlocked ? await login(alice.email, 'wrong-password') : { status: 0 };
check('S21-18', 'after the API is restarted the address is STILL blocked: the count is shared and survives the process (a restart is not a way around the limit)', afterRestart.status === 429, `status ${afterRestart.status}`);
const seen = []; const waitStart = Date.now();
const waited = await waitFor(async () => { const r = await login(alice.email, 'wrong-password'); seen.push(r.status); return r.status === 401; }, 90000, 2000);
const rlKey = redisCli(`--scan --pattern 'ratelimit:auth:*'`).split('\n').filter(Boolean)[0];
check('S21-19', 'when the minute is over the address may try again (401, not 429)', !!waited, `after ${Math.round((Date.now() - waitStart) / 1000)} s of polling; statuses seen: ${[...new Set(seen)].join(',')}; key ${rlKey ?? '(gone)'} ttl ${rlKey ? redisCli(`pttl ${rlKey}`) : '-'} ms`);
const apiLogs = dc('logs --no-log-prefix api', { allowFail: true });
check('S21-20', 'the container log no longer warns "Failed to determine the https port for redirect"', !/Failed to determine the https port/.test(apiLogs));
check('S21-21', 'the API log records the refusals with the policy and path but no email or password', /Rate limit exceeded: policy Auth, POST \/api\/auth\/login/.test(apiLogs) && !apiLogs.includes(alice.email) && !/wrong-password/.test(apiLogs));

console.log('\n--- D: chat quota per user (2 per minute here) ---');
redisCli('flushall');
check('S21-22', 'the API restarts with a chat quota of 2 per minute', await recreateApi({ RATE_CHAT_PER_MINUTE: 2 }));
const q1 = await rest(WEB, `/api/chat/sessions/${carolSession}/messages`, { method: 'POST', token: carol.token, body: { content: 'Reply with the single word: one' } });
const q2 = await rest(WEB, `/api/chat/sessions/${carolSession}/messages`, { method: 'POST', token: carol.token, body: { content: 'Reply with the single word: two' } });
const q3 = await rest(WEB, `/api/chat/sessions/${carolSession}/messages`, { method: 'POST', token: carol.token, body: { content: 'this third question must be refused' } });
check('S21-23', 'two questions are answered by the real AI (200), the third in the same minute is 429', q1.status === 200 && q2.status === 200 && q3.status === 429 && q3.json?.error?.code === 'rate_limited', `statuses ${q1.status} ${q2.status} ${q3.status}`);
const saved = (await rest(WEB, `/api/chat/sessions/${carolSession}/messages`, { token: carol.token })).json ?? [];
check('S21-24', 'the refused question was not saved and cost no AI call (4 messages: two questions and two answers)', saved.length === 4 && !saved.some(m => /third question/.test(m.content)), `${saved.length} messages`);
const other = await rest(WEB, `/api/chat/sessions`, { token: dave.token });
const daveSession = await newSession(dave, WEB);
const daveAsk = await rest(WEB, `/api/chat/sessions/${daveSession}/messages`, { method: 'POST', token: dave.token, body: { content: 'Reply with the single word: ok' } });
check('S21-25', 'the quota is per person: someone else is not affected, and reading chats is never limited', daveAsk.status === 200 && other.status === 200 && (await rest(WEB, `/api/chat/sessions/${carolSession}/messages`, { token: carol.token })).status === 200);

console.log('\n--- E: account lockout (4 failures here, 1 minute) ---');
redisCli('flushall');
check('S21-26', 'the API restarts with lockout after 4 failed logins for 1 minute', await recreateApi({ LOCKOUT_MAX_ATTEMPTS: 4, LOCKOUT_MINUTES: 1 }));
const seq = []; let fourth = null;
for (let i = 1; i <= 4; i++) { const r = await login(alice.email, 'wrong-password'); seq.push(r.status); if (i === 4) fourth = r; }
check('S21-27', 'three wrong passwords are 401, the fourth locks the account: 423 account_locked with a message and Retry-After', seq.slice(0, 3).every(s => s === 401) && seq[3] === 423 && fourth.json?.error?.code === 'account_locked' && Number(fourth.h.get('retry-after')) <= 60, `statuses ${seq.join(' ')}: "${fourth.json?.error?.message}"`);
const right = await login(alice.email, alice.password);
check('S21-28', 'while locked even the CORRECT password is refused', right.status === 423, `status ${right.status}`);
const ghost = [];
for (let i = 1; i <= 4; i++) ghost.push((await login(`nobody-${stamp}@example.com`, 'whatever')).status);
check('S21-29', 'an email with no account behaves exactly the same (401, 401, 401, 423): lockout cannot be used to find out who is registered', JSON.stringify(ghost) === JSON.stringify(seq), `known: ${seq.join(' ')} / unknown: ${ghost.join(' ')}`);
check('S21-30', 'other people are not affected: Bob logs in normally', (await login(bob.email, bob.password)).status === 200);
const lockKeys = redisCli(`--scan --pattern 'ratelimit:lockout:*'`).split('\n').filter(Boolean);
check('S21-31', 'the lock is kept in Redis under a hash: no email address appears in any key, and it expires by itself', lockKeys.length === 2 && lockKeys.every(k => !/example\.com|sec-|nobody/i.test(k)) && lockKeys.every(k => Number(redisCli(`pttl ${k}`)) > 0 && Number(redisCli(`pttl ${k}`)) <= 60000), lockKeys.join(', '));

console.log('\n--- F: what the person sees ---');
const browser2 = await launch();
const lp = await openPage(browser2, { bypassCsp: false });
await lp.goto(WEB + '/login'); await lp.type('input[type=email]', alice.email); await lp.type('#login-password', alice.password);
await lp.click('button[type=submit]'); await lp.waitForSelector('.form-error', { timeout: 10000 });
const msg = await lp.$eval('.form-error', e => e.innerText);
await lp.screenshot({ path: path.join(shotsDir, 'phase21-lockout.png') });
check('S21-32', 'the login page shows the lock message in plain words, with the time to wait', /Too many failed login attempts/.test(msg) && /minute/.test(msg), `"${msg}"`);
await lp.close();
const unlocked = await waitFor(async () => (await login(alice.email, alice.password)).status === 200, 80000, 2000);
check('S21-33', 'after the minute is over the right password works again, and the count is cleared', !!unlocked);
const fresh = [];
for (let i = 0; i < 3; i++) fresh.push((await login(alice.email, 'wrong-again')).status);
check('S21-34', 'a successful login cleared the count: three new mistakes are plain 401s, not an immediate lock', fresh.every(s => s === 401), fresh.join(' '));

console.log('\n--- G: a flood of typing signals ---');
await recreateApi({});
const hA = await hub(alice, WEB), hB = await hub(bob, WEB);
await sleep(500);
for (let i = 0; i < 20; i++) await hA.conn.invoke('Typing', conv);
await sleep(700);
check('S21-35', '20 typing signals in a burst from one connection reach the other person once, not 20 times', hB.of('PeerTyping').length === 1, `${hB.of('PeerTyping').length} received`);
await sleep(1200);
await hA.conn.invoke('Typing', conv);
check('S21-36', 'after a second the next typing signal goes through (the browser\'s normal pace is never affected)', !!(await hB.wait('PeerTyping', () => true, 3000)) && hB.of('PeerTyping').length === 2, `${hB.of('PeerTyping').length} received`);
await hA.close(); await hB.close();

console.log('\n--- H: clean-up ---');
await browser2.close();
check('S21-37', 'the API is back on the standard configuration for the suites that follow', await recreateApi({}));
finish();
