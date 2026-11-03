import fs from 'node:fs';
import path from 'node:path';
import { sh, check, finish, sleep, waitFor, newUser, sql, count, cleanDb, redisCli, launch, openPage, WEB, ROOT } from '../lib.mjs';

// ---- Phase 23: sessions and tokens on the normal stack (real MySQL, real nginx and CSP, real Chrome) ----
// A schema   B the rotation rules over real HTTP and MySQL   C a browser session whose access token really lapses and is renewed
// D two tabs renewing at the same moment   E a password change on one device ends the other's session   F logout in the UI revokes the token
const OFF = { RATE_AUTH_PER_MINUTE: 100000, RATE_CHAT_PER_MINUTE: 100000, LOCKOUT_MAX_ATTEMPTS: 100000, LOCKOUT_MINUTES: 15 };
const shots = path.join(ROOT, 'e2e', 'shots', 'phase23'); fs.mkdirSync(shots, { recursive: true });
const PW = 'Passw0rd!x';

async function recreateApi(env) {
  const envStr = Object.entries({ ...OFF, ...env }).map(([k, v]) => `${k}=${v}`).join(' ');
  sh(`${envStr} docker compose --env-file docker.env up -d --force-recreate api 2>&1 | tail -1`, { allowFail: true });
  sh('docker compose --env-file docker.env restart web 2>&1 | tail -1', { allowFail: true });
  return !!(await waitFor(async () => (await fetch(WEB + '/api/nothing-here').then(r => r.status).catch(() => 0)) === 404, 120000, 1000));
}
const raw = async (p, { method = 'GET', body, token } = {}) => {
  const r = await fetch(WEB + p, { method, headers: { 'content-type': 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}) }, body: body ? JSON.stringify(body) : undefined });
  const text = await r.text(); let json = null; try { json = JSON.parse(text); } catch { /* no body */ }
  return { status: r.status, json, text };
};
const login = (email, password = PW) => raw('/api/auth/login', { method: 'POST', body: { email, password } });
const refresh = (refreshToken) => raw('/api/auth/refresh', { method: 'POST', body: { refreshToken } });
const uiLogin = async (page, email, password) => { await page.goto(WEB + '/login'); await page.type('input[type=email]', email); await page.type('#login-password', password); await page.click('button[type=submit]'); await page.waitForSelector('#composer-input', { timeout: 20000 }); };
const clickButton = (page, label) => page.evaluate((l) => { const b = Array.from(document.querySelectorAll('button')).find(x => x.textContent.trim() === l); if (!b) return false; b.click(); return true; }, label);
const ls = (page, key) => page.evaluate((k) => localStorage.getItem(k), key);
const exp = (jwt) => JSON.parse(Buffer.from(jwt.split('.')[1], 'base64url')).exp;
/** waitForSelector right after a reload can lose its frame ("detached Frame") when the page navigates under it; ask again. */
const waitSel = async (page, selector, timeout = 25000) => { for (let i = 0; ; i++) { try { return await page.waitForSelector(selector, { timeout }); } catch (e) { if (i >= 3 || !/detached/i.test(String(e))) throw e; await sleep(500); } } };
const snap = (p, n) => p.screenshot({ path: path.join(shots, n) });

cleanDb(); redisCli('flushall');

console.log('\n--- A: the schema the migration made, on real MySQL ---');
const cols = sql(`SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='AiChatAssistant' AND TABLE_NAME='RefreshTokens' ORDER BY 1`).split('\n').filter(Boolean);
check('A23-01', 'the RefreshTokens table exists with the expected columns', ['CreatedAt', 'ExpiresAt', 'FamilyId', 'Id', 'RevokedAt', 'TokenHash', 'UsedAt', 'UserId'].every(c => cols.includes(c)), cols.join(','));
const fk = sql(`SELECT DELETE_RULE FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='AiChatAssistant' AND TABLE_NAME='RefreshTokens' AND REFERENCED_TABLE_NAME='Users'`);
const uniq = sql(`SELECT NON_UNIQUE FROM information_schema.STATISTICS WHERE TABLE_SCHEMA='AiChatAssistant' AND TABLE_NAME='RefreshTokens' AND COLUMN_NAME='TokenHash'`);
check('A23-02', 'deleting a user cascades to their refresh tokens, and the token hash is unique', fk.trim() === 'CASCADE' && uniq.trim() === '0', `rule ${fk.trim()}, non_unique ${uniq.trim()}`);

