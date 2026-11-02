import fs from 'node:fs';
import path from 'node:path';
import { sh, dc, check, finish, sleep, waitFor, rest, newUser, newSession, ask, newConv, dsend, hub, sql, count, cleanDb, redisCli, launch, openPage, WEB, ROOT } from '../lib.mjs';

// ---- Phase 22: account management on the normal stack (real MySQL, real nginx, real Chrome, real AI service) ----
// A deleting an account removes everything of theirs on real MySQL (foreign-key cascades)   B changing a password across two devices
// C the profile page (rename, the two-step delete)   D optional email confirmation (registration, login, the link)   E schema.
const OFF = { RATE_AUTH_PER_MINUTE: 100000, RATE_CHAT_PER_MINUTE: 100000, LOCKOUT_MAX_ATTEMPTS: 100000, LOCKOUT_MINUTES: 15 };
const shots = path.join(ROOT, 'e2e', 'shots', 'phase22'); fs.mkdirSync(shots, { recursive: true });
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
const login = (email, password) => raw('/api/auth/login', { method: 'POST', body: { email, password } });
const uiLogin = async (page, email, password) => { await page.goto(WEB + '/login'); await page.type('input[type=email]', email); await page.type('#login-password', password); await page.click('button[type=submit]'); };
const clickButton = (page, label) => page.evaluate((l) => { const b = Array.from(document.querySelectorAll('button')).find(x => x.textContent.trim() === l); if (!b) return false; b.click(); return true; }, label);
const bodyText = p => p.evaluate(() => document.body.innerText);
const snap = (p, n) => p.screenshot({ path: path.join(shots, n) });

cleanDb(); redisCli('flushall');
const stamp = Date.now();

console.log('\n--- E: the schema the migration made, on real MySQL ---');
const cols = sql(`SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='AiChatAssistant' AND TABLE_NAME='Users' AND COLUMN_NAME IN ('PasswordChangedAt','EmailVerifiedAt') ORDER BY 1`).split('\n').filter(Boolean);
check('A22-01', 'the migration added Users.PasswordChangedAt and Users.EmailVerifiedAt', cols.join(',') === 'EmailVerifiedAt,PasswordChangedAt', cols.join(','));
const fk = sql(`SELECT DELETE_RULE FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='AiChatAssistant' AND TABLE_NAME='EmailVerificationTokens' AND REFERENCED_TABLE_NAME='Users'`);
check('A22-02', 'the new EmailVerificationTokens table exists and cascades when its user is deleted', fk.trim() === 'CASCADE', `delete rule: ${fk.trim()}`);

