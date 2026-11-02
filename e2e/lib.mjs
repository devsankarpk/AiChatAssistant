// Shared helpers for the end-to-end suites. Every suite talks to the REAL Docker stack: real MySQL, RabbitMQ, Redis, nginx,
// several API replicas, and (for the browser suites) real Chrome. Nothing here is mocked except, in the "stub" configuration,
// the Python AI service, which is replaced by e2e/stub/server.py so latency and failures can be controlled and calls counted.
import { execSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import * as signalR from '@microsoft/signalr';

const here = path.dirname(fileURLToPath(import.meta.url));
export const ROOT = process.env.PROJECT_ROOT ?? path.resolve(here, '..');

// docker.env holds the generated secrets; read them so suites can reach RabbitMQ's management API and the stub.
const envFile = path.join(ROOT, 'docker.env');
export const dockerEnv = fs.existsSync(envFile)
  ? Object.fromEntries(fs.readFileSync(envFile, 'utf8').split('\n').filter(l => /^[A-Z_]+=/.test(l)).map(l => [l.split('=')[0], l.slice(l.indexOf('=') + 1)]))
  : {};

export const STACK = process.env.E2E_STACK ?? 'stub'; // "stub" (AI stub + 3 replicas + load balancer) or "real" (the normal stack, real AI service)
const stubFiles = `-f docker-compose.yml -f e2e/compose.e2e.yml`;
export const COMPOSE = process.env.COMPOSE_CMD ?? `docker compose --env-file docker.env ${STACK === 'stub' ? stubFiles : ''}`;

export const sh = (cmd, opts = {}) => {
  try {
    return execSync(cmd, { cwd: ROOT, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], shell: '/bin/bash', maxBuffer: 64 * 1024 * 1024,
      env: { ...process.env, E2E_BASE_COMPOSE: path.join(ROOT, 'docker-compose.yml'), RABBITMQ_PASSWORD: dockerEnv.RABBITMQ_PASSWORD ?? '', REDIS_PASSWORD: dockerEnv.REDIS_PASSWORD ?? '', INTERNAL_API_KEY: dockerEnv.INTERNAL_API_KEY ?? '', ...(opts.env ?? {}) }, ...opts }).trim();
  } catch (e) {
    if (opts.allowFail) return ((e.stdout ?? '') + (e.stderr ?? '')).toString().trim();
    throw e;
  }
};
export const dc = (args, opts) => sh(`${COMPOSE} ${args}`, opts);
export const sleep = ms => new Promise(r => setTimeout(r, ms));
export const results = [];
export const check = (id, name, ok, detail = '') => {
  results.push({ id, name, ok: !!ok, detail });
  console.log(`${ok ? 'PASS' : 'FAIL'} ${id} ${name}${detail ? '\n        ' + detail : ''}`);
  saveResults(); // written after every check, so a suite that crashes later still leaves what it proved
};
export async function waitFor(fn, ms = 30000, step = 200) { const t = Date.now(); while (Date.now() - t < ms) { const v = await fn(); if (v) return v; await sleep(step); } return null; }

const suiteName = path.basename(process.argv[1] ?? 'suite', '.mjs');
function saveResults() {
  const out = process.env.OUT ?? path.join(here, 'results', `${suiteName}.json`);
  fs.mkdirSync(path.dirname(out), { recursive: true });
  fs.writeFileSync(out, JSON.stringify(results, null, 2));
}
export function finish() {
  const failed = results.filter(r => !r.ok);
  console.log(`\nTOTAL ${results.length - failed.length}/${results.length} passed`);
  saveResults();
  // a marker that the suite ran to its end; without it the report calls the suite crashed, however many checks had passed
  fs.writeFileSync((process.env.OUT ?? path.join(here, 'results', `${suiteName}.json`)).replace(/\.json$/, '.done'), 'done');
  process.exit(failed.length ? 1 : 0);
}

// ---- where things are ----
export const PORTS = [18081, 18082, 18083];          // the three API replicas, published directly (stub stack only)
export const R1 = PORTS[0], R2 = PORTS[1], R3 = PORTS[2];
export const WEB = 'http://localhost:8080';          // nginx + Angular, one API behind it (the real stack)
export const LB = 'http://localhost:18090';          // nginx in front of ALL API replicas (stub stack only)
export const FRONT = STACK === 'stub' ? LB : WEB;    // what a browser should open