console.log('\n--- B: rotation over real HTTP and real MySQL ---');
const ada = await newUser('Ada Rotate', WEB);
const l1 = await login(ada.email);
const pair1 = { access: l1.json.token, refresh: l1.json.refreshToken };
const life = exp(pair1.access) - Math.floor(Date.now() / 1000);
check('A23-03', 'login returns an access token that lives about 15 minutes and a refresh token', l1.status === 200 && life > 14 * 60 && life < 16 * 60 && !!pair1.refresh, `access lifetime ${life}s`);
const stored = sql(`SELECT TokenHash FROM RefreshTokens WHERE UserId=${ada.id}`);
check('A23-04', 'MySQL holds only a 64-character hash of it, never the token itself', !stored.includes(pair1.refresh) && stored.split('\n').filter(Boolean).every(h => /^[0-9A-F]{64}$/.test(h)));
const r2 = await refresh(pair1.refresh);
check('A23-05', 'refreshing returns a new, different pair and the new access token works', r2.status === 200 && r2.json.refreshToken !== pair1.refresh && (await raw('/api/chat/sessions', { token: r2.json.token })).status === 200);
const rows = sql(`SELECT COUNT(*), COUNT(DISTINCT FamilyId), SUM(UsedAt IS NOT NULL) FROM RefreshTokens WHERE UserId=${ada.id}`).trim().split(/\s+/).map(Number);
check('A23-06', 'the old row is marked used, the new one is in the same family', rows[2] >= 1 && rows[1] >= 1, rows.join(' / '));
const raced = await refresh(pair1.refresh);
const r2b = await refresh(r2.json.refreshToken); // the login is still alive: the winner's token rotates again
check('A23-07', 'using the old token again straight away (the two-tabs case) is refused with 401 invalid_refresh_token but does NOT end the login', raced.status === 401 && raced.json?.error?.code === 'invalid_refresh_token' && r2b.status === 200);
const r3pair = (await refresh((await login(ada.email)).json.refreshToken)).json; // a second, separate login to prove families are independent
await sleep(11000); // past the 10 s grace window
const replay = await refresh(pair1.refresh);
const newest = await refresh(r2b.json.refreshToken);
check('A23-08', 'replaying the old token after the grace window is theft: refused, and the WHOLE login is revoked (even the newest token of that login now fails)', replay.status === 401 && newest.status === 401);
check('A23-09', "the thief's replay did not touch Ada's other login (a different family keeps working)", (await refresh(r3pair.refreshToken)).status === 200);
const bad = await Promise.all(['', 'x', 'a'.repeat(5000)].map(refresh));
check('A23-10', 'empty, unknown and oversized tokens all get the same 401 in the standard error shape', bad.every(r => r.status === 401 && r.json?.error?.code === 'invalid_refresh_token'));
const bob = await newUser('Bob Phone', WEB); const bobLaptop = await login(bob.email);
const out = await raw('/api/auth/logout', { method: 'POST', body: { refreshToken: bob.token ? (await login(bob.email)).json.refreshToken : '' } });
check('A23-11', 'logout answers 204 and is idempotent (unknown token, missing body fields: still 204)', out.status === 204 && (await raw('/api/auth/logout', { method: 'POST', body: {} })).status === 204 && (await raw('/api/auth/logout', { method: 'POST', body: { refreshToken: 'nope' } })).status === 204);
const phone = (await login(bob.email)).json;
await raw('/api/auth/logout', { method: 'POST', body: { refreshToken: phone.refreshToken } });
check('A23-12', "logout revokes that device's refresh token but leaves Bob's other device (the laptop) signed in", (await refresh(phone.refreshToken)).status === 401 && (await refresh(bobLaptop.json.refreshToken)).status === 200);
const carol = await newUser('Carol Everywhere', WEB); const cA = (await login(carol.email)).json, cB = (await login(carol.email)).json;
await sleep(1300); // tokens carry whole-second issue times: let the clock move on so "issued before the sign-out" is unambiguous
const all = await raw('/api/auth/logout-all', { method: 'POST', token: cA.token });
check('A23-13', 'logout-all (signed in) ends every device: both refresh tokens and the live access tokens stop working', all.status === 204 && (await refresh(cA.refreshToken)).status === 401 && (await refresh(cB.refreshToken)).status === 401 && (await raw('/api/chat/sessions', { token: cB.token })).status === 401);
check('A23-14', 'logout-all without signing in is 401, and Carol can log in again afterwards', (await raw('/api/auth/logout-all', { method: 'POST' })).status === 401 && (await login(carol.email)).status === 200);
const dan = await newUser('Dan Delete', WEB); const dTok = (await login(dan.email)).json;
await raw('/api/account/delete', { method: 'POST', token: dTok.token, body: { password: PW } });
check('A23-15', "deleting an account removes its refresh-token rows from MySQL and its refresh token is useless", count('RefreshTokens', `UserId=${dan.id}`) === 0 && (await refresh(dTok.refreshToken)).status === 401);
const gpad = await newUser('Gina Reset', WEB); const gTok = (await login(gpad.email)).json;
await sleep(1300);
const cp = await raw('/api/account/change-password', { method: 'POST', token: gTok.token, body: { currentPassword: PW, newPassword: 'Brand-new-pass-9' } });
check('A23-16', 'changing the password ends the other logins but the caller gets a new working pair', cp.status === 200 && (await refresh(gTok.refreshToken)).status === 401 && (await refresh(cp.json.refreshToken)).status === 200);
const pathsLeak = sh('docker compose --env-file docker.env logs --no-log-prefix api 2>&1', { allowFail: true });
check('A23-17', 'no refresh token appears in the API log', !pathsLeak.includes(pair1.refresh) && !pathsLeak.includes(r2.json.refreshToken));

