import * as signalR from '@microsoft/signalr';
import { COMPOSE, dc, sh, check, sleep, waitFor, rest, newUser, newConv, dsend, hub, redisCli, redisUp, cleanDb, healthy, useReplicas, waitHealthy, refreshLb, LB, R1, R2, finish } from '../lib.mjs';

// ---- Phase 16: SignalR through the Redis backplane and presence shared across API replicas (two replicas, real Redis) ----
const list = (port, who) => rest(port, '/api/direct/conversations', { token: who.token });
const peerOnline = async (port, who, peer) => (await list(port, who)).json?.find(c => c.peer.id === peer.id)?.peerOnline;
const presenceKeys = () => redisCli(`--scan --pattern 'presence:*'`).split('\n').filter(Boolean);
const closeAll = async (...hs) => { for (const h of hs) await h.close().catch(() => { }); };
const mine = (convId, port, user) => rest(port, `/api/direct/conversations/${convId}/messages`, { token: user.token });

await useReplicas(['api', 'api2', 'api3']); // api3 is idle here, but must exist for the balancer test at the end
cleanDb();
redisCli('flushall');
check('P-01', 'two API replicas are up and healthy (ports 18081 and 18082), sharing one MySQL, Redis and RabbitMQ', await waitHealthy([R1, R2], 60000));

const alice = await newUser('Alice'), bob = await newUser('Bob'), carol = await newUser('Carol');
const convAB = await newConv(alice, bob), convAC = await newConv(alice, carol);

console.log('\n--- A: pushes cross replicas (Alice on replica 1, Bob on replica 2) ---');
const hA = await hub(alice, R1, 'alice@r1'), hB = await hub(bob, R2, 'bob@r2');
await sleep(500);
const keys = presenceKeys();
check('P-02', 'both people are recorded in Redis presence (one key per user)', keys.includes(`presence:user:${alice.id}`) && keys.includes(`presence:user:${bob.id}`), keys.join(', '));
check('P-03', 'each presence key has an expiry, so an abandoned key cleans itself up', Number(redisCli(`pttl presence:user:${bob.id}`)) > 0, `PTTL ${redisCli(`pttl presence:user:${bob.id}`)} ms`);
check('P-04', 'Alice (replica 1) sees Bob (connected to replica 2) as online in the conversation list', (await peerOnline(R1, alice, bob)) === true);
check('P-05', 'Bob (replica 2) sees Alice (replica 1) as online', (await peerOnline(R2, bob, alice)) === true);
const sent = await dsend(alice, convAB, 'hello bob (sent through replica 1)', R1);
check('P-06', 'a message sent through replica 1 is "delivered" at once because Bob is online on replica 2 (shared presence)', sent.status === 201 && sent.json?.status === 'delivered', `status=${sent.json?.status}`);
const got = await hB.wait('MessageReceived', p => p.content?.includes('hello bob'));
check('P-07', 'Bob, connected to replica 2, receives the live MessageReceived push from replica 1 (Redis backplane)', !!got, got ? `payload: "${got.p.content}"` : 'not received');
const msgId = sent.json?.id;
const read = await rest(R2, `/api/direct/conversations/${convAB}/read`, { method: 'POST', token: bob.token });
check('P-08', "Bob marks it read on replica 2; Alice's read tick arrives live on replica 1", read.status === 200 && !!(await hA.wait('MessageStatusChanged', p => p.status === 'read' && p.messageIds?.includes(msgId))));
await hA.conn.invoke('Typing', convAB);
check('P-09', "Alice's typing signal (replica 1) reaches Bob (replica 2)", !!(await hB.wait('PeerTyping', p => p.userId === alice.id)));
const del = await rest(R1, `/api/direct/conversations/${convAB}/messages/${msgId}`, { method: 'DELETE', token: alice.token });
check('P-10', 'delete through replica 1 is pushed to both people, on both replicas', del.status === 200 && !!(await hA.wait('MessageDeleted', p => p.messageId === msgId)) && !!(await hB.wait('MessageDeleted', p => p.messageId === msgId)), `delete status ${del.status}`);