console.log('\n--- A: deleting an account removes everything of theirs (foreign-key cascades on real MySQL) ---');
const carol = await newUser('Carol Delete', WEB), dave = await newUser('Dave Stays', WEB);
const s1 = await newSession(carol, WEB), s2 = await newSession(carol, WEB), daveSession = await newSession(dave, WEB);
const reply = await ask(carol, s1, 'Reply with the single word: ok', WEB);
await ask(dave, daveSession, 'Reply with the single word: fine', WEB);
const conv = await newConv(carol, dave, WEB);
await dsend(carol, conv, 'from carol to dave', WEB); await dsend(dave, conv, 'from dave to carol', WEB); await dsend(carol, conv, 'carol again', WEB);
await raw('/api/auth/forgot-password', { method: 'POST', body: { email: carol.email } }); // leaves a password-reset token row
await sleep(500);
const carolRows = () => ({
  user: count('Users', `Id=${carol.id}`), roles: count('UserRoles', `UserId=${carol.id}`), sessions: count('ChatSessions', `UserId=${carol.id}`),
  messages: count('Messages', `SessionId IN ('${s1}','${s2}')`), usage: count('UsageLogs', `UserId=${carol.id}`),
  convs: count('DirectConversations', `UserLowId=${carol.id} OR UserHighId=${carol.id}`), dms: count('DirectMessages', `ConversationId='${conv}'`),
  resets: count('PasswordResetTokens', `UserId=${carol.id}`),
});
const before = carolRows();
const daveBefore = { sessions: count('ChatSessions', `UserId=${dave.id}`), messages: count('Messages', `SessionId='${daveSession}'`), usage: count('UsageLogs', `UserId=${dave.id}`) };
check('A22-03', 'before deleting, Carol really has rows in every table (user, role, chats, messages, usage, a direct conversation with messages, a reset token)', reply.status === 200 && before.user === 1 && before.roles === 1 && before.sessions === 2 && before.messages >= 2 && before.usage >= 1 && before.convs === 1 && before.dms === 3 && before.resets === 1, JSON.stringify(before));
const wrong = await raw('/api/account/delete', { method: 'POST', token: carol.token, body: { password: 'not-her-password' } });
check('A22-04', 'a wrong password does not delete anything (400) and her token still works', wrong.status === 400 && wrong.json?.error?.code === 'invalid_current_password' && count('Users', `Id=${carol.id}`) === 1 && (await raw('/api/chat/sessions', { token: carol.token })).status === 200, `status ${wrong.status}`);
const del = await raw('/api/account/delete', { method: 'POST', token: carol.token, body: { password: PW } });
const after = carolRows();
check('A22-05', 'deleting returns 204 and EVERY one of her rows is gone from MySQL: user, role, chats, messages, usage logs, the direct conversation and its messages, the reset token', del.status === 204 && Object.values(after).every(n => n === 0), `status ${del.status}; after: ${JSON.stringify(after)}`);
const daveAfter = { sessions: count('ChatSessions', `UserId=${dave.id}`), messages: count('Messages', `SessionId='${daveSession}'`), usage: count('UsageLogs', `UserId=${dave.id}`) };
check('A22-06', "Dave's own account, chat, messages and usage log are untouched", count('Users', `Id=${dave.id}`) === 1 && JSON.stringify(daveAfter) === JSON.stringify(daveBefore), JSON.stringify(daveAfter));
const daveConvs = (await raw('/api/direct/conversations', { token: dave.token })).json;
check('A22-07', 'the direct conversation is gone from Dave\'s side too (he no longer sees it, and cannot read its old id)', Array.isArray(daveConvs) && daveConvs.length === 0 && (await raw(`/api/direct/conversations/${conv}/messages`, { token: dave.token })).status === 404, `${daveConvs?.length} conversations`);
check('A22-08', "Carol's token stops working at once (401), and her email can no longer log in", (await raw('/api/chat/sessions', { token: carol.token })).status === 401 && (await login(carol.email, PW)).status === 401);
const rereg = await raw('/api/auth/register', { method: 'POST', body: { name: 'Carol Again', email: carol.email, password: PW } });
check('A22-09', 'the address is free again: registering it makes a brand-new account with none of the old data', rereg.status === 201 && Number(JSON.parse(Buffer.from(rereg.json.token.split('.')[1], 'base64url')).sub) !== carol.id && count('ChatSessions', `UserId=${rereg.json.user.id}`) === 0);
const outboxText = sql(`SELECT COUNT(*) FROM OutboxMessages WHERE Payload LIKE '%from carol to dave%'`);
check('A22-10', 'the events left in the outbox never contained message text (they carry ids and status only), so deleting leaves no text behind there either', Number(outboxText) === 0, `rows containing her message text: ${outboxText}`);
const apiLogs = dc('logs --no-log-prefix api', { allowFail: true });
check('A22-11', 'the API logged the deletion by user id only (no email address)', new RegExp(`User ${carol.id} deleted their account`).test(apiLogs) && !apiLogs.includes(carol.email));