console.log('\n--- C: a browser session whose access token REALLY lapses is renewed without the person noticing ---');
check('A23-18', 'the API restarts with a 1-minute access token (and the normal 14-day refresh token)', await recreateApi({ JWT_EXPIRY_MINUTES: 1 }));
const eve = await newUser('Eve Browser', WEB);
const browser = await launch();
const page = await openPage(browser, { bypassCsp: false });
const refreshCalls = []; page.on('response', (r) => { if (r.url().includes('/api/auth/refresh')) refreshCalls.push(r.status()); });
await uiLogin(page, eve.email, PW);
const access0 = await ls(page, 'aichat.auth.token'), refresh0 = await ls(page, 'aichat.auth.refresh');
check('A23-19', 'after logging in the browser holds both tokens and the access token lasts only about a minute', !!access0 && !!refresh0 && exp(access0) - Math.floor(Date.now() / 1000) <= 61);
console.log('   waiting for the access token to really expire (1 minute + the 30 s validation tolerance)...');
await sleep(95000);
check('A23-20', 'the old access token is now refused by the API (401), so the lapse is real', (await raw('/api/chat/sessions', { token: access0 })).status === 401);
await page.evaluate(() => { const a = document.querySelector('a[href="/profile"]'); if (a) a.click(); });
await page.waitForSelector('#name-title', { timeout: 20000 });
const access1 = await ls(page, 'aichat.auth.token'), refresh1 = await ls(page, 'aichat.auth.refresh');
check('A23-21', 'opening another page (no reload) just works: the 401 was caught, ONE refresh happened, the request was repeated, and the person never saw a login screen', page.url().includes('/profile') && refreshCalls.length === 1 && refreshCalls[0] === 200, `url ${page.url()}, refresh calls ${JSON.stringify(refreshCalls)}`);
check('A23-22', 'both tokens in the browser were replaced by the new pair', access1 !== access0 && refresh1 !== refresh0 && exp(access1) > exp(access0));
await snap(page, 'renewed-session.png');
await page.reload(); await page.waitForSelector('#name-title', { timeout: 20000 });
check('A23-23', 'a reload keeps the session (the app starts signed in thanks to the refresh token)', page.url().includes('/profile'));
await sleep(95000);
await page.reload(); await page.waitForSelector('#name-title', { timeout: 20000 });
check('A23-24', 'a RELOAD after the access token lapsed again also stays signed in (the guard lets a refresh-token session in; the first request renews)', page.url().includes('/profile') && refreshCalls.length === 2, `refresh calls ${JSON.stringify(refreshCalls)}`);

