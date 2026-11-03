// Phase 23 demo: a real browser session whose short-lived access token really expires and is renewed without the person noticing,
// the rotation / theft-detection rules shown over plain HTTP, and "Sign out everywhere" ending a second signed-in window.
// Needs the normal stack on :8080 started with a 1-minute access token:  JWT_EXPIRY_MINUTES=1 docker compose --env-file docker.env up -d --force-recreate api && docker compose --env-file docker.env restart web
import fs from 'node:fs';
import path from 'node:path';
import { launch, openPage, sleep, waitFor, newUser, redisCli, sql, WEB, ROOT } from './lib.mjs';

const shots = path.join(ROOT, 'e2e', 'shots', 'demo23'); fs.mkdirSync(shots, { recursive: true });
const snap = (p, n) => p.screenshot({ path: path.join(shots, n) }).then(() => console.log('   screenshot:', path.join(shots, n)));
const say = t => console.log('\n=== ' + t);
const short = t => (t ?? 'none').slice(0, 14) + '…';
const exp = jwt => JSON.parse(Buffer.from(jwt.split('.')[1], 'base64url')).exp;
const left = jwt => exp(jwt) - Math.floor(Date.now() / 1000);
const raw = async (p, { method = 'GET', body, token } = {}) => { const r = await fetch(WEB + p, { method, headers: { 'content-type': 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}) }, body: body ? JSON.stringify(body) : undefined }); const t = await r.text(); let j = null; try { j = JSON.parse(t); } catch { } return { status: r.status, json: j }; };
const ls = (page, k) => page.evaluate(x => localStorage.getItem(x), k);

redisCli('flushall');
const alice = await newUser('Alice Demo', WEB);

say('1. Alice logs in. The server answers with TWO tokens');
const l = await raw('/api/auth/login', { method: 'POST', body: { email: alice.email, password: alice.password } });
console.log(`   access token  ${short(l.json.token)}  lives ${left(l.json.token)} s   (short: a stolen copy is useless within minutes)`);
console.log(`   refresh token ${short(l.json.refreshToken)}  lives until ${l.json.refreshExpiresAt}   (single use; only its hash is stored in MySQL)`);
console.log('   MySQL holds:', sql(`SELECT LEFT(TokenHash,16) FROM RefreshTokens WHERE UserId=${alice.id} ORDER BY Id DESC LIMIT 1`).trim() + '…  (a hash, not the token)');

say('2. Rotation: every refresh gives a NEW pair and kills the old refresh token');
const r1 = await raw('/api/auth/refresh', { method: 'POST', body: { refreshToken: l.json.refreshToken } });
console.log(`   refresh with the login token   -> ${r1.status}, new refresh token ${short(r1.json.refreshToken)}`);
const again = await raw('/api/auth/refresh', { method: 'POST', body: { refreshToken: l.json.refreshToken } });
console.log(`   same old token again, at once  -> ${again.status} ${again.json.error.code}   (the two-tabs race: refused, login kept)`);
console.log('   waiting 11 s for the race-grace window to pass, as a thief replaying a copied token would...'); await sleep(11000);
const theft = await raw('/api/auth/refresh', { method: 'POST', body: { refreshToken: l.json.refreshToken } });
const owner = await raw('/api/auth/refresh', { method: 'POST', body: { refreshToken: r1.json.refreshToken } });
console.log(`   old token replayed later       -> ${theft.status}   THEFT DETECTED`);
console.log(`   the owner's newest token       -> ${owner.status}   (the whole login was revoked: both are signed out)`);

say('3. A real browser: the access token expires while the page is open');
const browser = await launch();
const page = await openPage(browser, { width: 1100, height: 760, bypassCsp: false });
const calls = []; page.on('response', r => { if (r.url().includes('/api/auth/refresh')) calls.push(r.status()); });
await page.goto(WEB + '/login'); await page.type('input[type=email]', alice.email); await page.type('#login-password', alice.password); await page.click('button[type=submit]');
await page.waitForSelector('#composer-input', { timeout: 20000 });
const a0 = await ls(page, 'aichat.auth.token'), r0 = await ls(page, 'aichat.auth.refresh');
console.log(`   signed in. access ${short(a0)} (${left(a0)} s left), refresh ${short(r0)}`);
let stale = a0;
if (left(a0) > 120) {
  // The stack runs with the normal 15-minute token, so waiting is not practical: SIMULATE the lapse by breaking the stored access token's signature.
  console.log('   (SIMULATED lapse: this stack uses 15-minute tokens, so the stored access token is corrupted instead of waiting. The 33-check suite waits for a real expiry.)');
  stale = a0.slice(0, -6) + 'XXXXXX';
  await page.evaluate(t => localStorage.setItem('aichat.auth.token', t), stale);
  await page.reload(); await page.waitForSelector('#composer-input', { timeout: 20000 });
} else {
  console.log('   now doing nothing for 95 s so the access token really expires (1 minute + 30 s tolerance)...'); await sleep(95000);
}
console.log(`   the stale access token is refused by the API: ${(await raw('/api/chat/sessions', { token: stale })).status}`);
await page.evaluate(() => document.querySelector('a[href="/profile"]')?.click());
await page.waitForSelector('#name-title', { timeout: 20000 });
const a1 = await ls(page, 'aichat.auth.token'), r1b = await ls(page, 'aichat.auth.refresh');
console.log(`   clicked "Account": the page opened, no login screen. The app caught the 401, renewed once (${calls.length} refresh call, status ${calls[0]}), retried.`);
console.log(`   access ${short(stale)} -> ${short(a1)}   refresh ${short(r0)} -> ${short(r1b)}   (both replaced)`);
await snap(page, '1-renewed-and-on-account-page.png');

say('4. A second signed-in window, then "Sign out everywhere"');
const other = await openPage(browser, { width: 1100, height: 760, bypassCsp: false });
await other.goto(WEB + '/login'); await other.type('input[type=email]', alice.email); await other.type('#login-password', alice.password); await other.click('button[type=submit]');
await other.waitForSelector('#composer-input', { timeout: 20000 });
const otherRefresh = await ls(other, 'aichat.auth.refresh');
console.log('   window 2 is signed in too'); await sleep(1300);
await snap(page, '2-sessions-card.png');
await page.evaluate(() => Array.from(document.querySelectorAll('button')).find(b => b.textContent.trim() === 'Sign out everywhere')?.click());
await waitFor(async () => page.url().includes('/login'), 15000, 300);
console.log(`   window 1 clicked it -> now at ${page.url().replace(WEB, '')}, tokens cleared: ${(await ls(page, 'aichat.auth.refresh')) === null}`);
const o = await raw('/api/auth/refresh', { method: 'POST', body: { refreshToken: otherRefresh } });
console.log(`   window 2's refresh token on the server -> ${o.status} (dead). Its next request will send it to the login page:`);
await other.evaluate(() => document.querySelector('a[href="/profile"]')?.click());
await waitFor(async () => other.url().includes('/login'), 20000, 300);
console.log(`   window 2 is at ${other.url().replace(WEB, '')}`);
await snap(other, '3-other-window-signed-out.png');

await browser.close();
console.log('\nDemo finished.');
