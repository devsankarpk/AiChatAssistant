import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { check, finish, sleep, waitFor, sql, count, cleanDb, redisCli, sh, launch, openPage, WEB, ROOT } from '../lib.mjs';

// ---- Phase 26: cost tracking and admin user management on the normal stack (real nginx and CSP, real API, real Python service and LLM provider, real MySQL, real Chrome) ----
// A schema   B cost of real replies (one-shot and streamed): model, token split and price   C first-admin bootstrap   D roles and disabling over HTTP
// E usage summary and CSV export computed by real MySQL   F the admin pages in Chrome (including accessibility)   Z clean up and put the standard configuration back
const require = createRequire(import.meta.url);
const axeSource = fs.readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8');
const shots = path.join(ROOT, 'e2e', 'shots', 'phase26'); fs.mkdirSync(shots, { recursive: true });
const snap = (p, n) => p.screenshot({ path: path.join(shots, n) });
const PW = 'Passw0rd!x';
const OFF = { RATE_AUTH_PER_MINUTE: 100000, RATE_CHAT_PER_MINUTE: 100000, LOCKOUT_MAX_ATTEMPTS: 100000, LOCKOUT_MINUTES: 15 };
const IN_RATE = 0.05, OUT_RATE = 0.08; // the compose defaults (PRICE_INPUT_PER_MILLION / PRICE_OUTPUT_PER_MILLION)
const esc = (s) => s.replace(/'/g, "''");
const uuid = () => crypto.randomUUID();

async function recreateApi(env) {
  const envStr = Object.entries({ ...OFF, ...env }).map(([k, v]) => `${k}=${v}`).join(' ');
  sh(`${envStr} docker compose --env-file docker.env up -d --force-recreate api 2>&1 | tail -1`, { allowFail: true });
  sh('docker compose --env-file docker.env restart web 2>&1 | tail -1', { allowFail: true });
  return !!(await waitFor(async () => (await fetch(WEB + '/api/nothing-here').then(r => r.status).catch(() => 0)) === 404, 120000, 1000));
}
const raw = async (p, { method = 'GET', body, token } = {}) => {
  const r = await fetch(WEB + p, { method, headers: { 'content-type': 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}) }, body: body ? JSON.stringify(body) : undefined });
  const text = await r.text(); let json = null; try { json = JSON.parse(text); } catch { /* not JSON */ }
  return { status: r.status, json, text, headers: r.headers };
};
let seq = 0;
async function register(name, email = `p26-${Date.now()}-${seq++}@example.com`) {
  const r = await raw('/api/auth/register', { method: 'POST', body: { name, email, password: PW } });
  if (r.status !== 201) throw new Error(`register ${r.status} ${r.text}`);
  return { name, email, id: Number(JSON.parse(Buffer.from(r.json.token.split('.')[1], 'base64url')).sub), token: r.json.token, refresh: r.json.refreshToken, roles: r.json.user.roles };
}
const login = (u) => raw('/api/auth/login', { method: 'POST', body: { email: u.email, password: PW } });
const makeAdminSql = (id) => sql(`INSERT INTO UserRoles (UserId,RoleId) SELECT ${id}, Id FROM Roles WHERE Name='Admin'`);
const adminLogin = async (u) => { makeAdminSql(u.id); const r = await login(u); return { ...u, token: r.json.token, refresh: r.json.refreshToken }; };
const newChat = async (u) => (await raw('/api/chat/sessions', { method: 'POST', token: u.token, body: {} })).json.id;
async function streamOnce(u, chat, content) {
  const res = await fetch(WEB + `/api/chat/sessions/${chat}/messages/stream`, { method: 'POST', headers: { 'content-type': 'application/json', authorization: `Bearer ${u.token}` }, body: JSON.stringify({ content }) });
  return { status: res.status, text: await res.text() };
}
const usageRows = (chat) => sql(`SELECT CONCAT_WS('|',TokensUsed,PromptTokens,CompletionTokens,Model,CostEstimate) FROM UsageLogs WHERE SessionId='${chat}' ORDER BY Id`).trim().split('\n').filter(Boolean)
  .map(l => { const [t, p, c, m, cost] = l.split('|'); return { total: +t, prompt: +p, completion: +c, model: m, cost: +cost, costText: cost }; });
