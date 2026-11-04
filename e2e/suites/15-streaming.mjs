import fs from 'node:fs';
import path from 'node:path';
import { check, finish, sleep, waitFor, newUser, sql, count, cleanDb, redisCli, dc, healthy, launch, openPage, WEB, ROOT } from '../lib.mjs';

// ---- Phase 25: streamed replies on the normal stack (real nginx and CSP, real API, real Python service, real LLM provider, real MySQL, real Chrome) ----
// A the stream over HTTP: words arrive one by one (nginx does not hold them back), the answer and usage are saved, Stop keeps the partial, regenerate replaces
// B robustness: a long chat cannot overflow the model any more; the AI service being down is a 503 with the question saved; the one-shot endpoint still works
// C the real UI: words appear while it is written, Stop, Regenerate, reload shows the saved text
const shots = path.join(ROOT, 'e2e', 'shots', 'phase25'); fs.mkdirSync(shots, { recursive: true });
const snap = (p, n) => p.screenshot({ path: path.join(shots, n) });
const PROMPT = 'Write a numbered list of 12 short facts about the ocean, one sentence each.';
const esc = (s) => s.replace(/'/g, "''");

/** Reads a server-sent-event response as it arrives, noting when each event came in. `stopAfterDeltas` closes the connection early (Stop). */
async function stream(pathAndQuery, { token, body, stopAfterDeltas } = {}) {
  const abort = new AbortController();
  const started = Date.now();
  const res = await fetch(WEB + pathAndQuery, { method: 'POST', signal: abort.signal, headers: { 'content-type': 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}) }, body: body === undefined ? undefined : JSON.stringify(body) });
  const out = { status: res.status, type: res.headers.get('content-type') ?? '', events: [], json: null };
  if (!res.body || !res.headers.get('content-type')?.startsWith('text/event-stream')) { try { out.json = await res.json(); } catch { /* no body */ } return out; }
  const reader = res.body.getReader(); const decoder = new TextDecoder(); let buf = ''; let deltas = 0;
  try {
    for (;;) {
      const { done, value } = await reader.read(); if (done) break;
      buf += decoder.decode(value, { stream: true });
      let i; while ((i = buf.indexOf('\n\n')) >= 0) {
        const block = buf.slice(0, i); buf = buf.slice(i + 2);
        const name = /^event: (.*)$/m.exec(block)?.[1]; const data = /^data: (.*)$/m.exec(block)?.[1];
        if (!name || !data) continue;
        out.events.push({ name, data: JSON.parse(data), at: Date.now() - started });
        if (name === 'delta' && ++deltas === stopAfterDeltas) { abort.abort(); return out; }
      }
    }
  } catch (e) { if (!abort.signal.aborted) throw e; }
  return out;
}
const text = (r) => r.events.filter(e => e.name === 'delta').map(e => e.data.text).join('');
const raw = async (p, { method = 'GET', body, token } = {}) => {
  const r = await fetch(WEB + p, { method, headers: { 'content-type': 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}) }, body: body ? JSON.stringify(body) : undefined });
  let json = null; try { json = await r.json(); } catch { /* no body */ } return { status: r.status, json };
};
const newChat = async (u) => (await raw('/api/chat/sessions', { method: 'POST', token: u.token, body: {} })).json.id;
const rowsOf = (id) => sql(`SELECT CONCAT(Id,'|',Role,'|',CHAR_LENGTH(Content)) FROM Messages WHERE SessionId='${id}' ORDER BY Id`).trim().split('\n').filter(Boolean).map(l => { const [i, role, len] = l.split('|'); return { id: Number(i), role, len: Number(len) }; });

cleanDb(); redisCli('flushall');

console.log('\n--- A: the stream over HTTP, through the real nginx ---');
const ann = await newUser('Ann Stream', WEB);
const chatA = await newChat(ann);
const r1 = await stream(`/api/chat/sessions/${chatA}/messages/stream`, { token: ann.token, body: { content: PROMPT } });
const names = r1.events.map(e => e.name);
check('A25-01', 'the stream answers 200 as text/event-stream: first "user", then several "delta", last "done"',
  r1.status === 200 && r1.type.startsWith('text/event-stream') && names[0] === 'user' && names.at(-1) === 'done' && names.filter(n => n === 'delta').length >= 5, `${r1.status} ${r1.type}; ${names.length} events; deltas ${names.filter(n => n === 'delta').length}`);