console.log('\n--- B: presence across replicas ---');
const c1 = await hub(carol, R1, 'carol-tab1@r1');
const online1 = await hA.wait('PeerPresenceChanged', p => p.userId === carol.id && p.online === true);
check('P-11', "Carol's first tab (replica 1) makes her come online: Alice is told exactly once", !!online1 && hA.of('PeerPresenceChanged', p => p.userId === carol.id && p.online).length === 1);
const c2 = await hub(carol, R2, 'carol-tab2@r2');
await sleep(800);
check('P-12', "Carol's second tab on the OTHER replica does not announce her online again", hA.of('PeerPresenceChanged', p => p.userId === carol.id && p.online).length === 1);
await c1.close(); await sleep(1000);
check('P-13', 'closing tab 1 (replica 1) does not take her offline: no event, still online per the other replica', hA.of('PeerPresenceChanged', p => p.userId === carol.id && !p.online).length === 0 && (await peerOnline(R2, bob, alice)) === true && (await peerOnline(R1, alice, carol)) === true);
await c2.close();
const off = await hA.wait('PeerPresenceChanged', p => p.userId === carol.id && p.online === false);
await sleep(600);
check('P-14', 'closing the last tab (replica 2) takes her offline: exactly one event, with a last-seen time', !!off && !!off.p.lastSeenAt && hA.of('PeerPresenceChanged', p => p.userId === carol.id && !p.online).length === 1);
const lastSeen = (await list(R1, alice)).json?.find(c => c.peer.id === carol.id);
check('P-15', 'afterwards the list shows Carol offline with the saved last-seen time', lastSeen?.peerOnline === false && !!lastSeen?.peerLastSeenAt, lastSeen?.peerLastSeenAt);

console.log('\n--- C: 20 people connect (and disconnect) on both replicas at the same instant ---');
const crowd = [];
for (let i = 0; i < 20; i++) { const u = await newUser(`Crowd${i}`); await newConv(alice, u); crowd.push(u); }
const tabs = await Promise.all(crowd.flatMap(u => [hub(u, R1), hub(u, R2)]));
await sleep(2500);
const onlineCounts = crowd.map(u => hA.of('PeerPresenceChanged', p => p.userId === u.id && p.online).length);
check('P-16', 'exactly ONE "came online" announcement per person, even with two simultaneous first connections on different replicas (atomic Lua)', onlineCounts.every(n => n === 1), `counts: ${[...new Set(onlineCounts)].join('/')} over 20 people`);
await Promise.all(tabs.map(t => t.close()));
await sleep(3000);
const offCounts = crowd.map(u => hA.of('PeerPresenceChanged', p => p.userId === u.id && !p.online).length);
check('P-17', 'exactly ONE "went offline" announcement per person after both tabs close at the same instant', offCounts.every(n => n === 1), `counts: ${[...new Set(offCounts)].join('/')} over 20 people`);

console.log('\n--- D: heartbeat keeps live connections online past the 6 s lifetime ---');
await sleep(12000);
check('P-18', 'Bob has been idle for 12 s (twice the 6 s mark lifetime) and is still online: the heartbeat refreshes his mark', (await peerOnline(R1, alice, bob)) === true);

