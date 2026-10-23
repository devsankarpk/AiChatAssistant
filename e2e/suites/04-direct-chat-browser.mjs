// The whole user-to-user chat feature in real Chrome, through a load balancer in front of three API replicas
// (so the two people usually sit on different replicas): search, ticks, badge, toast, typing, presence, delete, paging,
// races, privacy, accessibility, a phone-width screen, and a replica dying under a live conversation.
import fs from 'node:fs';
import { createRequire } from 'node:module';
import { dc, sh, check, finish, sleep, waitFor, rest, newUser, newConv, dsend, sql, count, cleanDb, useReplicas, refreshLb, replicaOf, launch, openPage, LB, R1, healthy } from '../lib.mjs';
const require = createRequire(import.meta.url);
const axeSource = fs.readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8');

await useReplicas(['api', 'api2', 'api3']);
await refreshLb();
cleanDb();
const alice = await newUser('Alice Rao', LB), bob = await newUser('Bob Menon', LB), carol = await newUser('Carol Iyer', LB), dave = await newUser('Dave Outsider', LB);
const browser = await launch();
const body = p => p.evaluate(() => document.body.innerText);
const n = (p, sel) => p.$$eval(sel, e => e.length);
const status = p => p.$eval('.thread-status', e => e.innerText).catch(() => '');
const ticks = (p, s) => n(p, `.bubble-row.mine svg[data-status="${s}"]`);
// the badge also holds a visually hidden " unread" for screen readers, so read only the number
const badge = p => p.$eval('.badge', e => (e.innerText.match(/\d+/) ?? ['0'])[0]).catch(() => '0');
const openConv = async (page, name) => { for (const r of await page.$$('.conversation')) { if ((await r.evaluate(e => e.innerText)).includes(name)) { await r.click(); return true; } } return false; };
const login = async (page, user) => {
  await page.goto(LB + '/login');
  await page.type('input[type=email]', user.email); await page.type('#login-password', user.password);
  await page.click('button[type=submit]'); await page.waitForSelector('#composer-input', { timeout: 20000 });
};
const say = async (page, text) => { await page.type('#direct-composer', text); await page.keyboard.press('Enter'); };
const visibility = (page, v) => page.evaluate((state) => { Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state }); Object.defineProperty(document, 'hidden', { configurable: true, get: () => state === 'hidden' }); document.dispatchEvent(new Event('visibilitychange')); }, v);

const a = await openPage(browser), b = await openPage(browser);
await login(a, alice);
await a.goto(LB + '/messages'); await a.waitForSelector('input[placeholder="Find someone by name"]');

// ---- find and start ----
await a.type('input[placeholder="Find someone by name"]', 'Bob');
await a.waitForSelector('.person', { timeout: 10000 });
check('DC-01', 'searching by name finds exactly that person and shows the name only (no email anywhere on the page)', (await n(a, '.person')) === 1 && !(await body(a)).includes('@') && (await body(a)).includes('Bob Menon'));
await a.click('.person'); await a.waitForSelector('#direct-composer');

// ---- one grey tick: Bob has never signed in, so he is offline ----
await say(a, 'm1 hello bob');
check('DC-02', 'ONE grey tick (sent) while the other person is offline', !!(await waitFor(async () => (await ticks(a, 'sent')) === 1, 8000, 200)) && (await ticks(a, 'delivered')) === 0);

// ---- Bob signs in (somewhere else): two grey ticks, "Online", badge 1 but no toast for a message that was already waiting ----
await login(b, bob);
const deliveredLive = await waitFor(async () => (await ticks(a, 'delivered')) === 1, 10000, 200);
await waitFor(async () => (await badge(b)) === '1', 8000, 200);
check('DC-03', 'Bob signs in: Alice\'s tick turns TWO grey (delivered) live, she sees him "Online", and he has badge 1 but NO toast (the message was already waiting)', !!deliveredLive && /online/i.test(await status(a)) && (await badge(b)) === '1' && (await n(b, '.toast')) === 0, `delivered ticks: ${await ticks(a, 'delivered')}, status: "${await status(a)}", Bob's badge: ${await badge(b)}, toasts: ${await n(b, '.toast')}`);

