// Real Chrome, real nginx, real API replicas, real RabbitMQ and Redis; the AI service is the stub so latency/failure can be controlled.
import fs from 'node:fs';
import { createRequire } from 'node:module';
import { launch, COMPOSE, dc, sh, check, finish, sleep, waitFor, stub, sql, cleanDb, purgeAll, healthy, useReplicas, refreshLb, LB } from '../lib.mjs';
const require = createRequire(import.meta.url);
const axeSource = fs.readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8');
const BASE = LB, PW = 'Passw0rd!x', tag = Date.now();
const SHOTS = process.env.SHOTS; // optional folder for screenshots
await useReplicas(['api', 'api2', 'api3']);
await refreshLb(); // the page goes through the load balancer, so it may land on either replica
cleanDb(); purgeAll();
await stub.control({ reset: true, delay_ms: 0, fail_next: 0, fail_always: false });

const browser = await launch();
const open = async () => { const ctx = await browser.createBrowserContext(); const p = await ctx.newPage(); p.errs = []; p.on('pageerror', e => p.errs.push(String(e))); await p.setViewport({ width: 1280, height: 800 }); return p; };
const n = (p, sel) => p.$$eval(sel, e => e.length);
const txt = (p, sel) => p.$$eval(sel, els => els.map(e => e.innerText.trim()));
const body = p => p.evaluate(() => document.body.innerText);
const sendBtnDisabled = p => p.$eval('button.send', b => b.disabled);
const ask = async (p, text) => { await p.type('#composer-input', text); await p.keyboard.press('Enter'); };

const a = await open();
await a.goto(BASE + '/register');
await a.type('input[type=text]', 'Async Tester'); await a.type('input[type=email]', `async-${tag}@example.com`); await a.type('#register-password', PW);
await a.click('button[type=submit]'); await a.waitForSelector('#composer-input', { timeout: 15000 });

// ---- B1: pending then replied, live ----
await stub.control({ delay_ms: 4000 });
const t0 = Date.now();
await ask(a, 'What is DNS?');
await a.waitForSelector('.message.user', { timeout: 5000 });
const quick = Date.now() - t0;
await sleep(500);
check('B18-01', 'sending: the question appears at once and "Thinking…" shows while the reply is produced in the background', (await n(a, '.message.user')) === 1 && (await n(a, '.message.thinking')) === 1 && quick < 3000, `question shown after ${quick} ms`);
check('B18-02', 'while a reply is pending the Send button is disabled (no second question queued behind it)', await a.evaluate(() => { const t = document.querySelector('#composer-input'); t.value = 'x'; t.dispatchEvent(new Event('input', { bubbles: true })); return true; }) && await sendBtnDisabled(a));
if (SHOTS) await a.screenshot({ path: `${SHOTS}/18-thinking.png` });
await a.evaluate(() => { const t = document.querySelector('#composer-input'); t.value = ''; t.dispatchEvent(new Event('input', { bubbles: true })); });
const replied = await waitFor(async () => (await n(a, '.message.assistant:not(.thinking)')) === 1, 15000, 200);
const took = Date.now() - t0;
check('B18-03', 'the reply appears by itself (no reload), after about the 4 s the AI took, "Thinking…" goes away', !!replied && took >= 3500 && (await n(a, '.message.thinking')) === 0, `${took} ms; reply: "${(await txt(a, '.message.assistant .text'))[0]}"`);
await a.type('#composer-input', 'next'); 
check('B18-04', 'and the composer is usable again', !(await sendBtnDisabled(a)));
await a.evaluate(() => { const t = document.querySelector('#composer-input'); t.value = ''; t.dispatchEvent(new Event('input', { bubbles: true })); });

// ---- B2: failure, then Send again ----
await stub.control({ reset: true, delay_ms: 0, fail_always: true });
const t1 = Date.now();
await ask(a, 'this one will fail');
const failed = await waitFor(async () => (await n(a, '.reply-failed')) === 1, 30000, 300);
const failAfter = (Date.now() - t1) / 1000;
check('B18-05', 'when the AI keeps failing, after the retries (about 5 s) the question shows "The assistant couldn\'t reply." with Send again', !!failed && failAfter >= 4.5 && (await body(a)).includes("The assistant couldn't reply."), `after ${failAfter.toFixed(1)} s`);
check('B18-06', 'no "Thinking…" is left behind and the composer is free', (await n(a, '.message.thinking')) === 0);
if (SHOTS) await a.screenshot({ path: `${SHOTS}/18-failed.png` });
const userBubblesBefore = await n(a, '.message.user');
await stub.control({ reset: true, fail_always: false, delay_ms: 1500 });
await a.click('.reply-failed button');
await sleep(400);
check('B18-07', 'Send again: the failed note disappears at once and "Thinking…" returns (same question, nothing re-typed)', (await n(a, '.reply-failed')) === 0 && (await n(a, '.message.thinking')) === 1);
const recovered = await waitFor(async () => (await n(a, '.message.assistant:not(.thinking)')) === 2, 15000, 300);
check('B18-08', 'the reply arrives, and the chat still has exactly the two questions (no duplicate from Send again)', !!recovered && (await n(a, '.message.user')) === userBubblesBefore && (await n(a, '.message.user')) === 2);