console.log('\n--- B: changing a password signs out the other device (two real browsers behind the real nginx and CSP) ---');
const alice = await newUser('Alice Phone', WEB);
const browser = await launch();
const dev1 = await openPage(browser, { bypassCsp: false }), dev2 = await openPage(browser, { bypassCsp: false });
await Promise.all([uiLogin(dev1, alice.email, PW), uiLogin(dev2, alice.email, PW)]);
await Promise.all([dev1.waitForSelector('#composer-input', { timeout: 20000 }), dev2.waitForSelector('#composer-input', { timeout: 20000 })]);
const oldToken = await dev2.evaluate(() => localStorage.getItem('aichat.auth.token'));
check('A22-12', 'Alice is signed in on two devices at once (two separate browser windows)', !!oldToken && (await raw('/api/chat/sessions', { token: oldToken })).status === 200);
await sleep(1300); // tokens carry their issue time in whole seconds: let the clock move on so "older session" is unambiguous
await dev1.goto(WEB + '/profile'); await dev1.waitForSelector('#current-password');
await dev1.type('#current-password', PW); await dev1.type('#new-password', 'Brand-new-pass-9'); await dev1.type('#confirm-password', 'Brand-new-pass-9');
await clickButton(dev1, 'Change password');
await dev1.waitForFunction(() => /other devices have been signed out/.test(document.body.innerText), { timeout: 15000 });
await snap(dev1, 'password-changed.png');
check('A22-13', 'on device 1 the profile page says "Your other devices have been signed out."', true);
const stillOk = await raw('/api/chat/sessions', { token: await dev1.evaluate(() => localStorage.getItem('aichat.auth.token')) });
check('A22-14', 'device 1 stays signed in (the response carried a new token)', stillOk.status === 200);
await dev2.goto(WEB + '/messages');
const kicked = await waitFor(async () => dev2.url().includes('/login'), 20000, 300);
check('A22-15', 'device 2 is signed out: its next page load is sent to the login screen by the error interceptor', !!kicked, dev2.url());
check('A22-16', 'the old token is refused by the API (401) and the old password no longer logs in, the new one does', (await raw('/api/chat/sessions', { token: oldToken })).status === 401 && (await login(alice.email, PW)).status === 401 && (await login(alice.email, 'Brand-new-pass-9')).status === 200);
let hubRejected = false; try { const h = await hub({ token: oldToken }, WEB); await h.close(); } catch { hubRejected = true; }
check('A22-17', 'the realtime connection refuses the old token too (a fresh WebSocket with it is rejected)', hubRejected);
await dev1.close(); await dev2.close();

console.log('\n--- C: the profile page: rename and the two-step delete ---');
const bob = await newUser('Bob Profile', WEB);
const bobPage = await openPage(browser, { bypassCsp: false });
await uiLogin(bobPage, bob.email, PW); await bobPage.waitForSelector('#composer-input', { timeout: 20000 });
await bobPage.goto(WEB + '/profile'); await bobPage.waitForSelector('#name-title');
const nameBefore = await bobPage.$eval('section[aria-labelledby=name-title] input', e => e.value);
const saveDisabledBefore = await bobPage.evaluate(() => Array.from(document.querySelectorAll('button')).find(b => b.textContent.trim() === 'Save name').disabled);
await bobPage.$eval('section[aria-labelledby=name-title] input', e => { e.value = ''; });
await bobPage.type('section[aria-labelledby=name-title] input', 'Robert Renamed');
const saveDisabledAfter = await bobPage.evaluate(() => Array.from(document.querySelectorAll('button')).find(b => b.textContent.trim() === 'Save name').disabled);
check('A22-18', 'the name field starts with the current name and "Save name" is off until it is changed', nameBefore === 'Bob Profile' && saveDisabledBefore === true && saveDisabledAfter === false, `"${nameBefore}", disabled before ${saveDisabledBefore}, after ${saveDisabledAfter}`);
await clickButton(bobPage, 'Save name');
await bobPage.waitForFunction(() => /Name saved/.test(document.body.innerText), { timeout: 10000 });
await bobPage.reload(); await bobPage.waitForSelector('#name-title');
check('A22-19', 'the new name is saved ("Name saved."), survives a reload and is in MySQL', (await bobPage.$eval('section[aria-labelledby=name-title] input', e => e.value)) === 'Robert Renamed' && sql(`SELECT Name FROM Users WHERE Id=${bob.id}`).trim() === 'Robert Renamed');
check('A22-20', 'other people find him under the new name in the Messages search (name only, no email)', await (async () => { const r = await raw('/api/direct/users?search=Robert', { token: dave.token }); return r.status === 200 && JSON.stringify(r.json).includes('Robert Renamed') && !JSON.stringify(r.json).includes('@'); })());
check('A22-21', 'the privacy note on the page says what is kept, who can see it and for how long', await (async () => { const t = await bodyText(bobPage); return /one-way hash/.test(t) && /AI provider/.test(t) && /administrators/.test(t) && /until you delete your account/.test(t); })());
await snap(bobPage, 'profile.png');
await clickButton(bobPage, 'Delete my account…');
await bobPage.waitForSelector('#delete-password');
const lockedBefore = await bobPage.evaluate(() => Array.from(document.querySelectorAll('button')).find(b => b.textContent.trim() === 'Delete account permanently').disabled);
await bobPage.type('#delete-password', 'wrong-password-1'); await bobPage.type('input[formcontrolname=phrase]', 'delete');
const lockedWrongCase = await bobPage.evaluate(() => Array.from(document.querySelectorAll('button')).find(b => b.textContent.trim() === 'Delete account permanently').disabled);
check('A22-22', 'delete is two steps: the first click only asks; the red button stays disabled until the exact word DELETE is typed (lower case does not count)', lockedBefore === true && lockedWrongCase === true && count('Users', `Id=${bob.id}`) === 1);
await bobPage.$eval('input[formcontrolname=phrase]', e => { e.value = ''; }); await bobPage.type('input[formcontrolname=phrase]', 'DELETE');
await snap(bobPage, 'delete-confirm.png');
await clickButton(bobPage, 'Delete account permanently');
await bobPage.waitForFunction(() => document.querySelector('.form-error'), { timeout: 10000 });
const errText = await bobPage.$eval('.form-error', e => e.innerText);
check('A22-23', 'a wrong password shows "The password is not correct." on the page, deletes nothing and keeps him signed in', /not correct/.test(errText) && count('Users', `Id=${bob.id}`) === 1 && bobPage.url().includes('/profile'), errText.trim());
await bobPage.$eval('#delete-password', e => { e.value = ''; }); await bobPage.type('#delete-password', PW);
await clickButton(bobPage, 'Delete account permanently');
const gone = await waitFor(async () => bobPage.url().includes('/login'), 15000, 300);
check('A22-24', 'the right password deletes the account: he lands on the login page, the stored session is cleared and the user is gone from MySQL', !!gone && (await bobPage.evaluate(() => localStorage.getItem('aichat.auth.token'))) === null && count('Users', `Id=${bob.id}`) === 0);
await bobPage.goto(WEB + '/profile');
check('A22-25', 'the profile page is no longer reachable (back to login), and logging in with the old details fails', (await waitFor(async () => bobPage.url().includes('/login'), 10000, 300)) !== null && (await login(bob.email, PW)).status === 401);
await bobPage.close();