export async function rest(portOrBase, path_, { method = 'GET', body, token } = {}) {
  const base = typeof portOrBase === 'number' ? `http://localhost:${portOrBase}` : portOrBase;
  const t = Date.now();
  const r = await fetch(base + path_, { method, headers: { 'content-type': 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}) }, body: body ? JSON.stringify(body) : undefined });
  let json = null; try { json = await r.json(); } catch { /* no body */ }
  return { status: r.status, json, ms: Date.now() - t };
}
let seq = 0;
export async function newUser(name, port = R1) {
  const email = `${name.toLowerCase().replace(/\s+/g, '')}-${Date.now()}-${seq++}@example.com`;
  const r = await rest(port, '/api/auth/register', { method: 'POST', body: { name, email, password: 'Passw0rd!x' } });
  if (r.status !== 201) throw new Error('register ' + r.status + JSON.stringify(r.json));
  return { name, email, password: 'Passw0rd!x', token: r.json.token, id: Number(JSON.parse(Buffer.from(r.json.token.split('.')[1], 'base64url')).sub) };
}
export async function newSession(user, port = R1) { return (await rest(port, '/api/chat/sessions', { method: 'POST', token: user.token, body: {} })).json.id; }
export const ask = (user, sessionId, content, port = R1) => rest(port, `/api/chat/sessions/${sessionId}/messages`, { method: 'POST', token: user.token, body: { content } });
export const messages = (user, sessionId, port = R1) => rest(port, `/api/chat/sessions/${sessionId}/messages`, { token: user.token }).then(r => r.json ?? []);
export const jobs = (user, sessionId, port = R1) => rest(port, `/api/chat/sessions/${sessionId}/jobs`, { token: user.token }).then(r => r.json);
export const newConv = async (a, b, port = R1) => (await rest(port, '/api/direct/conversations', { method: 'POST', token: a.token, body: { userId: b.id } })).json.id;
export const dsend = (user, conv, content, port = R1) => rest(port, `/api/direct/conversations/${conv}/messages`, { method: 'POST', token: user.token, body: { content } });

/** A real SignalR client over a real WebSocket to one replica (or the load balancer), recording every event it receives. */
export async function hub(user, portOrBase, label = '') {
  const base = typeof portOrBase === 'number' ? `http://localhost:${portOrBase}` : portOrBase;
  const events = [];
  const conn = new signalR.HubConnectionBuilder()
    .withUrl(`${base}/hubs/direct`, { accessTokenFactory: () => user.token, skipNegotiation: true, transport: signalR.HttpTransportType.WebSockets })
    .configureLogging(signalR.LogLevel.None).build();
  for (const name of ['MessageReceived', 'MessageStatusChanged', 'MessagesRead', 'MessageDeleted', 'PeerTyping', 'PeerPresenceChanged', 'AssistantReplied', 'AssistantFailed'])
    conn.on(name, p => events.push({ t: Date.now(), name, p }));
  await conn.start();
  return { user, label, conn, events,
    of: (name, pred = () => true) => events.filter(e => e.name === name && pred(e.p)),
    wait: (name, pred = () => true, ms = 15000) => waitFor(() => events.find(e => e.name === name && pred(e.p)), ms, 50),
    close: () => conn.stop() };
}

// ---- test doubles and the broker ----
export const stub = {
  control: (body) => fetch('http://localhost:18000/__control', { method: 'POST', body: JSON.stringify(body) }).then(r => r.json()),
  stats: () => fetch('http://localhost:18000/__stats').then(r => r.json()),
};
const mgmtAuth = () => 'Basic ' + Buffer.from('aichat:' + dockerEnv.RABBITMQ_PASSWORD).toString('base64');
export const mgmt = async (p, { method = 'GET', body } = {}) => {
  // RabbitMQ's management API drops connections for a few seconds after the broker (re)starts: try again before giving up.
  let lastError;
  for (let attempt = 0; attempt < 8; attempt++) {
    try {
      const r = await fetch('http://localhost:15673/api' + p, { method, headers: { authorization: mgmtAuth(), 'content-type': 'application/json' }, body: body ? JSON.stringify(body) : undefined });
      let json = null; try { json = await r.json(); } catch { /* no body */ }
      return { status: r.status, json };
    } catch (e) { lastError = e; await sleep(1500); }
  }
  throw lastError;
};
export const mgmtReady = () => waitFor(async () => { try { return (await mgmt('/overview')).status === 200; } catch { return false; } }, 120000, 1000);
export const publishRaw = (exchange, routingKey, payload, properties = {}) =>
  mgmt(`/exchanges/%2F/${exchange}/publish`, { method: 'POST', body: { properties: { delivery_mode: 2, ...properties }, routing_key: routingKey, payload, payload_encoding: 'string' } });
export const peek = (queue, count = 10) => mgmt(`/queues/%2F/${queue}/get`, { method: 'POST', body: { count, ackmode: 'ack_requeue_true', encoding: 'auto' } }).then(r => r.json ?? []);
export const queues = () => Object.fromEntries(JSON.parse(dc(`exec -T rabbitmq rabbitmqctl -q list_queues name messages consumers --formatter json`)).map(x => [x.name, { m: x.messages, c: x.consumers }]));
export const depth = (n) => queues()[n]?.m ?? -1;
export const purgeAll = () => { for (const n of Object.keys(queues())) if (/^(email|ai|direct)\./.test(n)) dc(`exec -T rabbitmq rabbitmqctl -q purge_queue ${n}`, { allowFail: true }); };
export const redisCli = (args) => dc(`exec -T redis redis-cli ${args} 2>&1`, { allowFail: true });
export const redisUp = () => waitFor(async () => redisCli('ping') === 'PONG', 60000, 500);

