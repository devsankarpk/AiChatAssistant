import { COMPOSE, useReplicas, dc, sh, check, finish, sleep, waitFor, rest, newUser, hub, mgmt, publishRaw, queues, depth, purgeAll, sql, cleanDb, healthy, apiLogs, R1, R2 } from '../lib.mjs';

// ---- Phase 19: direct-chat events through a transactional outbox, against the real Docker stack ----
const count = (table, where = '1=1') => Number(sql(`SELECT COUNT(*) FROM ${table} WHERE ${where}`));
const start = new Date().toISOString();
const TAP = 'test.tap';
const tapPeek = async (n = 500) => (await mgmt(`/queues/%2F/${TAP}/get`, { method: 'POST', body: { count: n, ackmode: 'ack_requeue_true', encoding: 'auto' } })).json ?? [];
const tapDrain = async () => { await mgmt(`/queues/%2F/${TAP}/contents`, { method: 'DELETE' }); };
const newConv = async (a, b, port = R1) => (await rest(port, '/api/direct/conversations', { method: 'POST', token: a.token, body: { userId: b.id } })).json.id;
const send = (user, conv, content, port = R1) => rest(port, `/api/direct/conversations/${conv}/messages`, { method: 'POST', token: user.token, body: { content } });
const row = (id) => { const [type, disp] = sql(`SELECT Type, DispatchedAt IS NOT NULL FROM OutboxMessages WHERE Id=${id}`).split('\t'); return { type, dispatched: disp === '1' }; };
const outboxIds = () => sql('SELECT Id FROM OutboxMessages ORDER BY Id').split('\n').filter(Boolean).map(Number);
const logLines = (re, since = start) => apiLogs(since).split('\n').filter(l => re.test(l));
const healthyBoth = async () => (await healthy(R1)) && (await healthy(R2));
const secrets = ['violet-elephant-4471', 'moonlit harbour plan 77', 'zebra_quartz#9', 'my private plan for saturday', 'paprika & thyme 2026'];

await waitFor(async () => { try { return (await mgmt('/overview')).status === 200; } catch { return false; } }, 90000, 1000); // RabbitMQ may still be starting
await useReplicas(['api']);
cleanDb(); purgeAll();
await waitFor(() => healthy(R1), 60000, 500);
await mgmt(`/queues/%2F/${TAP}`, { method: 'DELETE' });
await mgmt(`/queues/%2F/${TAP}`, { method: 'PUT', body: { durable: true, auto_delete: false } });
await mgmt(`/bindings/%2F/e/direct.events/q/${TAP}`, { method: 'POST', body: { routing_key: '#' } });
const alice = await newUser('Alice'), bob = await newUser('Bob');
const conv = await newConv(alice, bob);
const exch = (await mgmt('/exchanges/%2F/direct.events')).json;
check('P19-01', 'one API replica runs (the second is stopped for the ordering tests); RabbitMQ has the direct.events topic exchange and the subscriber queue; the outbox is empty', (await healthy(R1)) && count('OutboxMessages') === 0 && exch?.type === 'topic' && queues()['direct.events.log']?.c >= 1);