console.log('\n--- E: a replica dies without warning (SIGKILL) ---');
dc('kill api2');
check('P-19', "Bob's replica is killed: within the mark lifetime (plus one beat) Alice's replica reports him offline, with no manual cleanup", !!(await waitFor(async () => (await peerOnline(R1, alice, bob)) === false, 25000, 500)));
const whileDown = await dsend(alice, convAB, 'bob, are you there?', R1);
check('P-20', 'a message to him now stays at "sent" (one grey tick), not falsely "delivered"', whileDown.status === 201 && whileDown.json?.status === 'sent', `status=${whileDown.json?.status}`);
const killedAt = Date.now();
const swept = await hA.wait('PeerPresenceChanged', p => p.userId === bob.id && !p.online, 30000);
check('P-21', "the crashed replica sent no goodbye, yet the surviving replicas' sweeper announces Bob offline to Alice (live push, no manual cleanup)", !!swept, swept ? `${((Date.now() - killedAt) / 1000).toFixed(1)} s after the kill` : 'no event within 30 s');
await sleep(6000); // two more sweep rounds, with both surviving replicas sweeping
check('P-21a', 'exactly ONE offline announcement for Bob although two surviving replicas both sweep (the Redis script hands him to one of them)', hA.of('PeerPresenceChanged', p => p.userId === bob.id && !p.online).length === 1, `${hA.of('PeerPresenceChanged', p => p.userId === bob.id && !p.online).length} event(s)`);
const bobRow = (await list(R1, alice)).json?.find(c => c.peer.id === bob.id);
check('P-21b', "Bob's last-seen time was saved, and is about when he was last alive (not when the sweep ran)", !!bobRow?.peerLastSeenAt && Math.abs(new Date(bobRow.peerLastSeenAt).getTime() - killedAt) < 20000 && bobRow.peerOnline === false, bobRow?.peerLastSeenAt);
check('P-21c', "Bob's presence key and index entry are gone from Redis (nothing left to sweep again)", redisCli(`EXISTS presence:user:${bob.id}`).trim() === '0' && redisCli(`ZSCORE presence:index ${bob.id}`).trim() === '', `index score: "${redisCli(`ZSCORE presence:index ${bob.id}`).trim()}"`);
check('P-21d', 'a person whose replica did NOT die (Alice, on replica 1) was not announced offline by the sweeper: Bob (via the surviving replica) still sees her online', (await peerOnline(R1, bob, alice)) === true);
const hB1 = await hub(bob, R1, 'bob@r1');
check('P-22', 'Bob reconnects to the surviving replica: his waiting message becomes "delivered" and Alice is told live', !!(await hA.wait('MessageStatusChanged', p => p.status === 'delivered' && p.messageIds?.includes(whileDown.json?.id))));
dc('start api2');
check('P-23', 'the killed replica starts again and is healthy', !!(await waitFor(() => healthy(R2), 60000, 500)));
await closeAll(hB1); await sleep(500);

console.log('\n--- F: Redis goes down and comes back ---');
const hB2 = await hub(bob, R2, 'bob@r2');
await sleep(500);
dc('stop redis'); await sleep(1500);
const outage = await dsend(alice, convAB, 'sent while Redis is down', R1);
check('P-24', 'Redis down: sending a message still works (201, saved) and is not a 500', outage.status === 201, `status ${outage.status} in ${outage.ms} ms, delivery status=${outage.json?.status}`);
check('P-25', 'Redis down: the request is slowed by seconds at most, not left hanging (2 s Redis timeouts)', outage.ms < 8000, `${outage.ms} ms`);
const listDown = await list(R1, alice);
check('P-26', 'Redis down: the conversation list still loads (presence falls back to this server)', listDown.status === 200, `status ${listDown.status} in ${listDown.ms} ms`);
check('P-27', 'Redis down: /health stays 200 on both replicas (liveness only, no dependency check)', (await healthy(R1)) && (await healthy(R2)));
const saved = await mine(convAB, R2, bob);
check('P-28', 'Redis down: the message was saved and Bob can read it (REST is unaffected)', JSON.stringify(saved.json).includes('sent while Redis is down'));
dc('start redis'); await redisUp(); await sleep(6000);
const afterBack = await dsend(alice, convAB, 'after redis came back', R1);
check('P-29', 'Redis back: live pushes cross replicas again WITHOUT reconnecting Bob or restarting anything', afterBack.status === 201 && !!(await hB2.wait('MessageReceived', p => p.content === 'after redis came back', 15000)), `delivery status=${afterBack.json?.status}`);

console.log('\n--- F2: a long Redis outage must not make the sweeper declare live people gone ---');
const offlineBefore = hA.of('PeerPresenceChanged', p => p.userId === bob.id && !p.online).length;
dc('pause redis'); await sleep(16000); // longer than the 6 s mark lifetime + 4 s grace + 2 s sweep: every mark has lapsed
dc('unpause redis'); await redisUp();
await sleep(9000); // several sweep rounds and heartbeats after Redis returns
check('P-29a', "after a 16 s Redis outage (every mark lapsed) nobody who is still connected was announced offline by the sweeper", hA.of('PeerPresenceChanged', p => p.userId === bob.id && !p.online).length === offlineBefore, `offline events for Bob before/after: ${offlineBefore}/${hA.of('PeerPresenceChanged', p => p.userId === bob.id && !p.online).length}`);
check('P-29b', 'and Bob still shows online afterwards (his replica re-created his mark)', (await peerOnline(R1, alice, bob)) === true);