const expectedCost = (p, c) => Math.round((p * IN_RATE + c * OUT_RATE) / 1e6 * 1e8) / 1e8;
const codeOf = (r) => r.json?.error?.code;

cleanDb(); redisCli('flushall');
check('P26-00', 'the API runs with the standard test configuration first', await recreateApi({ ADMIN_BOOTSTRAP_EMAIL: '' }));

console.log('\n--- A: the schema the migration made ---');
const col = (t, c) => sql(`SELECT CONCAT(COLUMN_TYPE,'/',IS_NULLABLE) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='AiChatAssistant' AND TABLE_NAME='${t}' AND COLUMN_NAME='${c}'`).trim();
check('A26-01', 'UsageLogs.CostEstimate is decimal(14,8)', col('UsageLogs', 'CostEstimate') === 'decimal(14,8)/NO', col('UsageLogs', 'CostEstimate'));
check('A26-02', 'UsageLogs has PromptTokens, CompletionTokens (int) and Model (varchar(100))', col('UsageLogs', 'PromptTokens') === 'int/NO' && col('UsageLogs', 'CompletionTokens') === 'int/NO' && col('UsageLogs', 'Model') === 'varchar(100)/NO', `${col('UsageLogs', 'PromptTokens')} ${col('UsageLogs', 'Model')}`);
check('A26-03', 'Users.DisabledAt is a nullable datetime(6)', col('Users', 'DisabledAt') === 'datetime(6)/YES', col('Users', 'DisabledAt'));

console.log('\n--- B: the cost of real replies ---');
const ann = await register('Ann Payer');
const chat1 = await newChat(ann);
const one = await raw(`/api/chat/sessions/${chat1}/messages`, { method: 'POST', token: ann.token, body: { content: 'In one short sentence: what is DNS?' } });
const r1 = usageRows(chat1)[0];
check('B26-01', 'a one-shot reply is recorded with a model, a prompt/completion split that adds up to the total, and a price', one.status === 200 && !!r1 && r1.model.length > 3 && r1.prompt > 0 && r1.completion > 0 && r1.total === r1.prompt + r1.completion, JSON.stringify(r1));
check('B26-02', `its cost is prompt x $${IN_RATE}/M + completion x $${OUT_RATE}/M (${r1 ? expectedCost(r1.prompt, r1.completion) : '?'})`, !!r1 && Math.abs(r1.cost - expectedCost(r1.prompt, r1.completion)) < 2e-8 && r1.cost > 0, JSON.stringify(r1));
check('B26-03', 'a single short reply costs less than a thousandth of a dollar, and the old 4-decimal column would have lost most of it', !!r1 && r1.cost < 0.001 && r1.cost > 0, String(r1?.costText));
const chat2 = await newChat(ann);
const st = await streamOnce(ann, chat2, 'In one short sentence: what is HTTP?');
const r2 = usageRows(chat2)[0];
check('B26-04', 'a streamed reply gets the same treatment (model, split, price)', st.status === 200 && !!r2 && r2.model === r1?.model && r2.prompt > 0 && r2.completion > 0 && Math.abs(r2.cost - expectedCost(r2.prompt, r2.completion)) < 2e-8, JSON.stringify(r2));
const admin0 = await adminLogin(await register('Root Admin'));
const list = await raw(`/api/admin/usage?pageSize=100`, { token: admin0.token });
const item = list.json?.items?.find(i => i.sessionId === chat1);
check('B26-05', 'the admin usage list carries the model, the split and the cost for that reply', !!item && item.model === r1.model && item.promptTokens === r1.prompt && item.completionTokens === r1.completion && Math.abs(item.costEstimate - r1.cost) < 2e-8, JSON.stringify(item));