const firstDelta = r1.events.find(e => e.name === 'delta')?.at ?? 0; const done = r1.events.at(-1)?.at ?? 0;
check('A25-02', 'the words arrive over time, not in one lump at the end (the first word comes well before the last)', done - firstDelta > 800, `first word at ${firstDelta} ms, finished at ${done} ms`);
const saved = rowsOf(chatA);
check('A25-03', 'the question and the full answer are saved, and the saved answer is exactly the streamed text',
  saved.length === 2 && saved[0].role === 'user' && saved[1].role === 'assistant' && saved[1].len === [...text(r1)].length && r1.events.at(-1).data.assistantMessage.content === text(r1), JSON.stringify(saved));
const usage = sql(`SELECT TokensUsed FROM UsageLogs WHERE SessionId='${chatA}'`).trim();
check('A25-04', 'one usage row is written with a real token count', count('UsageLogs', `SessionId='${chatA}'`) === 1 && Number(usage) > 10, `tokens ${usage}`);
check('A25-05', 'the new chat was titled from the question', (await raw('/api/chat/sessions', { token: ann.token })).json[0].title.startsWith('Write a numbered list'));

const chatB = await newChat(ann);
const rb = await stream(`/api/chat/sessions/${chatB}/messages/stream`, { token: ann.token, body: { content: PROMPT }, stopAfterDeltas: 3 });
const stopped = await waitFor(() => rowsOf(chatB).length === 2, 15000, 300);
const sb = rowsOf(chatB);
check('A25-06', 'Stop (closing the connection after 3 words) saves the words written so far as the answer', !!stopped && sb[1]?.role === 'assistant' && sb[1].len > 0 && sb[1].len < 300, JSON.stringify(sb));
check('A25-07', 'a stopped reply still gets a usage row', count('UsageLogs', `SessionId='${chatB}'`) === 1);

const before = rowsOf(chatA);
const rg = await stream(`/api/chat/sessions/${chatA}/regenerate`, { token: ann.token });
const after = rowsOf(chatA);
check('A25-08', 'regenerate streams a new answer (no "user" event) and replaces the old one: still two messages, the answer has a new id',
  rg.status === 200 && rg.events[0].name === 'delta' && rg.events.at(-1).name === 'done' && after.length === 2 && after[1].id > before[1].id, `${JSON.stringify(before)} -> ${JSON.stringify(after)}`);
check('A25-09', 'regenerating wrote a second usage row (the first answer was paid for too)', count('UsageLogs', `SessionId='${chatA}'`) === 2);

const empty = await newChat(ann);
const bob = await newUser('Bob Other', WEB);
const noQ = await raw(`/api/chat/sessions/${empty}/regenerate`, { method: 'POST', token: ann.token });
const foreign = await raw(`/api/chat/sessions/${chatA}/regenerate`, { method: 'POST', token: bob.token });
const foreignStream = await raw(`/api/chat/sessions/${chatA}/messages/stream`, { method: 'POST', token: bob.token, body: { content: 'hi' } });
const anon = await raw(`/api/chat/sessions/${chatA}/regenerate`, { method: 'POST' });
check('A25-10', 'regenerate on an empty chat is 409 nothing_to_regenerate', noQ.status === 409 && noQ.json?.error?.code === 'nothing_to_regenerate', `${noQ.status}`);
check('A25-11', "someone else's chat is 404 for both endpoints, nothing is written, and no token is 401",
  foreign.status === 404 && foreignStream.status === 404 && anon.status === 401 && rowsOf(chatA).length === 2, `${foreign.status} ${foreignStream.status} ${anon.status}`);
const blank = await raw(`/api/chat/sessions/${chatA}/messages/stream`, { method: 'POST', token: ann.token, body: { content: '   ' } });
check('A25-12', 'an empty question is 400 in the standard error shape', blank.status === 400 && !!blank.json?.error?.code, `${blank.status}`);