console.log('\n--- G: Redis loses its data ---');
const marksBefore = presenceKeys().length;
redisCli('flushall');
check('P-30', 'Redis loses all its data (FLUSHALL): it held the connections\' marks before, so this really wipes them', marksBefore >= 2, `${marksBefore} presence marks before the flush`);
check('P-31', "within a heartbeat the replicas re-create their connections' marks and Bob shows online again, with nobody reconnecting", !!(await waitFor(async () => (await peerOnline(R1, alice, bob)) === true, 15000, 500)));

console.log('\n--- H: everyone leaves ---');
await closeAll(hA, hB2);
await sleep(1500);
check('P-32', 'when everyone has disconnected no presence keys are left in Redis', presenceKeys().length === 0, presenceKeys().join(', '));

// =============== N: negative control: Redis OFF, two replicas must NOT see each other ===============
console.log('\n--- N: Redis OFF on both replicas (expected to be broken: this is what Phase 16 fixes) ---');
cleanDb();
sh(`T_REDIS= ${COMPOSE} up -d --force-recreate api api2 2>&1 | tail -1`);
await waitHealthy([R1, R2], 90000); await sleep(3000);
const nA = await newUser('NegAlice'), nB = await newUser('NegBob');
const nConv = await newConv(nA, nB);
const nhA = await hub(nA, R1), nhB = await hub(nB, R2);
await sleep(500);
const ns = await dsend(nA, nConv, 'cross replica without redis', R1);
check('P-33', 'without Redis, a message to someone on the OTHER replica is only "sent" (presence is not shared)', ns.status === 201 && ns.json?.status === 'sent', `status=${ns.json?.status}`);
check('P-34', 'without Redis, the other replica gets NO live push', !(await nhB.wait('MessageReceived', () => true, 3000)));
check('P-35', 'without Redis, Alice cannot see that Bob is online (he is on the other replica)', (await peerOnline(R1, nA, nB)) === false);
await nhB.close();
const nhB1 = await hub(nB, R1); await sleep(500);
const ns2 = await dsend(nA, nConv, 'same replica without redis', R1);
check('P-36', 'without Redis, two people on the SAME replica still work (delivered and pushed): the single-replica mode is intact', ns2.json?.status === 'delivered' && !!(await nhB1.wait('MessageReceived', p => p.content === 'same replica without redis', 5000)));
await closeAll(nhA, nhB1);
sh(`${COMPOSE} up -d --force-recreate api api2 2>&1 | tail -1`);
await waitHealthy([R1, R2], 90000);

// =============== T: why the browser client skips negotiation ===============
console.log('\n--- T: through a load balancer with two replicas, with and without the negotiate step ---');
await refreshLb();
const reg = await newUser('Nego');
const attempt = async (skip) => {
  const c = new signalR.HubConnectionBuilder().withUrl(`${LB}/hubs/direct`, { accessTokenFactory: () => reg.token, transport: signalR.HttpTransportType.WebSockets, ...(skip ? { skipNegotiation: true } : {}) }).configureLogging(signalR.LogLevel.None).build();
  try { await c.start(); await c.stop(); return true; } catch { try { await c.stop(); } catch { /* already closed */ } return false; }
};
let withNeg = 0, without = 0; const N = 30;
for (let i = 0; i < N; i++) { if (await attempt(false)) withNeg++; if (await attempt(true)) without++; }
check('P-37', 'WITH the negotiate step, connections through the balancer fail when negotiate and socket land on different replicas (the proof the change is needed)', withNeg < N, `${withNeg}/${N} connected with negotiation`);
check('P-38', 'WITHOUT the negotiate step (what the app does), every connection through the balancer succeeds', without === N, `${without}/${N} connected`);
finish();