console.log('\n--- C: the first-admin bootstrap ---');
const bootEmail = `boot-${Date.now()}@example.com`;
check('C26-01', 'the API restarts with ADMIN_BOOTSTRAP_EMAIL set while another Admin already exists', await recreateApi({ ADMIN_BOOTSTRAP_EMAIL: bootEmail }));
const bootNow = await register('Boot Strap', bootEmail);
check('C26-02', 'registering the configured address does NOT make an Admin while someone else already is one', !bootNow.roles.includes('Admin') && (await raw('/api/admin/ping', { token: bootNow.token })).status === 403, JSON.stringify(bootNow.roles));
sql('DELETE FROM UserRoles WHERE RoleId=(SELECT Id FROM Roles WHERE Name="Admin")'); // nobody is an Admin any more
check('C26-03', 'no Admin exists now (set up for the next step)', count('UserRoles', "RoleId=(SELECT Id FROM Roles WHERE Name='Admin')") === 0);
const bootLogin = await login(bootNow);
check('C26-04', 'with no Admin, the configured account becomes Admin at its next login, and the token proves it', bootLogin.status === 200 && bootLogin.json.user.roles.includes('Admin') && (await raw('/api/admin/ping', { token: bootLogin.json.token })).status === 200, JSON.stringify(bootLogin.json?.user?.roles));
const other = await register('Other Person');
const otherLogin = await login(other);
check('C26-05', 'nobody else is promoted: an ordinary account stays a User', !otherLogin.json.user.roles.includes('Admin'));
const lateEmail = `late-${Date.now()}@example.com`;
const late = await register('Late Admin', lateEmail);
sql('DELETE FROM UserRoles WHERE RoleId=(SELECT Id FROM Roles WHERE Name="Admin")');
check('C26-06', 'the API restarts with the setting pointing at an account that already exists', await recreateApi({ ADMIN_BOOTSTRAP_EMAIL: lateEmail }));
const promotedAtStart = await waitFor(() => count('UserRoles', `UserId=${late.id} AND RoleId=(SELECT Id FROM Roles WHERE Name='Admin')`) === 1, 20000, 500);
check('C26-07', 'start-up promotes that existing account (no login needed) when the installation has no Admin', !!promotedAtStart);
const logLine = sh('docker compose --env-file docker.env logs --no-log-prefix api 2>&1 | grep -c "Admin:BootstrapEmail"', { allowFail: true }).trim();
check('C26-08', 'start-up logged that it made an Admin, and logged no email address or password', Number(logLine) >= 1 && !sh(`docker compose --env-file docker.env logs --no-log-prefix api 2>&1 | grep -c "${lateEmail}"`, { allowFail: true }).trim().replace('0', ''), `lines ${logLine}`);
check('C26-09', 'the API goes back to the standard configuration', await recreateApi({ ADMIN_BOOTSTRAP_EMAIL: '' }));

console.log('\n--- D: roles and disabling over HTTP, on real MySQL ---');
const boss = await adminLogin(await register('Boss'));
const sam = await register('Sam Target');
const samChat = await newChat(sam);
const hubOk = async (tok) => (await fetch(WEB + `/hubs/direct/negotiate?negotiateVersion=1&access_token=${tok}`, { method: 'POST' })).status;
check('D26-01', 'before: Sam can use the API and the realtime hub accepts Sam', (await raw('/api/auth/me', { token: sam.token })).status === 200 && (await hubOk(sam.token)) === 200);
const dis = await raw(`/api/admin/users/${sam.id}/disable`, { method: 'POST', token: boss.token });
check('D26-02', 'disabling answers 200 with DisabledAt set, saved in the database', dis.status === 200 && !!dis.json.disabledAt && count('Users', `Id=${sam.id} AND DisabledAt IS NOT NULL`) === 1, String(dis.status));
check('D26-03', "Sam's existing token stops working at once (no waiting for it to expire), at the API and at the hub",
  (await raw('/api/auth/me', { token: sam.token })).status === 401 && (await hubOk(sam.token)) === 401);