console.log('\n--- B: robustness ---');
const longChat = await newChat(ann);
sql(`INSERT INTO Messages (SessionId,Role,Content,CreatedAt) WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<40) SELECT '${longChat}', IF(i%2=1,'user','assistant'), CONCAT('Message ', i, ': ', REPEAT('lorem ipsum dolor sit amet ', 280)), TIMESTAMPADD(SECOND,i,'2026-01-01 00:00:00.000000') FROM n`);
const hugeChars = Number(sql(`SELECT SUM(LENGTH(Content)) FROM Messages WHERE SessionId='${longChat}'`));
const rl = await stream(`/api/chat/sessions/${longChat}/messages/stream`, { token: ann.token, body: { content: 'Say only the word: ready' } });
check('B25-01', `a chat holding ${Math.round(hugeChars / 1000)}k characters (about ${Math.round(hugeChars / 4000)}k tokens, far past the model window) still gets an answer, because only the newest messages that fit are sent`,
  rl.status === 200 && rl.events.at(-1)?.name === 'done' && text(rl).length > 0, `${rl.status}; ${rl.events.at(-1)?.name}; "${text(rl).slice(0, 40)}"`);
const oneShot = await raw(`/api/chat/sessions/${longChat}/messages`, { method: 'POST', token: ann.token, body: { content: 'Say only the word: again' } });
check('B25-02', 'the one-shot endpoint (older clients) uses the same budget and still answers 200 with both messages', oneShot.status === 200 && !!oneShot.json?.assistantMessage?.content, `${oneShot.status}`);

dc('stop ai-service', { allowFail: true });
await sleep(1500);
const chatC = await newChat(ann);
const down = await stream(`/api/chat/sessions/${chatC}/messages/stream`, { token: ann.token, body: { content: 'Is anyone there?' } });
check('B25-03', 'with the AI service down, the stream is a plain 503 ai_service_unavailable (not a 500, not a half-open stream)', down.status === 503 && down.json?.error?.code === 'ai_service_unavailable', `${down.status} ${down.json?.error?.code}`);
const dRows = rowsOf(chatC);
check('B25-04', 'the question stays saved and no answer or usage row exists', dRows.length === 1 && dRows[0].role === 'user' && count('UsageLogs', `SessionId='${chatC}'`) === 0, JSON.stringify(dRows));
dc('start ai-service', { allowFail: true });
await waitFor(async () => (await fetch('http://localhost:8080/api/chat/sessions', { headers: { authorization: `Bearer ${ann.token}` } }).then(r => r.status).catch(() => 0)) === 200, 30000, 500);
const aiBack = await waitFor(async () => { const r = await stream(`/api/chat/sessions/${chatC}/regenerate`, { token: ann.token }); return r.status === 200 && r.events.at(-1)?.name === 'done' ? r : null; }, 90000, 3000);
check('B25-05', 'once the AI service is back, "regenerate" answers the question that was left unanswered', !!aiBack && rowsOf(chatC).length === 2, JSON.stringify(rowsOf(chatC)));

