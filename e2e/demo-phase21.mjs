// Phase 21 demo: two real browser windows, two signed-in users, live messages between them, then the new protections:
// the security headers, a wrong-password lockout shown on the login page, and the sign-in rate limit.
// Run against the normal stack (docker compose up) after `./e2e/run.sh --keep 11`, or any running stack on :8080.
import fs from 'node:fs';
import path from 'node:path';
import { launch, openPage, sleep, waitFor, rest, newUser, newConv, redisCli, sh, WEB, ROOT } from './lib.mjs';

const shots = path.join(ROOT, 'e2e', 'shots', 'demo21'); fs.mkdirSync(shots, { recursive: true });
const snap = (p, n) => p.screenshot({ path: path.join(shots, n) }).then(() => console.log('   screenshot:', path.join(shots, n)));
const say = t => console.log('\n=== ' + t);
const login = async (page, u) => { await page.goto(WEB + '/login'); await page.type('input[type=email]', u.email); await page.type('#login-password', u.password); await page.click('button[type=submit]'); };
const bodyText = p => p.evaluate(() => document.body.innerText);
// All auth endpoints share one per-address bucket (10 per minute). The earlier steps used it up, so wait for the window to end before a step that needs a clean count.
const freshWindow = async (why) => {
  const key = redisCli(`--scan --pattern 'ratelimit:auth:*'`).split('\n').filter(Boolean)[0];
  const ttl = key ? Number(redisCli(`pttl ${key}`)) : 0;
  if (ttl > 0) { console.log(`   (waiting ${Math.ceil(ttl / 1000)} s for the one-minute rate-limit window to end, so ${why})`); await sleep(ttl + 700); }
};

redisCli('flushall');
const alice = await newUser('Alice Demo', WEB), bob = await newUser('Bob Demo', WEB);
await newConv(alice, bob, WEB);

const browser = await launch();
const a = await openPage(browser, { width: 1100, height: 760, bypassCsp: false }), b = await openPage(browser, { width: 1100, height: 760, bypassCsp: false });
for (const p of [a, b]) await p.evaluateOnNewDocument(() => { window.__cspv = []; document.addEventListener('securitypolicyviolation', e => window.__cspv.push(e.violatedDirective)); });

say('1. Two users log in, each in their own browser window');
await Promise.all([login(a, alice), login(b, bob)]);
await Promise.all([a.waitForSelector('#composer-input', { timeout: 20000 }), b.waitForSelector('#composer-input', { timeout: 20000 })]);
console.log(`   Alice (id ${alice.id}) and Bob (id ${bob.id}) are signed in`);

say('2. Live chat between them (Messages tab, over the SignalR WebSocket allowed by the new CSP)');
await Promise.all([a.goto(WEB + '/messages'), b.goto(WEB + '/messages')]);
await a.waitForSelector('.conversation'); await b.waitForSelector('.conversation');
await a.click('.conversation'); await sleep(800);
await a.waitForSelector('#direct-composer');
await a.type('#direct-composer', 'Hi Bob, this is Alice - Phase 21 demo'); await a.keyboard.press('Enter');
const got = await waitFor(async () => /Phase 21 demo/.test(await bodyText(b)) || (await b.$('.conversation .unread, .badge')) !== null, 15000, 300);
await b.click('.conversation'); await sleep(800);
console.log('   Bob received it live:', /Phase 21 demo/.test(await bodyText(b)));
await b.waitForSelector('#direct-composer');
await b.type('#direct-composer', 'Got it Alice - two ticks please'); await b.keyboard.press('Enter');
await waitFor(async () => /two ticks please/.test(await bodyText(a)), 15000, 300);
await sleep(1200);
console.log('   Alice received the reply live:', /two ticks please/.test(await bodyText(a)));
await snap(a, '1-alice-messages.png'); await snap(b, '2-bob-messages.png');

say('3. Security headers on the real nginx');
const r = await fetch(WEB + '/');
for (const h of ['content-security-policy', 'x-content-type-options', 'x-frame-options', 'referrer-policy', 'permissions-policy', 'server'])
  console.log(`   ${h}: ${(r.headers.get(h) ?? '(none)').slice(0, 150)}`);
console.log('   CSP violations seen by the browser while chatting:', await a.evaluate(() => (window.__cspv ?? []).length));

say('4. An injected script is blocked by the policy (negative control)');
const ran = await a.evaluate(() => { const s = document.createElement('script'); s.textContent = 'window.__pwned = 1'; document.body.appendChild(s); return window.__pwned === 1; });
console.log('   injected <script> executed?', ran, '  (false = blocked)');

say('5. Lockout: five wrong passwords for Bob, shown on a fresh login page');
const c = await openPage(browser, { width: 900, height: 700, bypassCsp: false });
for (let i = 1; i <= 5; i++) {
  const res = await rest(WEB, '/api/auth/login', { method: 'POST', body: { email: bob.email, password: 'wrong-' + i } });
  console.log(`   attempt ${i}: HTTP ${res.status} ${res.json?.error?.code ?? ''}`);
}
await login(c, { email: bob.email, password: bob.password });
await c.waitForSelector('.form-error', { timeout: 10000 });
console.log('   login page says:', (await c.$eval('.form-error', e => e.innerText)).trim());
await snap(c, '3-lockout-login-page.png');
await c.close();

say('6. Bob (already signed in) is not kicked out, and Alice can still log in: lockout is per email');
await freshWindow('this step is not affected by the earlier requests');
console.log('   Bob still chatting:', /Got it Alice/.test(await bodyText(b)));
console.log('   Alice login while Bob is locked: HTTP', (await rest(WEB, '/api/auth/login', { method: 'POST', body: { email: alice.email, password: alice.password } })).status);

say('7. Sign-in rate limit per address (default 10 per minute), with a rotating fake X-Forwarded-For');
await freshWindow('the count starts from zero');
let codes = [];
for (let i = 1; i <= 14; i++) {
  const res = await fetch(WEB + '/api/auth/login', { method: 'POST', headers: { 'content-type': 'application/json', 'X-Forwarded-For': `9.9.9.${i}` }, body: JSON.stringify({ email: `nobody${i}@example.com`, password: 'x' }) });
  codes.push(res.status);
  if (res.status === 429 && codes.filter(c => c === 429).length === 1) console.log(`   first 429 at request ${i}: Retry-After ${res.headers.get('retry-after')}s, body ${JSON.stringify(await res.json())}`);
}
console.log('   statuses:', codes.join(' '));
console.log('   counter in Redis:', redisCli(`--scan --pattern 'ratelimit:auth:*'`).split('\n').filter(Boolean).join(', '));
await sleep(500);
await browser.close();
console.log('\nDemo finished. Screenshots are in', shots);