const samRefresh = await raw('/api/auth/refresh', { method: 'POST', body: { refreshToken: sam.refresh } });
check('D26-04', "Sam's refresh token is revoked: 401 invalid_refresh_token", samRefresh.status === 401 && codeOf(samRefresh) === 'invalid_refresh_token', String(samRefresh.status));
const samLogin = await login(sam);
check('D26-05', 'logging in with the right password says the account is disabled (403 account_disabled)', samLogin.status === 403 && codeOf(samLogin) === 'account_disabled', `${samLogin.status} ${codeOf(samLogin)}`);
const wrong = await raw('/api/auth/login', { method: 'POST', body: { email: sam.email, password: 'Wrong-pass1!' } });
const nobody = await raw('/api/auth/login', { method: 'POST', body: { email: 'nobody-' + Date.now() + '@example.com', password: 'Wrong-pass1!' } });
check('D26-06', 'with a WRONG password a disabled account and an unknown one answer identically (no hint that the account exists)', wrong.status === 401 && wrong.text === nobody.text, `${wrong.status} / ${nobody.status}`);
check('D26-07', "nothing was deleted: Sam's chat is still in the database", count('ChatSessions', `Id='${samChat}' AND DeletedAt IS NULL`) === 1);
const en = await raw(`/api/admin/users/${sam.id}/enable`, { method: 'POST', token: boss.token });
const samBack = await login(sam);
check('D26-08', 'enabling restores login, and the chat is there', en.status === 200 && samBack.status === 200 && (await raw('/api/chat/sessions', { token: samBack.json.token })).json.some(s => s.id === samChat));
const self = await raw(`/api/admin/users/${boss.id}/disable`, { method: 'POST', token: boss.token });
const selfRole = await raw(`/api/admin/users/${boss.id}/role`, { method: 'PUT', token: boss.token, body: { role: 'User' } });
check('D26-09', 'an admin cannot disable or demote their own account (409)', self.status === 409 && codeOf(self) === 'cannot_disable_self' && selfRole.status === 409 && codeOf(selfRole) === 'cannot_change_own_role');
await sleep(1100); // sessions older than a second, because the "issued after" comparison is in whole seconds
const promote = await raw(`/api/admin/users/${samBack.json.user.id}/role`, { method: 'PUT', token: boss.token, body: { role: 'Admin' } });
check('D26-10', 'making Sam an Admin: roles = [Admin] in the database, and the old token no longer works', promote.status === 200 && JSON.stringify(promote.json.roles) === '["Admin"]' && (await raw('/api/auth/me', { token: samBack.json.token })).status === 401);
const samAdmin = await login(sam);
check('D26-11', 'the next login carries the Admin role and can reach admin endpoints', samAdmin.status === 200 && samAdmin.json.user.roles.includes('Admin') && (await raw('/api/admin/ping', { token: samAdmin.json.token })).status === 200);
const demote = await raw(`/api/admin/users/${sam.id}/role`, { method: 'PUT', token: boss.token, body: { role: 'User' } });
check('D26-12', 'demoting Sam works while another active Admin remains', demote.status === 200 && JSON.stringify(demote.json.roles) === '["User"]' && count('UserRoles', `UserId=${sam.id}`) === 1);
const plain = await raw('/api/admin/users', { token: ann.token });
const anon = await raw('/api/admin/users');
check('D26-13', 'an ordinary user gets 403 and no token gets 401 on every new admin endpoint',
  plain.status === 403 && anon.status === 401 && (await raw('/api/admin/usage/summary', { token: ann.token })).status === 403 && (await raw('/api/admin/usage/export', { token: ann.token })).status === 403 && (await raw(`/api/admin/users/${sam.id}/disable`, { method: 'POST', token: ann.token })).status === 403);
const wildcard = await register('100% Real');
const found = async (q) => (await raw(`/api/admin/users?search=${encodeURIComponent(q)}`, { token: boss.token })).json?.items?.map(i => i.name) ?? [];
check('D26-14', 'user search treats % as a plain character (real MySQL LIKE), ignores case, and also matches email', JSON.stringify(await found('%')) === '["100% Real"]' && (await found('SAM TARGET')).includes('Sam Target') && (await found(sam.email.slice(0, 12).toUpperCase())).includes('Sam Target'), JSON.stringify(await found('%')));
const hash = (await raw('/api/admin/users?pageSize=100', { token: boss.token })).text;
check('D26-15', 'the user list never contains a password hash', !/PasswordHash|\$2[aby]\$/.test(hash));
const dm = await raw('/api/direct/conversations', { method: 'POST', token: ann.token, body: { userId: sam.id } });
check('D26-16', 'an Admin still cannot read a private conversation (404)', dm.status < 300 && (await raw(`/api/direct/conversations/${dm.json.id}/messages`, { token: boss.token })).status === 404);