console.log('\n--- C: the real page in Chrome ---');
const browser = await launch();
const page = await openPage(browser, { bypassCsp: false }); // the app's own Content-Security-Policy stays in force: streaming must work under it
const csp = []; page.on('console', m => { if (/Content Security Policy|Refused to/i.test(m.text())) csp.push(m.text()); });
const cat = await newUser('Cat Browser', WEB);
await page.goto(WEB + '/login'); await page.type('input[type=email]', cat.email); await page.type('#login-password', cat.password); await page.click('button[type=submit]');
await page.waitForSelector('#composer-input', { timeout: 20000 });
await page.type('#composer-input', PROMPT); await page.keyboard.press('Enter');
await page.waitForSelector('.message.streaming', { timeout: 30000 });
const lens = []; const sawStop = { v: false };
for (let i = 0; i < 40; i++) {
  const s = await page.evaluate(() => ({ len: document.querySelector('.message.streaming .text')?.innerText.length ?? -1, stop: !![...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Stop'), streaming: !!document.querySelector('.message.streaming') }));
  if (!s.streaming) break; if (s.stop) sawStop.v = true; lens.push(s.len); await sleep(250);
}
const grew = lens.filter((l, i) => i > 0 && l > lens[i - 1]).length;
await snap(page, 'streaming.png');
check('C25-01', 'the answer grows on screen while it is written (the text length rose at least 3 times between samples)', grew >= 3, `samples ${lens.join(',')}`);
check('C25-02', 'a Stop button is shown while it is written', sawStop.v);
await page.waitForFunction(() => !document.querySelector('.message.streaming') && !![...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Regenerate'), { timeout: 60000 });
check('C25-03', 'when it finishes, Stop is gone, Send is back, Copy and Regenerate sit under the answer, the page has no errors',
  await page.evaluate(() => !![...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Copy') && !![...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Regenerate') && !![...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Stop') === false && !!document.querySelector('button.send')) && page.errs.length === 0, page.errs.join(' | '));
await snap(page, 'done.png');
const shownText = await page.$eval('.message.assistant .text', e => e.innerText.trim().length);
await page.reload(); await page.waitForSelector('.message.assistant');
const reloadedText = await page.$eval('.message.assistant .text', e => e.innerText.trim().length);
check('C25-04', 'after a reload the same answer is shown (what was streamed is what was saved)', Math.abs(reloadedText - shownText) <= 2, `${shownText} before, ${reloadedText} after`);

const oldId = rowsOf((await raw('/api/chat/sessions', { token: cat.token })).json[0].id).at(-1).id;
await page.evaluate(() => [...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Regenerate').click());
await page.waitForSelector('.message.streaming', { timeout: 30000 });
await page.waitForFunction(() => !document.querySelector('.message.streaming'), { timeout: 60000 });
const newId = rowsOf((await raw('/api/chat/sessions', { token: cat.token })).json[0].id).at(-1).id;
check('C25-05', 'Regenerate streams a new answer in place: still one answer on screen, and the saved answer has a new id', (await page.$$eval('.message.assistant', e => e.length)) === 1 && newId > oldId, `${oldId} -> ${newId}`);

await page.evaluate(() => [...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Regenerate').click());
await page.waitForSelector('.message.streaming', { timeout: 30000 });
await sleep(1200);
const midLen = await page.$eval('.message.streaming .text', e => e.innerText.length);
await page.evaluate(() => [...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Stop').click());
await sleep(500);
const afterStop = await page.evaluate(() => ({ streaming: !!document.querySelector('.message.streaming'), send: !!document.querySelector('button.send'), len: document.querySelector('.message.assistant .text')?.innerText.length ?? 0 }));
check('C25-06', 'clicking Stop ends the writing at once, keeps the words shown, and gives the Send button back', !afterStop.streaming && afterStop.send && afterStop.len > 0 && afterStop.len >= midLen, JSON.stringify(afterStop) + ` mid ${midLen}`);
await snap(page, 'stopped.png');
// The server saves the partial answer (and drops the one it replaces) a moment after the connection closes; a reload before that still shows the old answer.
const chatId = (await raw('/api/chat/sessions', { token: cat.token })).json[0].id;
const swapped = await waitFor(() => rowsOf(chatId).at(-1).id > newId, 15000, 200);
await page.reload(); await page.waitForSelector('.message.assistant');
const persisted = await page.$$eval('.message.assistant', e => e.map(x => x.innerText.trim().length));
check('C25-07', 'the stopped (partial) answer is saved on the server (it replaced the old one) and is what a reload shows', !!swapped && persisted.length === 1 && persisted[0] >= afterStop.len - 2, // never less than was shown; a few words already in flight when the connection closed may be saved too
     `${JSON.stringify(persisted)}; shown at Stop ${afterStop.len}; swapped ${!!swapped}`);
check('C25-08', "the page's Content-Security-Policy blocked nothing while streaming", csp.length === 0, csp.join(' | '));
await browser.close();

console.log('\n--- Z: clean up ---');
cleanDb(); redisCli('flushall');
check('Z25-01', 'test data removed', count('Users') === 0 && count('Messages') === 0);
finish();