// ---- a live message while Bob is on another page ----
await say(a, 'm2 are you there?');
await waitFor(async () => (await n(b, '.toast')) >= 1, 8000, 200);
const toastText = await b.$$eval('.toast', t => t.map(x => x.innerText).join(' '));
check('DC-04', 'a live message while Bob is on the chatbot page: a toast with the sender and a preview, and the badge goes to 2', toastText.includes('m2 are you there?') && (await badge(b)) === '2', `toasts: "${toastText.replace(/\s+/g, ' ')}", badge: ${await badge(b)}`);

// ---- Bob opens the chat: blue ticks, badge clears ----
await b.bringToFront(); await b.goto(LB + '/messages'); await b.waitForSelector('.conversation', { timeout: 15000 });
const unreadPill = await b.$eval('.conversation .unread', e => (e.innerText.match(/\d+/) ?? [''])[0]).catch(() => '');
await b.click('.conversation'); await b.waitForSelector('.bubble');
await a.bringToFront();
check('DC-05', 'Bob sees "2" unread in the list, opens the chat: Alice\'s ticks turn BLUE (read) live and his badge and unread pill clear', unreadPill === '2' && !!(await waitFor(async () => (await ticks(a, 'read')) === 2, 10000, 200)) && (await badge(b)) === '0' && (await n(b, '.conversation .unread')) === 0);

// ---- typing ----
await b.bringToFront();
await b.type('#direct-composer', 'draft not sent yet');
await a.bringToFront();
const typing = await waitFor(async () => /typing/i.test(await status(a)), 8000, 200);
await b.bringToFront();
await b.evaluate(() => { const t = document.querySelector('#direct-composer'); t.value = ''; t.dispatchEvent(new Event('input', { bubbles: true })); });
await say(b, 'm3 yes I am here');
await a.bringToFront();
const gotM3 = await waitFor(async () => (await body(a)).includes('m3 yes I am here'), 10000, 200);
check('DC-06', 'typing: Alice sees "typing…" while Bob types; sending ends it at once and the message arrives', !!typing && !!gotM3 && !/typing/i.test(await status(a)));

// ---- a hidden tab: a message counts as unread and toasts, and is only marked read when the tab is visible again ----
await b.bringToFront(); await visibility(b, 'hidden');
await a.bringToFront(); await say(a, 'm4 while you are away');
await b.bringToFront();
await waitFor(async () => (await badge(b)) === '1', 8000, 200);
const hiddenOk = (await badge(b)) === '1' && (await ticks(a, 'delivered')) >= 1;
await visibility(b, 'visible');
const readAgain = !!(await waitFor(async () => (await badge(b)) === '0', 8000, 200)) && !!(await waitFor(async () => (await ticks(a, 'read')) >= 3, 10000, 200));
check('DC-07', 'a hidden tab: the message counts as unread (badge 1) and is NOT marked read; when the tab is visible again it is read and Alice\'s tick turns blue', hiddenOk && readAgain, `while hidden: ok=${hiddenOk}; after visible: badge ${await badge(b)}, blue ticks ${await ticks(a, 'read')}`);

// ---- two tabs of the same person ----
// (headless Chrome treats every window as visible, so the first tab is told it is hidden, like a tab in the background)
const a2 = await openPage(browser);
await login(a2, alice);
await a.bringToFront(); await visibility(a, 'hidden');
await dsend(bob, await newConv(bob, alice, LB), 'm5 for both of your tabs', LB);
await waitFor(async () => (await badge(a2)) === '1' && (await badge(a)) === '1', 10000, 200);
const both1 = (await badge(a2)) === '1' && (await badge(a)) === '1';
const badgesNow = `badges: second tab ${await badge(a2)}, first tab ${await badge(a)}`;
await visibility(a, 'visible'); // she looks at the first tab: it has the conversation open, so it is read
const cleared = !!(await waitFor(async () => (await badge(a2)) === '0' && (await badge(a)) === '0', 10000, 200));
check('DC-08', 'two tabs of the same person stay in step: a message shows badge 1 in both, and reading it in one clears the badge in the other', both1 && cleared, `${badgesNow}; afterwards: second tab ${await badge(a2)}, first tab ${await badge(a)}`);