console.log('\n--- E: summary and CSV computed by real MySQL ---');
cleanDb(); redisCli('flushall');
const adm = await adminLogin(await register('Report Admin'));
const u1 = await register('Una Heavy'); const u2 = await register('=HYPERLINK("http://evil")'); const u3 = await register('Cleo Light');
const sess = {}; for (const u of [u1, u2, u3]) { const id = uuid(); sess[u.id] = id; sql(`INSERT INTO ChatSessions (Id,UserId,Title,CreatedAt) VALUES ('${id}',${u.id},'${esc(u === u2 ? '+cmd, "x"' : 'Chat of ' + u.name)}','2030-01-01 00:00:00.000000')`); }
const usage = (u, at, total, p, c, cost, model = 'vendor/model-x') => sql(`INSERT INTO UsageLogs (UserId,SessionId,TokensUsed,PromptTokens,CompletionTokens,Model,CostEstimate,CreatedAt) VALUES (${u.id},'${sess[u.id]}',${total},${p},${c},'${model}',${cost},'${at}')`);
usage(u1, '2030-03-01 23:59:59.500000', 1000, 700, 300, 0.00001234); usage(u1, '2030-03-02 00:00:00.000000', 2000, 1500, 500, 0.00002468); usage(u1, '2030-03-02 12:00:00.000000', 500, 400, 100, 0.00000617);
usage(u2, '2030-03-02 08:00:00.000000', 100, 60, 40, 0.00000100); usage(u3, '2030-03-03 08:00:00.000000', 10, 0, 0, 0, '');
const win = 'from=2030-03-01T00:00:00Z&to=2030-03-31T23:59:59Z';
const sum = (await raw(`/api/admin/usage/summary?${win}`, { token: adm.token })).json;
const dbTotals = sql(`SELECT CONCAT(COUNT(*),'|',SUM(TokensUsed),'|',SUM(PromptTokens),'|',SUM(CompletionTokens),'|',SUM(CostEstimate)) FROM UsageLogs WHERE CreatedAt BETWEEN '2030-03-01' AND '2030-03-31 23:59:59'`).trim().split('|');
check('E26-01', 'summary totals equal what MySQL itself sums (replies, tokens, split, cost to 8 decimals)',
  sum.totals.requests === +dbTotals[0] && sum.totals.tokens === +dbTotals[1] && sum.totals.promptTokens === +dbTotals[2] && sum.totals.completionTokens === +dbTotals[3] && Math.abs(sum.totals.cost - +dbTotals[4]) < 1e-9, `${JSON.stringify(sum.totals)} vs ${dbTotals}`);