console.log('\n--- D: two tabs of the same browser renew at the same moment ---');
const tabB = await page.browserContext().newPage();
await tabB.goto(WEB + '/profile'); await tabB.waitForSelector('#name-title', { timeout: 20000 });
await sleep(95000);
await Promise.all([page.reload(), tabB.reload()]);
await Promise.all([waitSel(page, '#name-title'), waitSel(tabB, '#name-title')]);
await sleep(1500);
check('A23-25', 'both tabs are still signed in on /profile (the tab that lost the race adopted the winner\'s tokens instead of signing out)', page.url().includes('/profile') && tabB.url().includes('/profile'), `${page.url()} | ${tabB.url()}`);
check('A23-26', 'the stored refresh token is still valid for the next renewal', (await refresh(await ls(page, 'aichat.auth.refresh'))).status === 200);
await tabB.close();

console.log('\n--- E: changing the password on one device ends the other device for real ---');
const frank = await newUser('Frank Two', WEB);
const d1 = await openPage(browser, { bypassCsp: false }), d2 = await openPage(browser, { bypassCsp: false });
await Promise.all([uiLogin(d1, frank.email, PW), uiLogin(d2, frank.email, PW)]);
await sleep(1300);
await d1.goto(WEB + '/profile'); await d1.waitForSelector('#current-password');
await d1.type('#current-password', PW); await d1.type('#new-password', 'Brand-new-pass-9'); await d1.type('#confirm-password', 'Brand-new-pass-9');
await clickButton(d1, 'Change password');
await d1.waitForFunction(() => /other devices have been signed out/.test(document.body.innerText), { timeout: 15000 });
await d2.evaluate(() => { const a = document.querySelector('a[href="/profile"]'); if (a) a.click(); });
const kicked = await waitFor(async () => d2.url().includes('/login'), 20000, 300);
check('A23-27', 'device 2 is sent to the login page (its access token was refused and its refresh token was revoked, so renewal failed)', !!kicked, d2.url());
check('A23-28', 'device 2 holds no tokens any more; device 1 is still signed in', (await ls(d2, 'aichat.auth.refresh')) === null && (await ls(d1, 'aichat.auth.refresh')) !== null);

console.log('\n--- F: "Sign out everywhere" and logging out in the UI ---');
const gail = await newUser('Gail Out', WEB);
const g1 = await openPage(browser, { bypassCsp: false }), g2 = await openPage(browser, { bypassCsp: false });
await Promise.all([uiLogin(g1, gail.email, PW), uiLogin(g2, gail.email, PW)]);
const g2Refresh = await ls(g2, 'aichat.auth.refresh');
await sleep(1300);
await g1.goto(WEB + '/profile'); await g1.waitForSelector('#sessions-title');
await snap(g1, 'sessions-card.png');
await clickButton(g1, 'Sign out everywhere');
const g1Out = await waitFor(async () => g1.url().includes('/login'), 15000, 300);
check('A23-29', '"Sign out everywhere" sends this browser to the login page and clears its tokens', !!g1Out && (await ls(g1, 'aichat.auth.refresh')) === null);
check('A23-30', "the other browser's refresh token is dead on the server", (await refresh(g2Refresh)).status === 401);
const hal = await newUser('Hal Logout', WEB);
const h = await openPage(browser, { bypassCsp: false });
await uiLogin(h, hal.email, PW);
const hRefresh = await ls(h, 'aichat.auth.refresh');
await h.evaluate(() => { const b = Array.from(document.querySelectorAll('button')).find(x => x.textContent.trim() === 'Log out'); b?.click(); });
await waitFor(async () => h.url().includes('/login'), 15000, 300);
await sleep(800);
check('A23-31', 'Log out in the UI revokes the refresh token on the server (not just in the browser)', (await refresh(hRefresh)).status === 401 && (await ls(h, 'aichat.auth.refresh')) === null);
const pageErrors = [page, d1, d2, g1, g2, h].flatMap(p => p.errs);
check('A23-32', 'no uncaught script errors in any page during the whole run', pageErrors.length === 0, pageErrors.join(' | '));

await browser.close();
console.log('\n--- put the normal configuration back ---');
check('A23-33', 'the API is restarted with the normal 15-minute access token', await recreateApi({}));
cleanDb(); redisCli('flushall');
finish();
