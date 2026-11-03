import fs from 'node:fs';
import path from 'node:path';
import { check, finish, sleep, waitFor, newUser, sql, count, cleanDb, redisCli, launch, openPage, WEB, ROOT } from '../lib.mjs';

// ---- Phase 24: chat management and paging on the normal stack (real MySQL, real nginx and CSP, real Chrome) ----
// A schema   B title search on real MySQL (case, wildcards, quotes)   C soft delete keeps messages and usage logs; the account delete still cascades
// D paging of 120 chats and 130 messages with no gaps or repeats   E the real UI: scroll for more, search, rename, delete with confirmation, scroll up in a thread
// No LLM is called: chats and messages are written straight into the Docker stack's own database (never the host dev database), and cleaned at the end.
const shots = path.join(ROOT, 'e2e', 'shots', 'phase24'); fs.mkdirSync(shots, { recursive: true });
const PW = 'Passw0rd!x';
const raw = async (p, { method = 'GET', body, token } = {}) => {
  const r = await fetch(WEB + p, { method, headers: { 'content-type': 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}) }, body: body ? JSON.stringify(body) : undefined });
  const text = await r.text(); let json = null; try { json = JSON.parse(text); } catch { /* no body */ }
  return { status: r.status, json, text };
};
const snap = (p, n) => p.screenshot({ path: path.join(shots, n) });
const uuid = () => crypto.randomUUID();
const esc = (s) => s.replace(/'/g, "''");
const addChat = (userId, title, createdAt = '2026-01-01 00:00:00.000000') => { const id = uuid(); sql(`INSERT INTO ChatSessions (Id,UserId,Title,CreatedAt) VALUES ('${id}',${userId},'${esc(title)}','${createdAt}')`); return id; };
const addMessages = (sessionId, n) => sql(`INSERT INTO Messages (SessionId,Role,Content,CreatedAt) WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<${n}) SELECT '${sessionId}', IF(i%2=1,'user','assistant'), CONCAT('message ',LPAD(i,3,'0')), TIMESTAMPADD(SECOND,i,'2026-01-01 00:00:00.000000') FROM n`);
const addManyChats = (userId, n) => sql(`INSERT INTO ChatSessions (Id,UserId,Title,CreatedAt) WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<${n}) SELECT UUID(), ${userId}, CONCAT('Chat ',LPAD(i,3,'0')), TIMESTAMPADD(MINUTE,i,'2026-01-01 00:00:00.000000') FROM n`);
const titlesOf = (r) => (r.json ?? []).map(s => s.title);
const uiLogin = async (page, email, password) => { await page.goto(WEB + '/login'); await page.type('input[type=email]', email); await page.type('#login-password', password); await page.click('button[type=submit]'); await page.waitForSelector('#composer-input', { timeout: 20000 }); };
const rows = (page) => page.$$eval('.session-item', els => els.length);

cleanDb(); redisCli('flushall');

console.log('\n--- A: the schema the migration made, on real MySQL ---');
const col = sql(`SELECT CONCAT(COLUMN_TYPE,'/',IS_NULLABLE) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='AiChatAssistant' AND TABLE_NAME='ChatSessions' AND COLUMN_NAME='DeletedAt'`).trim();
check('A24-01', 'ChatSessions.DeletedAt exists as a nullable datetime(6)', col === 'datetime(6)/YES', col);
const idx = sql(`SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA='AiChatAssistant' AND TABLE_NAME='ChatSessions' AND INDEX_NAME='IX_ChatSessions_UserId_DeletedAt_CreatedAt'`).trim();
check('A24-02', 'the list index (UserId, DeletedAt, CreatedAt) exists', idx === 'UserId,DeletedAt,CreatedAt', idx);

console.log('\n--- B: title search on real MySQL ---');
const ann = await newUser('Ann Search', WEB);
for (const t of ['Trip to Lisbon', '100% done', 'a_b', 'axb', "O'Brien notes"]) addChat(ann.id, t);
const search = async (q) => titlesOf(await raw(`/api/chat/sessions?search=${encodeURIComponent(q)}`, { token: ann.token }));
check('B24-01', 'search ignores case and finds part of a title', JSON.stringify(await search('LISBON')) === JSON.stringify(['Trip to Lisbon']), JSON.stringify(await search('LISBON')));
check('B24-02', 'a % in the search is a plain character, not a wildcard', JSON.stringify(await search('%')) === JSON.stringify(['100% done']), JSON.stringify(await search('%')));
check('B24-03', 'an _ in the search is a plain character, not "any one character"', JSON.stringify(await search('a_b')) === JSON.stringify(['a_b']), JSON.stringify(await search('a_b')));
check('B24-04', 'a quote in a title can be searched for', JSON.stringify(await search("O'Brien")) === JSON.stringify(["O'Brien notes"]), JSON.stringify(await search("O'Brien")));
const inj = await raw(`/api/chat/sessions?search=${encodeURIComponent("'; DROP TABLE ChatSessions; --")}`, { token: ann.token });
check('B24-05', 'an injection attempt matches nothing and the table is intact', inj.status === 200 && titlesOf(inj).length === 0 && count('ChatSessions', `UserId=${ann.id}`) === 5, `${inj.status}, rows ${count('ChatSessions', `UserId=${ann.id}`)}`);
const bob = await newUser('Bob Other', WEB);
addChat(bob.id, 'Lisbon secrets');
check('B24-06', "search never returns another person's chat", (await search('lisbon')).length === 1);

console.log('\n--- C: soft delete on real MySQL ---');
const cat = await newUser('Cat Delete', WEB);
const doomed = addChat(cat.id, 'Doomed chat'); addMessages(doomed, 4);
sql(`INSERT INTO UsageLogs (UserId,SessionId,TokensUsed,CostEstimate,CreatedAt) VALUES (${cat.id},'${doomed}',42,0,NOW(6))`);
const del = await raw(`/api/chat/sessions/${doomed}`, { method: 'DELETE', token: cat.token });
check('C24-01', 'DELETE answers 204', del.status === 204, String(del.status));
check('C24-02', 'the row stays with DeletedAt set; its messages and usage log are kept',
  Number(sql(`SELECT COUNT(*) FROM ChatSessions WHERE Id='${doomed}' AND DeletedAt IS NOT NULL`)) === 1 && count('Messages', `SessionId='${doomed}'`) === 4 && count('UsageLogs', `SessionId='${doomed}' AND TokensUsed=42`) === 1);
const after = await Promise.all([
  raw('/api/chat/sessions', { token: cat.token }),
  raw(`/api/chat/sessions/${doomed}/messages`, { token: cat.token }),
  raw(`/api/chat/sessions/${doomed}/messages`, { method: 'POST', token: cat.token, body: { content: 'anyone there?' } }),
  raw(`/api/chat/sessions/${doomed}`, { method: 'PATCH', token: cat.token, body: { title: 'x' } }),
  raw(`/api/chat/sessions/${doomed}`, { method: 'DELETE', token: cat.token }),
]);
check('C24-03', 'the list is empty and read, send, rename and a second delete all answer 404 in the standard shape',
  titlesOf(after[0]).length === 0 && after.slice(1).every(r => r.status === 404 && r.json?.error?.code === 'session_not_found'), after.map(r => r.status).join(','));
check('C24-04', 'sending to the deleted chat saved nothing', count('Messages', `SessionId='${doomed}'`) === 4);
const other = addChat(cat.id, 'Cat private');
const intruder = await Promise.all([raw(`/api/chat/sessions/${other}`, { method: 'PATCH', token: bob.token, body: { title: 'hacked' } }), raw(`/api/chat/sessions/${other}`, { method: 'DELETE', token: bob.token })]);
check('C24-05', "someone else cannot rename or delete a chat: 404, and it is untouched", intruder.every(r => r.status === 404) && sql(`SELECT Title FROM ChatSessions WHERE Id='${other}'`).trim() === 'Cat private' && Number(sql(`SELECT COUNT(*) FROM ChatSessions WHERE Id='${other}' AND DeletedAt IS NOT NULL`)) === 0);
const ren = await raw(`/api/chat/sessions/${other}`, { method: 'PATCH', token: cat.token, body: { title: '  Renamed chat  ' } });
check('C24-06', 'a rename is trimmed and saved', ren.status === 200 && sql(`SELECT Title FROM ChatSessions WHERE Id='${other}'`).trim() === 'Renamed chat');
const bad = await Promise.all([raw(`/api/chat/sessions/${other}`, { method: 'PATCH', token: cat.token, body: { title: '   ' } }), raw(`/api/chat/sessions/${other}`, { method: 'PATCH', token: cat.token, body: { title: 'a'.repeat(201) } })]);
check('C24-07', 'an empty or 201-character title is a 400 and changes nothing', bad.every(r => r.status === 400 && r.json?.error) && sql(`SELECT Title FROM ChatSessions WHERE Id='${other}'`).trim() === 'Renamed chat');
const gone = await raw('/api/account/delete', { method: 'POST', token: cat.token, body: { password: PW } });
check('C24-08', 'deleting the account still removes everything, including the soft-deleted chat, its messages and its usage log',
  gone.status === 204 || gone.status === 200, String(gone.status));
check('C24-09', '... and nothing of Cat is left in the database', count('ChatSessions', `UserId=${cat.id}`) === 0 && count('Messages', `SessionId='${doomed}'`) === 0 && count('UsageLogs', `UserId=${cat.id}`) === 0);

console.log('\n--- D: paging 120 chats and 130 messages with no gaps or repeats ---');
const dan = await newUser('Dan Pages', WEB);
addManyChats(dan.id, 120);
const big = addChat(dan.id, 'Big thread', '2025-12-31 00:00:00.000000'); addMessages(big, 130);
const def = await raw('/api/chat/sessions', { token: dan.token });
check('D24-01', 'the chat list with no parameters is the newest 50 (Chat 120 first)', titlesOf(def).length === 50 && titlesOf(def)[0] === 'Chat 120', `${titlesOf(def).length}, first ${titlesOf(def)[0]}`);
const cap = await raw('/api/chat/sessions?limit=100000', { token: dan.token });
check('D24-02', 'a huge limit is cut to 100', titlesOf(cap).length === 100, String(titlesOf(cap).length));
const walked = []; let before = null;
for (let i = 0; i < 10; i++) { const r = await raw(`/api/chat/sessions?limit=30${before ? `&before=${encodeURIComponent(before)}` : ''}`, { token: dan.token }); if (!r.json?.length) break; walked.push(...r.json); before = r.json.at(-1).createdAt; }
const wt = walked.map(s => s.title);
check('D24-03', 'walking the chat list 30 at a time reaches all 121 chats once, newest first', walked.length === 121 && new Set(walked.map(s => s.id)).size === 121 && wt[0] === 'Chat 120' && wt[119] === 'Chat 001' && wt[120] === 'Big thread', `${walked.length} chats, ${new Set(walked.map(s => s.id)).size} unique`);
const m0 = await raw(`/api/chat/sessions/${big}/messages`, { token: dan.token });
check('D24-04', 'a thread with no parameters returns the newest 50, oldest first (message 081 to 130)', m0.json.length === 50 && m0.json[0].content === 'message 081' && m0.json.at(-1).content === 'message 130', `${m0.json.length}, ${m0.json[0]?.content}..${m0.json.at(-1)?.content}`);
const all = []; let cursor = null;
for (let i = 0; i < 10; i++) { const r = await raw(`/api/chat/sessions/${big}/messages?limit=40${cursor ? `&before=${cursor}` : ''}`, { token: dan.token }); if (!r.json?.length) break; all.unshift(...r.json); cursor = r.json[0].id; }
check('D24-05', 'walking a thread 40 at a time upwards gives all 130 messages in order, none missing or repeated', all.length === 130 && all.every((m, i) => m.content === `message ${String(i + 1).padStart(3, '0')}`), `${all.length} messages`);
const capM = await raw(`/api/chat/sessions/${big}/messages?limit=9999`, { token: dan.token });
check('D24-06', 'a huge message limit is cut to 100', capM.json.length === 100, String(capM.json.length));
const junk = await raw('/api/chat/sessions?limit=lots', { token: dan.token });
check('D24-07', 'a non-numeric limit is a 400 in the standard shape, not a 500', junk.status === 400 && !!junk.json?.error, `${junk.status}`);
check('D24-08', "paging someone else's thread is a 404", (await raw(`/api/chat/sessions/${big}/messages?limit=5`, { token: bob.token })).status === 404);

console.log('\n--- E: the real UI in Chrome, with the CSP on ---');
const browser = await launch();
const page = await openPage(browser, { width: 1280, height: 820, bypassCsp: false });
await uiLogin(page, dan.email, PW);
await page.waitForSelector('.session-item');
check('E24-01', 'the sidebar starts with the newest 50 chats', (await rows(page)) === 50, String(await rows(page)));
await page.evaluate(() => { const l = document.querySelector('.session-list'); l.scrollTop = l.scrollHeight; });
check('E24-02', 'scrolling to the bottom of the list loads the next page (50 to 100)', !!(await waitFor(async () => (await rows(page)) === 100, 8000)), String(await rows(page)));
await page.evaluate(() => { const l = document.querySelector('.session-list'); l.scrollTop = l.scrollHeight; });
check('E24-03', '... and again until the end (all 121 chats), with no "Show more" button left', !!(await waitFor(async () => (await rows(page)) === 121, 8000)) && !(await page.$('button.more')), String(await rows(page)));
await snap(page, '1-sidebar-all-loaded.png');

await page.type('#chat-search', 'Chat 05');
await sleep(500);
const found = await page.$$eval('.session-item', els => els.map(e => e.textContent.trim()));
check('E24-04', 'typing in the search box lists only matching chats (Chat 050 to 059)', found.length === 10 && found.every(t => t.startsWith('Chat 05')), `${found.length}: ${found.slice(0, 3).join(', ')}...`);
await snap(page, '2-search.png');
await page.click('#chat-search', { clickCount: 3 }); await page.type('#chat-search', 'nothing like this'); await sleep(500);
check('E24-05', 'a search with no match says so and offers to clear it', (await page.$eval('.session-list', e => e.textContent)).includes('No chats match'));
await page.click('#chat-search', { clickCount: 3 }); await page.keyboard.press('Backspace'); await sleep(500);
check('E24-06', 'clearing the search brings the normal list back', (await rows(page)) === 50, String(await rows(page)));

// open the big thread (search for it, then click)
await page.type('#chat-search', 'Big thread'); await sleep(500);
await page.click('.session-item');
await page.waitForSelector('.message');
check('E24-07', 'opening a long chat shows the newest 50 messages and a "Show earlier messages" button', (await page.$$eval('.message', e => e.length)) === 50 && !!(await page.$('button.older')));
await page.evaluate(() => { const l = document.querySelector('.message-list'); l.scrollTop = l.scrollHeight; }); await sleep(300);
// scroll to the top and read where the first message sits in the same breath, before the earlier page can arrive
const anchorBefore = await page.evaluate(() => { const l = document.querySelector('.message-list'); l.scrollTop = 0; const first = document.querySelector('.message .text'); return { top: first.getBoundingClientRect().top - l.getBoundingClientRect().top, text: first.textContent.trim() }; });
const loadedUp = await waitFor(async () => (await page.$$eval('.message', e => e.length)) === 100, 8000);
await sleep(300);
const anchorAfter = await page.evaluate((txt) => { const l = document.querySelector('.message-list'); const el = Array.from(document.querySelectorAll('.message .text')).find(e => e.textContent.trim() === txt); return el ? el.getBoundingClientRect().top - l.getBoundingClientRect().top : null; }, anchorBefore.text);
check('E24-08', 'scrolling to the top loads 50 earlier messages (100 on screen)', !!loadedUp);
check('E24-09', 'the message being read did not jump: it is still in the same place on screen, within 40px (reading position kept)', anchorAfter !== null && Math.abs(anchorAfter - anchorBefore.top) < 40, `before ${Math.round(anchorBefore.top)}px, after ${anchorAfter === null ? 'missing' : Math.round(anchorAfter)}px, text "${anchorBefore.text}"`);
await snap(page, '3-thread-scrolled-up.png');
await page.evaluate(() => { document.querySelector('.message-list').scrollTop = 0; }); await sleep(500);
await page.evaluate(() => { document.querySelector('.message-list').scrollTop = 0; });
await waitFor(async () => (await page.$$eval('.message', e => e.length)) === 130, 8000);
check('E24-10', 'one more scroll up reaches all 130 messages and the button disappears', (await page.$$eval('.message', e => e.length)) === 130 && !(await page.$('button.older')));

await page.click('#chat-search', { clickCount: 3 }); await page.keyboard.press('Backspace'); await sleep(500);
await page.type('#chat-search', 'Chat 120'); await sleep(500);
await page.hover('.session-row');
await page.click('.session-row .rename');
await page.waitForSelector('.rename-input');
// the input selects the old title when it opens, so typing replaces it
await page.type('.rename-input', 'Renamed from the browser'); await page.keyboard.press('Enter');
await waitFor(async () => sql(`SELECT COUNT(*) FROM ChatSessions WHERE UserId=${dan.id} AND Title='Renamed from the browser'`).trim() === '1', 8000);
check('E24-11', 'renaming in the sidebar saves to the database', sql(`SELECT COUNT(*) FROM ChatSessions WHERE UserId=${dan.id} AND Title='Renamed from the browser'`).trim() === '1');
await page.reload(); await page.waitForSelector('.session-item');
check('E24-12', 'the new name is still there after a reload', (await page.$$eval('.session-item', e => e.map(x => x.textContent.trim()))).includes('Renamed from the browser'));

await page.hover('.session-row');
await page.click('.session-row .delete');
await page.waitForSelector('[role=alertdialog]');
const focused = await page.evaluate(() => document.activeElement?.textContent.trim());
check('E24-13', 'Delete opens a confirmation that names the chat and puts the focus on Cancel', focused === 'Cancel' && (await page.$eval('#delete-text', e => e.textContent)).includes('Renamed from the browser'), `focus on "${focused}"`);
await snap(page, '4-confirm-delete.png');
await page.keyboard.press('Escape'); await sleep(200);
check('E24-14', 'Escape closes it and nothing is deleted', !(await page.$('[role=alertdialog]')) && Number(sql(`SELECT COUNT(*) FROM ChatSessions WHERE UserId=${dan.id} AND DeletedAt IS NOT NULL`)) === 0);
await page.hover('.session-row'); await page.click('.session-row .delete'); await page.waitForSelector('[role=alertdialog]');
await page.evaluate(() => Array.from(document.querySelectorAll('[role=alertdialog] button')).find(b => b.textContent.includes('Delete chat')).click());
await waitFor(async () => Number(sql(`SELECT COUNT(*) FROM ChatSessions WHERE UserId=${dan.id} AND DeletedAt IS NOT NULL`)) === 1, 8000);
await sleep(300);
check('E24-15', 'confirming deletes it (soft delete in MySQL) and it leaves the sidebar', Number(sql(`SELECT COUNT(*) FROM ChatSessions WHERE UserId=${dan.id} AND DeletedAt IS NOT NULL`)) === 1 && !(await page.$$eval('.session-item', e => e.map(x => x.textContent.trim()))).includes('Renamed from the browser'));
await snap(page, '5-after-delete.png');
check('E24-16', 'no script errors and no CSP violations on the page', page.errs.length === 0, page.errs.join(' | '));

await browser.close();
cleanDb(); redisCli('flushall');
check('Z24-01', 'the test data was removed from the Docker database', count('Users') === 0 && count('ChatSessions') === 0);
finish();