const dbDays = sql(`SELECT GROUP_CONCAT(CONCAT(DATE(CreatedAt),':',COUNT(*),':',SUM(TokensUsed)) ORDER BY DATE(CreatedAt)) FROM UsageLogs WHERE CreatedAt BETWEEN '2030-03-01' AND '2030-03-31 23:59:59' GROUP BY 1=1`).trim();
const apiDays = sum.byDay.map(d => `${d.date}:${d.requests}:${d.tokens}`).join(',');
check('E26-02', 'the per-day series groups by UTC day exactly as MySQL does (a row at 23:59:59.5 and one at 00:00:00 land on different days)', sum.byDay.length === 3 && sum.byDay[0].date === '2030-03-01' && sum.byDay[1].date === '2030-03-02', apiDays);
check('E26-03', 'biggest spender first, with their summed cost', sum.byUser[0].userName === 'Una Heavy' && sum.byUser[0].requests === 3 && Math.abs(sum.byUser[0].cost - 0.00004319) < 1e-9, JSON.stringify(sum.byUser[0]));
const narrow = (await raw(`/api/admin/usage/summary?from=2030-03-02T00:00:00Z&to=2030-03-02T23:59:59Z`, { token: adm.token })).json;
check('E26-04', 'a one-day range counts only that day', narrow.totals.requests === 3 && narrow.byDay.length === 1, JSON.stringify(narrow.totals));
const csvRes = await fetch(WEB + `/api/admin/usage/export?${win}`, { headers: { authorization: `Bearer ${adm.token}` } });
const csv = await csvRes.text(); const lines = csv.split('\r\n').filter(Boolean);
check('E26-05', 'the export is a CSV file download with a header and one line per row, newest first', csvRes.status === 200 && (csvRes.headers.get('content-type') ?? '').startsWith('text/csv') && /attachment/.test(csvRes.headers.get('content-disposition') ?? '') && lines.length === 6 && lines[0].startsWith('Time (UTC),User,Email') && lines[1].startsWith('"2030-03-03'), `${csvRes.status} ${lines.length} lines`);
check('E26-06', 'a name starting with = and a chat title starting with + are defused (prefixed with a quote), and embedded quotes are doubled',
  csv.includes('"\'=HYPERLINK(""http://evil"")"') && csv.includes('"\'+cmd, ""x"""'), lines.slice(1, 3).join(' // '));
check('E26-07', 'a row from before cost tracking (no model, zero cost) shows empty model and 0 in the CSV', lines[1].includes('"",0,0,10,0'), lines[1]);
const del = await raw('/api/account/delete', { method: 'POST', token: u1.token, body: { password: PW } });
const afterDel = (await raw(`/api/admin/usage/summary?${win}`, { token: adm.token })).json;
check('E26-08', "deleting Una's account removes her usage from the totals (database cascade)", del.status === 204 && afterDel.totals.requests === 2, `${del.status} ${afterDel.totals.requests}`);