// ---- the database ----
export const sql = (s) => sh(`${COMPOSE} exec -T mysql sh -c 'mysql -uaichat -p"$MYSQL_PASSWORD" AiChatAssistant -N 2>/dev/null'`, { allowFail: true, input: s, stdio: ['pipe', 'pipe', 'pipe'] });
export const count = (table, where = '1=1') => Number(sql(`SELECT COUNT(*) FROM ${table} WHERE ${where}`));
export const flushDedupe = () => dc(`exec -T redis redis-cli EVAL "for _,k in ipairs(redis.call('keys','dedupe:*')) do redis.call('del',k) end return 1" 0`, { allowFail: true });
const cleanDb0 = () => sql('DELETE FROM OutboxMessages; DELETE FROM AiReplyJobs; DELETE FROM DirectMessages; DELETE FROM DirectConversations; DELETE FROM UsageLogs; DELETE FROM Messages; DELETE FROM ChatSessions; DELETE FROM PasswordResetTokens; DELETE FROM UserRoles; DELETE FROM Users; ALTER TABLE Users AUTO_INCREMENT=1; ALTER TABLE OutboxMessages AUTO_INCREMENT=1;');
/** Empties every table the tests write to (roles stay) and the de-duplication keys that would otherwise outlive the reset. */
export const cleanDb = () => { flushDedupe(); return cleanDb0(); };

// ---- health and logs ----
export const healthy = async (port) => (await fetch(`http://localhost:${port}/health`).then(r => r.status).catch(() => 0)) === 200;
export const logs = (service, since) => dc(`logs --no-log-prefix ${since ? `--since ${since}` : ''} ${service}`, { allowFail: true });
export const apiLogs = (since) => logs('api api2 api3', since);
export const allHealthy = async (ports = PORTS) => (await Promise.all(ports.map(healthy))).every(Boolean);
export const waitHealthy = (ports = PORTS, ms = 120000) => waitFor(() => allHealthy(ports), ms, 1000);
/** `docker compose up` gives up (and leaves the container "Created") if a dependency is briefly unhealthy, e.g. a broker that has just been unpaused; ask again until the services answer. */
export async function bringUp(services, port, { env = '', ms = 120000 } = {}) {
  const until = Date.now() + ms;
  while (Date.now() < until) {
    sh(`${env} ${COMPOSE} up -d --force-recreate ${services} 2>&1 | tail -1`, { allowFail: true });
    if (await waitFor(() => healthy(port), 40000, 1000)) return true;
  }
  return false;
}

// ---- browsers ----
export const CHROME = process.env.CHROME_BIN ?? '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
export async function launch() {
  const { default: puppeteer } = await import('puppeteer-core');
  return puppeteer.launch({ executablePath: CHROME, headless: 'new', args: ['--no-sandbox'] });
}
/**
 * `bypassCsp` is on by default: the app now sends a Content-Security-Policy that forbids inline scripts, and several suites inject the
 * axe-core accessibility library into the page as one. The policy itself is checked by 11-security and the demo, which pass `bypassCsp: false`.
 */
export async function openPage(browser, { width = 1280, height = 800, bypassCsp = true } = {}) {
  const ctx = await browser.createBrowserContext();
  const page = await ctx.newPage();
  if (bypassCsp) await page.setBypassCSP(true);
  page.errs = [];
  page.on('pageerror', e => page.errs.push(String(e)));
  await page.setViewport({ width, height });
  return page;
}

// ---- the load balancer and replica bookkeeping ----
/** nginx resolves the replicas when it starts, so restart it after replicas were recreated, then wait until it answers. */
export async function refreshLb() {
  // nginx will not start if an upstream name cannot be resolved, and a stopped container has no name on the network:
  // so every replica must exist (be running) when the balancer starts.
  dc('start api api2 api3', { allowFail: true });
  await waitHealthy(PORTS, 120000);
  dc('restart lb', { allowFail: true });
  const ok = await waitFor(async () => (await fetch(`${LB}/`).then(r => r.status).catch(() => 0)) === 200, 60000, 500);
  if (!ok) throw new Error('the load balancer did not come back (is a replica missing?)');
}
export const REPLICA_CONTAINERS = ['aichat-api-1', 'aichat-api2-1', 'aichat-api3-1'];
/** Which replica logged the newest SignalR connection of this user (from "Hub connected: user N" lines). */
export function replicaOf(userId) {
  let best = null;
  for (const c of REPLICA_CONTAINERS) {
    const lines = sh(`docker logs --timestamps ${c} 2>&1 | grep "Hub connected: user ${userId},"`, { allowFail: true }).split('\n').filter(Boolean);
    const t = lines.length ? lines[lines.length - 1].split(' ')[0] : '';
    if (t && (!best || t > best.t)) best = { c, t };
  }
  return best?.c;
}
/** Starts exactly these replicas (stopping the others) and waits until they are healthy. */
export async function useReplicas(services) {
  const all = { api: R1, api2: R2, api3: R3 };
  const stopped = Object.keys(all).filter(s => !services.includes(s));
  if (stopped.length) dc(`stop ${stopped.join(' ')}`, { allowFail: true });
  dc(`up -d ${services.join(' ')}`, { allowFail: true });
  await waitHealthy(services.map(s => all[s]));
}