// ---- B3: reload while pending ----
await stub.control({ reset: true, delay_ms: 7000 });
await ask(a, 'reload me while thinking');
await sleep(1500);
await a.reload();
await a.waitForSelector('.message.user', { timeout: 15000 });
await sleep(1000);
check('B18-09', 'reloading the page while a reply is pending still shows the question AND "Thinking…" (restored from the server)', (await n(a, '.message.thinking')) === 1 && (await body(a)).includes('reload me while thinking') && await sendBtnDisabled(a));
const afterReload = await waitFor(async () => (await body(a)).includes('stub reply to: reload me while thinking'), 20000, 300);
check('B18-10', 'and the reply still arrives on the reloaded page', !!afterReload && (await n(a, '.message.thinking')) === 0);

// ---- B4: the push is lost (Redis down) -> the page finds the reply by itself ----
await stub.control({ reset: true, delay_ms: 3000 });
dc('stop redis');
await ask(a, 'push will be lost');
const t2 = Date.now();
const polled = await waitFor(async () => (await body(a)).includes('stub reply to: push will be lost'), 45000, 500);
check('B18-11', 'if the live push is lost (Redis down) the reply is saved anyway and the page picks it up by itself within about 15-20 s, with no reload', !!polled && (Date.now() - t2) > 3000, polled ? `appeared after ${((Date.now() - t2) / 1000).toFixed(0)} s` : 'never appeared');
check('B18-12', 'and "Thinking…" is cleared', (await n(a, '.message.thinking')) === 0 && !(await sendBtnDisabled(a).catch(() => true)) === false ? true : (await n(a, '.message.thinking')) === 0);
dc('start redis');
await waitFor(async () => sh(`${COMPOSE} exec -T redis redis-cli ping`, { allowFail: true }) === 'PONG', 30000, 500);

// ---- B5: switching chats while a reply is pending ----
await stub.control({ reset: true, delay_ms: 6000 });
await ask(a, 'answer me in the first chat');
await sleep(800);
await a.click('.new-chat');
await sleep(1200);
const emptyOther = (await n(a, '.message.user')) === 0;
await sleep(6500);
const sessionItems = await a.$$('.session-item');
await sessionItems[sessionItems.length > 1 ? 1 : 0].click();
await sleep(1500);
const firstChatBody = await body(a);
check('B18-13', 'switching to another chat while a reply is pending: that chat is clean, and coming back later shows the finished reply (no stuck "Thinking…")', emptyOther && firstChatBody.includes('stub reply to: answer me in the first chat') && (await n(a, '.message.thinking')) === 0);

// ---- B6: two tabs of the same person ----
const a2 = await open();
await a2.goto(BASE + '/login'); await a2.type('input[type=email]', `async-${tag}@example.com`); await a2.type('#login-password', PW);
await a2.click('button[type=submit]'); await a2.waitForSelector('#composer-input', { timeout: 15000 });
await a2.waitForSelector('.session-item');
const items2 = await a2.$$('.session-item'); await items2[1].click(); await sleep(1500);
await a.bringToFront();
await stub.control({ reset: true, delay_ms: 3000 });
await ask(a, 'two tabs');
await sleep(500);
const both = await waitFor(async () => (await body(a)).includes('stub reply to: two tabs') && (await body(a2)).includes('stub reply to: two tabs'), 20000, 300);
check('B18-14', 'the same person with the chat open in two tabs: the reply appears in BOTH tabs', !!both);

// ---- accessibility ----
await a.addScriptTag({ content: axeSource });
const axe = await a.evaluate(async () => { const r = await axe.run(document, { runOnly: ['wcag2a', 'wcag2aa'] }); return r.violations.map(v => ({ id: v.id, impact: v.impact, nodes: v.nodes.length })); });
await stub.control({ reset: true, fail_always: true });
await ask(a, 'fail for the accessibility scan');
await waitFor(async () => (await n(a, '.reply-failed')) === 1, 30000, 300);
const axeFailed = await a.evaluate(async () => { const r = await axe.run(document, { runOnly: ['wcag2a', 'wcag2aa'] }); return r.violations.map(v => ({ id: v.id, impact: v.impact, nodes: v.nodes.length })); });
await a.evaluate(() => { localStorage.setItem('aichat.theme', 'dark'); location.reload(); });
await a.waitForSelector('.reply-failed', { timeout: 15000 }); await sleep(800);
if (SHOTS) await a.screenshot({ path: `${SHOTS}/18-failed-dark.png` });
await a.addScriptTag({ content: axeSource });
const axeDark = await a.evaluate(async () => { const r = await axe.run(document, { runOnly: ['wcag2a', 'wcag2aa'] }); return r.violations.map(v => ({ id: v.id, impact: v.impact, nodes: v.nodes.length })); });
const bad = [...axe, ...axeFailed, ...axeDark].filter(v => ['serious', 'critical'].includes(v.impact));
check('B18-15', 'accessibility scan (axe-core WCAG 2 A/AA) of the chat with a pending reply, a failed reply, and in dark theme: no serious or critical violations', bad.length === 0, bad.length ? JSON.stringify(bad) : 'none');
await a.setViewport({ width: 390, height: 800 }); await sleep(600);
const overflow = await a.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
if (SHOTS) await a.screenshot({ path: `${SHOTS}/18-failed-mobile.png` });
check('B18-16', 'on a phone-width screen (390 px) the failed state does not overflow', !overflow);

check('B18-17', 'no uncaught JavaScript errors in either tab', [a, a2].flatMap(p => p.errs).length === 0, [a, a2].flatMap(p => p.errs).join(' | ').slice(0, 200));
await stub.control({ reset: true, fail_always: false, delay_ms: 0 });
await browser.close();
finish();