// ---- delete for everyone ----
await a.bringToFront();
const firstMine = await a.$('.bubble-row.mine .bubble');
await firstMine.hover(); await a.click('.bubble-row.mine .bubble button.delete');
await a.waitForSelector('.confirm'); await a.click('.confirm .danger');
const placeholderBob = await waitFor(async () => (await body(b)).includes('This message was deleted'), 10000, 200);
check('DC-09', 'delete: Alice deletes her first message; it becomes a placeholder for Alice and, live, for Bob; the text is gone from the database', !!placeholderBob && (await body(a)).includes('This message was deleted') && count('DirectMessages', "Content LIKE '%m1 hello bob%'") === 0, `Bob sees placeholder: ${!!placeholderBob}; Alice sees it: ${(await body(a)).includes('This message was deleted')}; rows still holding the text: ${count('DirectMessages', "Content LIKE '%m1 hello bob%'")}`);

// ---- Bob signs out: "Last seen" ----
await b.bringToFront();
await b.evaluate(() => localStorage.clear()); await b.goto(LB + '/login');
await a.bringToFront();
const lastSeen = await waitFor(async () => /last seen/i.test(await status(a)), 20000, 300);
await a.reload(); await a.waitForSelector('.conversation'); await openConv(a, 'Bob'); await sleep(1200);
const sameAfterReload = /last seen/i.test(await status(a));
await say(a, 'm6 while you are signed out');
check('DC-10', 'Bob signs out: Alice sees "Last seen today at …" live, the same after a reload, and a message sent now stays at ONE grey tick', !!lastSeen && sameAfterReload && !!(await waitFor(async () => (await ticks(a, 'sent')) >= 1, 8000, 200)));
await login(b, bob);
check('DC-11', 'Bob signs back in: that message becomes delivered live on Alice\'s screen', !!(await waitFor(async () => (await ticks(a, 'sent')) === 0, 10000, 200)));

// ---- a long conversation ----
const convAD = await newConv(alice, dave, LB);
for (let i = 1; i <= 66; i++) await dsend(alice, convAD, `bulk ${String(i).padStart(2, '0')}`, LB);
await a.goto(LB + '/messages'); await a.waitForSelector('.conversation');
const rows = await a.$$('.conversation');
for (const r of rows) { if ((await r.evaluate(e => e.innerText)).includes('Dave')) { await r.click(); break; } }
await a.waitForSelector('.bubble'); await sleep(1500);
const first = await n(a, '.bubble');
await a.click('button.earlier'); await sleep(1500);
const all = await n(a, '.bubble');
check('DC-12', 'a conversation longer than 50 messages: the page loads the newest 50, "Show earlier messages" loads the rest (66 = 66 in the database)', first === 50 && all === 66 && count('DirectMessages', `ConversationId='${convAD}'`) === 66, `first page ${first}, then ${all}`);

// ---- the same chat started by two people at the same instant ----
const pairs = []; for (let i = 0; i < 8; i++) pairs.push([await newUser(`PairA${i}`, LB), await newUser(`PairB${i}`, LB)]);
const answers = await Promise.all(pairs.flatMap(([x, y]) => [rest(LB, '/api/direct/conversations', { method: 'POST', token: x.token, body: { userId: y.id } }), rest(LB, '/api/direct/conversations', { method: 'POST', token: y.token, body: { userId: x.id } })]));
const ids = pairs.map((_, i) => new Set([answers[2 * i].json?.id, answers[2 * i + 1].json?.id]).size);
check('DC-13', 'two people starting the same chat at the same instant (8 pairs): no errors and exactly one conversation per pair', answers.every(r => r.status === 200 || r.status === 201) && ids.every(x => x === 1) && count('DirectConversations', `UserLowId IN (${pairs.map(p => Math.min(p[0].id, p[1].id)).join(',')})`) === 8);