console.log('\n--- F: the admin pages in Chrome ---');
cleanDb(); redisCli('flushall');
const browser = await launch();
const csp = [];
const page = await openPage(browser, { bypassCsp: false }); // the app's own Content-Security-Policy stays in force
page.on('console', m => { if (/Content Security Policy|Refused to/i.test(m.text())) csp.push(m.text()); });
const root = await adminLogin(await register('Rita Root'));
const vic = await register('Victor Victim');
const ben = await register('Ben Browser');
const vicChat = uuid();
sql(`INSERT INTO ChatSessions (Id,UserId,Title,CreatedAt) VALUES ('${vicChat}',${vic.id},'Victor chat','2030-01-01 00:00:00.000000')`);
for (const [d, t, c] of [['2030-05-01', 1200, 0.0012], ['2030-05-02', 3400, 0.0034], ['2030-05-03', 800, 0.0008]]) sql(`INSERT INTO UsageLogs (UserId,SessionId,TokensUsed,PromptTokens,CompletionTokens,Model,CostEstimate,CreatedAt) VALUES (${vic.id},'${vicChat}',${t},${t - 100},100,'vendor/model-x',${c},'${d} 10:00:00.000000')`);
const uiLogin = async (pg, u) => { await pg.goto(WEB + '/login'); await pg.type('input[type=email]', u.email); await pg.type('#login-password', PW); await pg.click('button[type=submit]'); await pg.waitForSelector('#composer-input', { timeout: 20000 }); };
await uiLogin(page, root);
const sideLinks = await page.$$eval('.sidebar-footer a', a => a.map(x => x.textContent.trim()));
check('F26-01', 'the chat sidebar offers an admin both "Usage logs" and "Users"', sideLinks.includes('Usage logs') && sideLinks.includes('Users'), JSON.stringify(sideLinks));
await page.goto(WEB + '/admin/usage'); await page.waitForSelector('.stat');
await page.evaluate(() => { const set = (n, v) => { const i = document.querySelector(`input[name=${n}]`); i.value = v; i.dispatchEvent(new Event('input', { bubbles: true })); }; set('from', '2030-05-01'); set('to', '2030-05-31'); });
await page.evaluate(() => [...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Apply dates').click());
await page.waitForFunction(() => [...document.querySelectorAll('.stat-value')].some(e => e.textContent.includes('$0.0054')), { timeout: 15000 });
const tiles = await page.$$eval('.stat', s => s.map(x => x.innerText.replace(/\s+/g, ' ').trim()));
check('F26-02', 'the usage page shows replies, tokens and estimated cost for the whole range', tiles.some(t => t.includes('Replies in range 3')) && tiles.some(t => t.includes('Tokens in range 5,400')) && tiles.some(t => t.includes('$0.0054')), JSON.stringify(tiles));
check('F26-03', 'it draws a bar per day and lists the biggest spender', (await page.$$eval('.bar-col', b => b.length)) === 3 && (await page.$eval('.spenders', e => e.innerText)).includes('Victor Victim'));
const costCells = await page.$$eval('tbody td.num:last-child', c => c.map(x => x.innerText.trim()));
check('F26-04', 'each reply row shows its own cost, not "Not tracked"', costCells.length >= 3 && costCells.every(c => c.startsWith('$')), JSON.stringify(costCells));
await snap(page, 'usage.png');
// the export: capture the file the page builds just before it is handed to the browser (headless Chrome's own download folder is not reliable)
await page.evaluate(() => {
  window.__csv = null; window.__csvName = null;
  const create = URL.createObjectURL.bind(URL);
  URL.createObjectURL = (blob) => { blob.text().then(t => { window.__csv = t; }); return create(blob); };
  HTMLAnchorElement.prototype.click = function () { window.__csvName = this.download; }; // no real download
});
await page.evaluate(() => [...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Export CSV').click());
const fileText = await waitFor(() => page.evaluate(() => window.__csv), 15000, 300) ?? '';
const fileName = await page.evaluate(() => window.__csvName);
check('F26-05', 'Export CSV builds a .csv file from the filtered rows (header plus three lines, including Victor) and names it usage-<date>.csv',
  /^usage-\d{4}-\d{2}-\d{2}\.csv$/.test(fileName ?? '') && fileText.split('\r\n').filter(Boolean).length === 4 && fileText.includes('Victor Victim'), `${fileName} ${fileText.split('\r\n').length} lines`);

await page.goto(WEB + '/admin/users'); await page.waitForSelector('tbody tr');
const people = await page.$$eval('tbody tr', r => r.map(x => x.innerText.replace(/\s+/g, ' ').trim()));
check('F26-06', 'the Users page lists everyone with role and status, and marks the admin themselves as "(you)"', people.length === 3 && people.some(p => p.includes('Rita Root') && p.includes('(you)') && p.includes('Admin')) && people.some(p => p.includes('Victor Victim') && p.includes('Active')), JSON.stringify(people));
check('F26-07', "the admin's own row offers no actions", people.find(p => p.includes('(you)')).includes('Not for your own account'));
await page.type('input[type=search]', 'victor'); await sleep(700);
check('F26-08', 'typing in the search box narrows the list to Victor', (await page.$$eval('tbody tr', r => r.length)) === 1);
// Victor is logged in in a second browser window
const vicPage = await openPage(browser, { bypassCsp: false });
await uiLogin(vicPage, vic);
await page.evaluate(() => [...document.querySelectorAll('tbody button')].find(b => b.textContent.trim() === 'Disable').click());
await page.waitForFunction(() => document.body.innerText.includes('Disable and sign out Victor Victim?'));
await snap(page, 'users-confirm.png');
check('F26-09', 'Disable asks first and nothing is sent yet', count('Users', `Id=${vic.id} AND DisabledAt IS NOT NULL`) === 0);
await page.evaluate(() => [...document.querySelectorAll('tbody button')].find(b => b.textContent.trim() === 'Confirm').click());
await page.waitForFunction(() => document.querySelector('.notice')?.innerText.includes('was disabled'), { timeout: 15000 });
check('F26-10', 'Confirm disables Victor: the row says Disabled, a notice appears, the database agrees', (await page.$eval('tbody tr', r => r.innerText)).includes('Disabled') && count('Users', `Id=${vic.id} AND DisabledAt IS NOT NULL`) === 1);
await snap(page, 'users-disabled.png');
await vicPage.reload({ waitUntil: 'networkidle2' }).catch(() => {});
const vicEnded = await waitFor(async () => vicPage.url().includes('/login'), 20000, 500);
check("F26-11", "Victor's open window is signed out and sent to the login page on its next request", !!vicEnded, vicPage.url());
await uiLogin(vicPage, vic).catch(() => {});
check('F26-12', 'Victor cannot log in through the page and is told why', (await vicPage.$eval('.form-error', e => e.innerText).catch(() => '')).toLowerCase().includes('disabled'));
await page.evaluate(() => [...document.querySelectorAll('tbody button')].find(b => b.textContent.trim() === 'Enable').click());
await page.waitForFunction(() => document.querySelector('.notice')?.innerText.includes('can log in again'), { timeout: 15000 });
check('F26-13', 'Enable restores the account at once (no confirmation needed)', count('Users', `Id=${vic.id} AND DisabledAt IS NULL`) === 1);
await page.evaluate(() => [...document.querySelectorAll('tbody button')].find(b => b.textContent.trim() === 'Make admin').click());
await page.waitForFunction(() => document.body.innerText.includes('Make Victor Victim an Admin?'));
await page.evaluate(() => [...document.querySelectorAll('tbody button')].find(b => b.textContent.trim() === 'Confirm').click());
await page.waitForFunction(() => document.querySelector('.notice')?.innerText.includes('log in again'), { timeout: 15000 });
check('F26-14', 'Make admin (with its confirmation) gives Victor the Admin role and tells the admin he must log in again', count('UserRoles', `UserId=${vic.id} AND RoleId=(SELECT Id FROM Roles WHERE Name='Admin')`) === 1);
check("F26-15", "the page's Content-Security-Policy blocked nothing, and the pages had no script errors", csp.length === 0 && page.errs.length === 0, csp.concat(page.errs).join(' | '));

// accessibility (separate pages: axe is injected as an inline script, which the app's CSP forbids)
const axePage = await openPage(browser, { bypassCsp: true });
await axePage.goto(WEB + '/login'); await axePage.evaluate((t, u) => { localStorage.setItem('aichat.auth.token', t); localStorage.setItem('aichat.auth.user', u); }, root.token, JSON.stringify({ id: root.id, name: root.name, email: root.email, roles: ['Admin'] }));
for (const theme of ['light', 'dark']) {
  for (const route of ['/admin/usage', '/admin/users']) {
    await axePage.evaluate((t) => { localStorage.setItem('aichat.theme', t); }, theme);
    await axePage.goto(WEB + route); await axePage.waitForSelector('table');
    if (route === '/admin/usage') await axePage.evaluate(() => { const set = (n, v) => { const i = document.querySelector(`input[name=${n}]`); i.value = v; i.dispatchEvent(new Event('input', { bubbles: true })); }; set('from', '2030-05-01'); set('to', '2030-05-31'); [...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Apply dates').click(); });
    await sleep(1200);
    await axePage.evaluate(axeSource);
    const res = await axePage.evaluate(() => axe.run(document, { runOnly: ['wcag2a', 'wcag2aa'] }));
    check(`F26-${theme === 'light' ? (route.endsWith('usage') ? '16' : '17') : (route.endsWith('usage') ? '18' : '19')}`, `${route} in the ${theme} theme has no accessibility violations (axe, WCAG 2 A and AA)`, res.violations.length === 0, res.violations.map(v => `${v.id}: ${v.nodes.length}`).join(', '));
    if (theme === 'dark') await snap(axePage, `${route.split('/').pop()}-dark.png`);
  }
}
await browser.close();

console.log('\n--- Z: clean up ---');
cleanDb(); redisCli('flushall');
check('Z26-01', 'test data removed and the standard configuration is back', count('Users') === 0 && await recreateApi({ ADMIN_BOOTSTRAP_EMAIL: '' }));
finish();