// =============== A: events are written with the change and published ===============
console.log('\n--- A: every direct-chat change writes an event; it is published in order, and carries no text ---');
const t0 = Date.now();
const m1 = await send(alice, conv, secrets[0]);                       // Bob offline: "sent"
const sentRow = await waitFor(() => { const ids = outboxIds(); return ids.length >= 1 && row(ids[0]).dispatched ? ids[0] : null; }, 10000, 100);
check('P19-02', 'sending a message (recipient offline) writes a "direct.message.sent" outbox row, which is published within about a second', !!sentRow && row(sentRow).type === 'direct.message.sent' && Date.now() - t0 < 4000, `row #${sentRow}, ${Date.now() - t0} ms after the POST`);
const hubBob = await hub(bob, R1);                                    // Bob connects: pending message becomes "delivered"
await sleep(1500);
await rest(R1, `/api/direct/conversations/${conv}/read`, { method: 'POST', token: bob.token }); // Bob reads it
await sleep(800);
await rest(R1, `/api/direct/conversations/${conv}/messages/${m1.json.id}`, { method: 'DELETE', token: alice.token }); // Alice deletes it
await sleep(1500);
const m2 = await send(alice, conv, secrets[1]);                       // Bob is online: "sent" then "delivered"
await sleep(1500);
await rest(R1, `/api/direct/conversations/${conv}/read`, { method: 'POST', token: bob.token });
await rest(R1, `/api/direct/conversations/${conv}/read`, { method: 'POST', token: bob.token });   // nothing unread: no event
await rest(R1, `/api/direct/conversations/${conv}/messages/${m1.json.id}`, { method: 'DELETE', token: alice.token }); // already deleted: no event
await waitFor(() => outboxIds().every(id => row(id).dispatched), 10000, 200);
const types = sql('SELECT Type FROM OutboxMessages ORDER BY Id').split('\n').filter(Boolean);
check('P19-03', 'the whole life of two messages writes exactly these events, in this order (repeat reads and repeat deletes write none)', JSON.stringify(types) === JSON.stringify(['direct.message.sent', 'direct.message.delivered', 'direct.message.read', 'direct.message.deleted', 'direct.message.sent', 'direct.message.delivered', 'direct.message.read']), types.join(' > '));
const tap = await tapPeek();
const tapIds = tap.map(m => Number(m.properties.message_id));
check('P19-04', 'RabbitMQ received the same events, in the same order (message id = outbox id, routing key = event type)', tap.length === types.length && JSON.stringify(tapIds) === JSON.stringify(outboxIds()) && tap.every((m, i) => m.routing_key === types[i]), `${tap.length} events: ${tap.map(m => m.routing_key.replace('direct.message.', '')).join(' > ')}`);
const bodies = tap.map(m => JSON.parse(m.payload));
const keys = [...new Set(bodies.flatMap(b => Object.keys(b)))].sort().join(',');
check('P19-05', 'the payload has exactly: conversationId, messageIds, senderId (the author), recipientId, status, at', keys === 'at,conversationId,messageIds,recipientId,senderId,status' && bodies.every(b => b.conversationId === conv), keys);
check('P19-06', 'the author is Alice and the other person is Bob in every event (also for "read", which Bob caused)', bodies.every(b => b.senderId === alice.id && b.recipientId === bob.id) && bodies.map(b => b.status).join() === 'sent,delivered,read,deleted,sent,delivered,read');
const leaks = secrets.slice(0, 2).map(sec => count('OutboxMessages', `Payload LIKE '%${sec.replace(/'/g, "''")}%'`)).reduce((a, b) => a + b, 0) + tap.filter(m => secrets.some(sec => m.payload.includes(sec))).length;
check('P19-07', 'NO MESSAGE TEXT anywhere: not in any outbox row, not in any message that reached RabbitMQ', leaks === 0, `rows/messages containing the text: ${leaks}`);
const handled = logLines(/Direct event direct\.message\./).filter(l => !/seen before/.test(l));
const handledIds = [...new Set(handled.map(l => Number(l.match(/#(\d+)/)?.[1])))];
check('P19-08', 'the subscriber consumed and logged every event once (ids only in the log line), and both subscriber queues are empty', handledIds.length === types.length && handled.length === types.length && depth('direct.events.log') === 0 && depth('direct.events.dead') === 0 && !handled.some(l => secrets.some(sec => l.includes(sec))), `${handled.length} log lines for ${types.length} events`);
check('P19-09', 'the dispatched rows carry a dispatch time; none is left waiting', count('OutboxMessages', 'DispatchedAt IS NULL') === 0);
await hubBob.close();

// =============== B: atomic with the change, on real MySQL ===============
console.log('\n--- B: the change and its event commit together (real MySQL transaction) ---');
const msgsBefore = count('DirectMessages'), rowsBefore = count('OutboxMessages');
sql('RENAME TABLE OutboxMessages TO OutboxMessages_off');
const broken = await send(alice, conv, secrets[2]);
sql('RENAME TABLE OutboxMessages_off TO OutboxMessages');
check('P19-10', 'if the event cannot be written, the whole request fails (500 in the standard error shape) ...', broken.status === 500 && broken.json?.error?.code, `status ${broken.status}`);
check('P19-11', '... and the message was NOT saved either: the change rolled back with its event (no change without an event)', count('DirectMessages') === msgsBefore && count('DirectMessages', `Content LIKE '%${secrets[2]}%'`) === 0, `messages ${msgsBefore} -> ${count('DirectMessages')}`);
const fixed = await send(alice, conv, secrets[3]);
await waitFor(() => count('OutboxMessages') > rowsBefore, 8000, 200);
check('P19-12', 'with the table back, sending works again and the event is written', fixed.status === 201 && count('OutboxMessages') > rowsBefore);

// =============== C: RabbitMQ is down ===============
console.log('\n--- C: RabbitMQ is down: chatting continues, events wait in the database, nothing is lost or reordered ---');
await waitFor(() => outboxIds().every(id => row(id).dispatched), 10000, 200);
await tapDrain();
const baseline = outboxIds().length;
dc('stop rabbitmq');
const downSends = [];
for (let i = 0; i < 20; i++) downSends.push(await send(alice, conv, `outage message ${i}`));
await sleep(3000);
const waiting = count('OutboxMessages', 'DispatchedAt IS NULL');
check('P19-13', 'broker down: all 20 messages are still sent (201) and 20 events wait undispatched in the database', downSends.every(r => r.status === 201) && waiting === 20, `201s: ${downSends.filter(r => r.status === 201).length}, waiting events: ${waiting}`);
check('P19-14', 'broker down: the API stays healthy and keeps trying (attempts are counted on the waiting rows)', (await healthy(R1)) && Number(sql('SELECT MAX(Attempts) FROM OutboxMessages WHERE DispatchedAt IS NULL')) >= 1, `max attempts ${sql('SELECT MAX(Attempts) FROM OutboxMessages WHERE DispatchedAt IS NULL')}`);
dc('start rabbitmq');
const drained = await waitFor(() => count('OutboxMessages', 'DispatchedAt IS NULL') === 0, 90000, 1000);
await sleep(2500);
const tapC = await tapPeek();
const idsC = tapC.map(m => Number(m.properties.message_id));
check('P19-15', 'broker back: every waiting event is published by itself, none lost', !!drained && tapC.length === 20, `${tapC.length} events arrived`);
check('P19-16', 'they arrive in the order they were written (ascending ids, no gaps, no repeats)', JSON.stringify(idsC) === JSON.stringify([...idsC].sort((a, b) => a - b)) && new Set(idsC).size === idsC.length);
const seenAgain = logLines(/seen before/).length;
check('P19-17', 'the subscriber handled each of them once (no repeats needed ignoring)', seenAgain === 0, `"seen before" lines so far: ${seenAgain}`);

// =============== D: two replicas ===============
console.log('\n--- D: two replicas dispatching from the same table ---');
await tapDrain();
dc('start api2');
await waitFor(healthyBoth, 90000, 500);
const bobHub = await hub(bob, R2);
const t1 = Date.now();
await Promise.all(Array.from({ length: 100 }, (_, i) => send(i % 2 ? alice : bob, conv, `dual ${i}`, i % 2 ? R1 : R2)));
await waitFor(() => count('OutboxMessages', 'DispatchedAt IS NULL') === 0, 60000, 500);
await sleep(2500);
const tapD = await tapPeek(1000);
const idsD = tapD.map(m => Number(m.properties.message_id));
const sentD = tapD.filter(m => m.routing_key === 'direct.message.sent').length;
check('P19-18', '100 messages sent through both replicas at once: all 100 "sent" events reached RabbitMQ (plus their delivered events)', sentD === 100, `sent=${sentD}, total=${tapD.length}`);
check('P19-19', 'EACH OUTBOX ROW WAS PUBLISHED EXACTLY ONCE: the two dispatchers never both took the same row', new Set(idsD).size === idsD.length, `${idsD.length} messages, ${new Set(idsD).size} distinct ids`);
check('P19-20', 'the subscriber (competing across both replicas) saw no repeats, and the queues drained', logLines(/seen before/).length === 0 && depth('direct.events.log') === 0 && depth('direct.events.dead') === 0, `drained in ${((Date.now() - t1) / 1000).toFixed(1)} s`);

console.log('\n--- E: a replica dies while a backlog is being dispatched ---');
await tapDrain();
dc('stop rabbitmq');
for (let i = 0; i < 60; i++) await send(i % 2 ? alice : bob, conv, `backlog ${i}`, i % 2 ? R1 : R2);
const backlog = count('OutboxMessages', 'DispatchedAt IS NULL');
dc('start rabbitmq');
await waitFor(() => { try { return queues()['direct.events.log']?.c >= 1; } catch { return false; } }, 60000, 500);
dc('kill api2');
const finished = await waitFor(() => count('OutboxMessages', 'DispatchedAt IS NULL') === 0, 120000, 1000);
await sleep(2500);
const tapE = await tapPeek(1000);
const idsE = tapE.map(m => Number(m.properties.message_id));
const need = sql('SELECT Id FROM OutboxMessages WHERE Id IN (' + (idsE.length ? idsE.join(',') : '0') + ')').split('\n').filter(Boolean).length;
check('P19-21', 'the surviving replica finishes the whole backlog (rows the dead one had claimed are taken over after the lease)', !!finished && backlog >= 60, `backlog was ${backlog}`);
check('P19-22', 'every backlog event reached RabbitMQ at least once (a repeat is allowed after a crash, loss is not)', new Set(idsE).size >= backlog, `${new Set(idsE).size} distinct of ${backlog}, ${idsE.length - new Set(idsE).size} repeat(s)`);
const repeats = idsE.length - new Set(idsE).size;
const ignored = logLines(/seen before/).length;
check('P19-23', 'if any event was published twice, the subscriber noticed and ignored the repeat', ignored >= repeats, `repeats published: ${repeats}, repeats ignored: ${ignored}`);
dc('start api2');
await waitFor(healthyBoth, 90000, 500);
await bobHub.close();

// =============== F: bad events ===============
console.log('\n--- F: bad and repeated events at the subscriber ---');
const deadBefore = depth('direct.events.dead');
await publishRaw('direct.events', 'direct.message.sent', 'this is not json', { message_id: '900001', type: 'direct.message.sent' });
const bad = await waitFor(() => depth('direct.events.dead') === deadBefore + 1, 8000, 250);
check('P19-24', 'an event that is not valid JSON is moved to the dead-letter queue, not retried forever', !!bad);
await publishRaw('direct.events', 'direct.message.sent', JSON.stringify({ conversationId: conv, messageIds: [], senderId: 1, recipientId: 2, status: 'sent', at: new Date().toISOString() }), { message_id: '900002', type: 'direct.message.sent' });
check('P19-25', 'an event with no message ids is dead-lettered too', !!(await waitFor(() => depth('direct.events.dead') === deadBefore + 2, 8000, 250)));
const good = JSON.stringify({ conversationId: conv, messageIds: [1], senderId: alice.id, recipientId: bob.id, status: 'sent', at: new Date().toISOString() });
await publishRaw('direct.events', 'direct.message.sent', good, { message_id: '900003', type: 'direct.message.sent' });
await publishRaw('direct.events', 'direct.message.sent', good, { message_id: '900003', type: 'direct.message.sent' });
await sleep(3000);
const handled3 = logLines(/Direct event direct\.message\.sent #900003/).filter(l => !/seen before/.test(l)).length;
const repeated3 = logLines(/Direct event direct\.message\.sent #900003 seen before/).length;
check('P19-26', 'the same event delivered twice (same message id) is handled once and the repeat is ignored', handled3 === 1 && repeated3 === 1, `handled ${handled3}, ignored ${repeated3}`);
check('P19-27', 'the subscriber keeps working after all of that: a real message is still logged', await (async () => { const r = await send(alice, conv, 'after the bad ones'); await sleep(2500); return r.status === 201 && logLines(/Direct event direct\.message\.sent #\d+/).length > 5; })());

// =============== G: retention ===============
console.log('\n--- G: old published events are cleaned up ---');
await waitFor(() => count('OutboxMessages', 'DispatchedAt IS NULL') === 0, 20000, 500);
const oldIds = sql('SELECT Id FROM OutboxMessages WHERE DispatchedAt IS NOT NULL ORDER BY Id LIMIT 5').split('\n').filter(Boolean);
sql(`UPDATE OutboxMessages SET DispatchedAt = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 25 HOUR) WHERE Id IN (${oldIds.join(',')})`);
const totalBefore = count('OutboxMessages');
const cleaned = await waitFor(() => count('OutboxMessages', `Id IN (${oldIds.join(',')})`) === 0, 150000, 3000);
check('P19-28', 'events published more than 24 h ago are deleted by the dispatcher (exactly those 5); recent ones are kept', !!cleaned && count('OutboxMessages') === totalBefore - 5, `rows ${totalBefore} -> ${count('OutboxMessages')}`);

// =============== H: switches ===============
console.log('\n--- H: configuration ---');
sh(`T_OUTBOX=false ${COMPOSE} up -d --force-recreate api api2 2>&1 | tail -1`);
await waitFor(healthyBoth, 90000, 1000);
const rowsOff = count('OutboxMessages');
const offSend = await send(alice, conv, 'outbox is off');
await sleep(2000);
check('P19-29', 'Outbox:Enabled=false: chat works (201), no event row is written, and no dispatcher runs', offSend.status === 201 && count('OutboxMessages') === rowsOff && !logLines(/Outbox dispatcher started/, '30s').length);
sh(`T_ASYNC=false T_RABBIT_HOST='' ${COMPOSE} up -d --force-recreate api api2 2>&1 | tail -1`);
await waitFor(healthyBoth, 90000, 1000);
const noBroker = await send(alice, conv, 'no broker at all');
await sleep(1500);
check('P19-30', 'no RabbitMQ configured: chat works exactly as before and the outbox writes nothing (the app runs with only MySQL)', noBroker.status === 201 && count('OutboxMessages') === rowsOff);
sh(`${COMPOSE} up -d --force-recreate api api2 2>&1 | tail -1`);
await waitFor(healthyBoth, 90000, 1000);
await mgmt(`/queues/%2F/${TAP}`, { method: 'DELETE' });

finish();