console.log('\n--- D: optional email confirmation (Account:RequireEmailVerification on, console mail provider) ---');
const pre = await newUser('Already Registered', WEB); // registered while the setting was off
check('A22-26', 'the API restarts with confirmation required and the email going to the log (no real mail is sent)', await recreateApi({ ACCOUNT_REQUIRE_EMAIL_VERIFICATION: 'true', EMAIL_PROVIDER: 'console' }));
check('A22-27', 'someone who registered BEFORE the requirement can still log in (people registered while it was off count as confirmed)', (await login(pre.email, PW)).status === 200);
const regPage = await openPage(browser, { bypassCsp: false });
const emailNew = `confirm-${stamp}@example.com`;
await regPage.goto(WEB + '/register'); await regPage.type('input[type=text]', 'Confirm Me'); await regPage.type('input[type=email]', emailNew); await regPage.type('#register-password', PW);
await regPage.click('button[type=submit]'); await regPage.waitForFunction(() => /Check your email/.test(document.body.innerText), { timeout: 20000 });
await snap(regPage, 'check-your-email.png');
check('A22-28', 'registering shows "Check your email" with the address, and does NOT sign anyone in', (await bodyText(regPage)).includes(emailNew) && (await regPage.evaluate(() => localStorage.getItem('aichat.auth.token'))) === null);
const mailLink = (e) => { const lines = dc('logs --no-log-prefix api', { allowFail: true }).split('\n').filter(l => l.includes('Confirm your AI Chat Assistant') && l.includes(`<${e}>`)); const m = lines.length ? lines[lines.length - 1].match(/Link: (\S+)/) : null; return m?.[1] ?? null; };
const firstLink = await waitFor(async () => mailLink(emailNew), 30000, 1000);
check('A22-29', 'the confirmation email went through RabbitMQ to the worker, which sent it (here: logged) with a link back to this site', !!firstLink && firstLink.startsWith(`${WEB}/verify-email?token=`), firstLink ? firstLink.replace(/token=.*/, 'token=…') : 'no mail found in the log');
const rowTok = sql(`SELECT COUNT(*) FROM EmailVerificationTokens t JOIN Users u ON u.Id=t.UserId WHERE u.Email='${emailNew}'`);
const stored = sql(`SELECT t.TokenHash FROM EmailVerificationTokens t JOIN Users u ON u.Id=t.UserId WHERE u.Email='${emailNew}' LIMIT 1`).trim();
check('A22-30', 'the database keeps only a hash of the link token (64 hex characters), never the token itself', Number(rowTok) === 1 && /^[0-9A-F]{64}$/i.test(stored) && firstLink && !firstLink.includes(stored), stored.slice(0, 12) + '…');
const dupe = await raw('/api/auth/register', { method: 'POST', body: { name: 'Someone', email: pre.email, password: PW } });
const fresh = await raw('/api/auth/register', { method: 'POST', body: { name: 'Someone', email: `fresh-${stamp}@example.com`, password: PW } });
check('A22-31', 'registering an ALREADY-registered address gives exactly the same answer as a new one (202, identical body), so registration reveals nothing', dupe.status === 202 && fresh.status === 202 && dupe.text === fresh.text, `${dupe.status} / ${fresh.status}; same body: ${dupe.text === fresh.text}`);
const early = await login(emailNew, PW);
check('A22-32', 'logging in before confirming is 403 email_not_verified (and only when the password is right: a wrong one is a plain 401)', early.status === 403 && early.json?.error?.code === 'email_not_verified' && (await login(emailNew, 'wrong-password-1')).status === 401);
await uiLogin(regPage, emailNew, PW); await regPage.waitForSelector('.form-error', { timeout: 10000 });
const resendVisible = await regPage.evaluate(() => Array.from(document.querySelectorAll('button')).some(b => /new confirmation link/.test(b.textContent)));
await snap(regPage, 'unconfirmed-login.png');
check('A22-33', 'the login page says so in plain words and offers "Send a new confirmation link"', /Confirm your email address first/.test(await regPage.$eval('.form-error', e => e.innerText)) && resendVisible);
await clickButton(regPage, 'Send a new confirmation link');
await regPage.waitForFunction(() => /a new link has been sent/.test(document.body.innerText), { timeout: 10000 });
const secondLink = await waitFor(async () => { const l = mailLink(emailNew); return l && l !== firstLink ? l : null; }, 30000, 1000);
check('A22-34', 'asking for a new link sends a different one', !!secondLink && secondLink !== firstLink);
const vp = await openPage(browser, { bypassCsp: false });
await vp.goto(firstLink); await vp.waitForFunction(() => /couldn't confirm that|Email confirmed/.test(document.body.innerText), { timeout: 15000 });
check('A22-35', 'the OLD link no longer works (a new request replaces it): "We couldn\'t confirm that"', /couldn't confirm that/.test(await bodyText(vp)));
await vp.goto(secondLink); await vp.waitForFunction(() => /Email confirmed/.test(document.body.innerText), { timeout: 15000 });
await snap(vp, 'email-confirmed.png');
check('A22-36', 'the new link confirms the address: "Email confirmed", and MySQL records when', /Email confirmed/.test(await bodyText(vp)) && sql(`SELECT EmailVerifiedAt IS NOT NULL FROM Users WHERE Email='${emailNew}'`).trim() === '1');
await vp.goto(secondLink); await vp.waitForFunction(() => /couldn't confirm that/.test(document.body.innerText), { timeout: 15000 });
check('A22-37', 'a link works only once: using it again is refused', true);
check('A22-38', 'after confirming, logging in works (200) and the address is recorded as confirmed', (await login(emailNew, PW)).status === 200);
await vp.close(); await regPage.close();

console.log('\n--- clean-up ---');
await browser.close();
check('A22-39', 'the API is put back to the standard configuration for the suites that follow', await recreateApi({ ACCOUNT_REQUIRE_EMAIL_VERIFICATION: 'false', EMAIL_PROVIDER: 'smtp' }));
finish();