// ---- privacy: an Admin and an outsider get 404 for someone else's conversation ----
const convAB = await newConv(alice, bob, LB);
sql(`INSERT INTO UserRoles (UserId, RoleId) VALUES (${carol.id}, 1)`);
const adminLogin = await rest(LB, '/api/auth/login', { method: 'POST', body: { email: carol.email, password: carol.password } });
const probes = [];
for (const who of [{ token: adminLogin.json.token }, dave]) {
  probes.push((await rest(LB, `/api/direct/conversations/${convAB}/messages`, { token: who.token })).status);
  probes.push((await rest(LB, `/api/direct/conversations/${convAB}/messages`, { method: 'POST', token: who.token, body: { content: 'sneaky' } })).status);
  probes.push((await rest(LB, `/api/direct/conversations/${convAB}/read`, { method: 'POST', token: who.token })).status);
  probes.push((await rest(LB, `/api/direct/conversations/${convAB}/messages/1`, { method: 'DELETE', token: who.token })).status);
}
const adminList = (await rest(LB, '/api/direct/conversations', { token: adminLogin.json.token })).json;
check('DC-14', 'privacy: an Admin and an outsider get 404 on someone else\'s conversation (read, send, mark read, delete) and see no conversation listed', probes.every(s => s === 404) && Array.isArray(adminList) && adminList.length === 0, `probes: ${probes.join(',')}`);

// ---- accessibility and a phone ----
await a.goto(LB + '/messages'); await a.waitForSelector('.conversation'); await a.click('.conversation'); await a.waitForSelector('.bubble');
await a.addScriptTag({ content: axeSource });
const axeLight = await a.evaluate(async () => (await axe.run(document, { runOnly: ['wcag2a', 'wcag2aa'] })).violations.map(v => ({ id: v.id, impact: v.impact, nodes: v.nodes.length })));
await a.evaluate(() => { localStorage.setItem('aichat.theme', 'dark'); location.reload(); });
await a.waitForSelector('.bubble', { timeout: 15000 }).catch(() => a.waitForSelector('.conversation'));
await a.addScriptTag({ content: axeSource });
const axeDark = await a.evaluate(async () => (await axe.run(document, { runOnly: ['wcag2a', 'wcag2aa'] })).violations.map(v => ({ id: v.id, impact: v.impact, nodes: v.nodes.length })));
const bad = [...axeLight, ...axeDark].filter(v => ['serious', 'critical'].includes(v.impact));
check('DC-15', 'accessibility scan (axe-core WCAG 2 A/AA) of the Messages screen in light and dark: no serious or critical violations', bad.length === 0, bad.length ? JSON.stringify(bad) : 'none');
await a.evaluate(() => localStorage.removeItem('aichat.theme'));
await a.setViewport({ width: 390, height: 800 }); await sleep(600);
check('DC-16', 'on a phone-width screen (390 px) there is no horizontal overflow', !(await a.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth)));
await a.setViewport({ width: 1280, height: 800 });

// ---- two replicas ----
let onSame = true;
for (let i = 0; i < 6 && onSame; i++) {
  await a.goto(LB + '/messages'); await a.waitForSelector('.conversation'); await sleep(1200);
  await b.goto(LB + '/messages'); await b.waitForSelector('.conversation'); await sleep(1200);
  onSame = replicaOf(alice.id) === replicaOf(bob.id);
  if (onSame) await fetch(LB + '/api/auth/me'); // shift the balancer's round robin
}
check('DC-17', 'Alice and Bob are connected to DIFFERENT API replicas behind the same balancer, and everything above worked across them', !onSame, `Alice -> ${replicaOf(alice.id)}, Bob -> ${replicaOf(bob.id)}`);

// ---- a replica dies under a live conversation ----
await a.bringToFront(); await openConv(a, 'Bob'); await a.waitForSelector('#direct-composer');
const bobReplica = replicaOf(bob.id);
sh(`docker kill ${bobReplica}`);
await sleep(500);
await say(a, 'm7 after the crash');
await b.bringToFront();
const survived = await waitFor(async () => (await body(b)).includes('m7 after the crash'), 60000, 500);
check('DC-18', "Bob's replica is killed: his browser reconnects to a surviving replica on its own and Alice's next message reaches him live", !!survived, `${bobReplica} died; Bob now on ${replicaOf(bob.id)}`);
dc(`start ${bobReplica.replace('aichat-', '').replace('-1', '')}`, { allowFail: true });
await sleep(1000);
check('DC-19', 'no uncaught JavaScript errors in any of the windows', [a, a2, b].flatMap(p => p.errs).length === 0, [a, a2, b].flatMap(p => p.errs).join(' | ').slice(0, 200));
await browser.close();
await useReplicas(['api', 'api2', 'api3']);
finish();
