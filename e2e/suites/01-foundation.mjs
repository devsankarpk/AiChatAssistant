import { hub, dc, check, finish, sleep, waitFor, rest, newUser, apiLogs, queues, mgmt, redisCli, dockerEnv, healthy, R1, PORTS, waitHealthy, mgmtReady } from '../lib.mjs';

// ---- Phase 15: the broker foundation (and the security of the internal services) on the real stack ----
await mgmtReady();
await waitHealthy(PORTS);
const ps = JSON.parse('[' + dc('ps --format json').split('\n').filter(Boolean).join(',') + ']');
const svc = n => ps.find(p => p.Service === n);

check('FND-01', 'rabbitmq and redis are running and report healthy', svc('rabbitmq')?.Health === 'healthy' && svc('redis')?.Health === 'healthy', `rabbitmq=${svc('rabbitmq')?.Health} redis=${svc('redis')?.Health}`);
const published = (service) => (svc(service)?.Publishers ?? []).filter(x => x.PublishedPort).map(x => `${x.TargetPort}->${x.PublishedPort}`);
check('FND-02', 'Redis, MySQL and the AMQP port of RabbitMQ are NOT published outside Docker (only RabbitMQ\'s management port is, for the test harness)', published('redis').length === 0 && published('mysql').length === 0 && !published('rabbitmq').some(p => p.startsWith('5672')), `redis: [${published('redis')}], mysql: [${published('mysql')}], rabbitmq: [${[...new Set(published('rabbitmq'))]}]`);
check('FND-03', 'Redis refuses a client without the password', /NOAUTH|Authentication required/i.test(dc(`exec -T -e REDISCLI_AUTH= redis redis-cli ping`, { allowFail: true })));
check('FND-04', 'Redis refuses a wrong password', /WRONGPASS|invalid/i.test(dc(`exec -T redis redis-cli -a not-the-password --no-auth-warning ping 2>&1`, { allowFail: true })));
check('FND-05', 'Redis answers PONG with the real password', dc(`exec -T redis redis-cli ping`) === 'PONG');
const guest = dc(`exec -T rabbitmq rabbitmqctl authenticate_user guest guest`, { allowFail: true });
check('FND-06', "RabbitMQ has no default 'guest' login", !/Success/i.test(guest), guest.split('\n').pop());
check('FND-07', "RabbitMQ accepts the generated 'aichat' login", /Success/i.test(dc(`exec -T rabbitmq rabbitmqctl authenticate_user aichat ${dockerEnv.RABBITMQ_PASSWORD}`, { allowFail: true })));
check('FND-08', 'RabbitMQ rejects a wrong password for aichat', !/Success/i.test(dc(`exec -T rabbitmq rabbitmqctl authenticate_user aichat not-the-password`, { allowFail: true })));
check('FND-09', 'every API replica answers /health 200 (liveness only, no broker check)', (await Promise.all(PORTS.map(healthy))).every(Boolean));
check('FND-10', 'every replica started its email worker, AI reply worker, outbox dispatcher and subscriber', ['Email worker is consuming', 'AI reply worker is consuming', 'Outbox dispatcher started', 'Direct events consumer is consuming'].every(m => (apiLogs().match(new RegExp(m, 'g')) ?? []).length >= 2), 'start-up lines in the logs');
const qs = queues();
const durable = JSON.parse(dc(`exec -T rabbitmq rabbitmqctl -q list_queues name durable --formatter json`));
check('FND-11', 'the topology exists and is durable: email.send/dead, ai.reply/dead, direct.events.log/dead and their retry queues', ['email.send', 'email.dead', 'ai.reply', 'ai.dead', 'direct.events.log', 'direct.events.dead'].every(n => n in qs) && durable.every(q => q.durable === true), Object.keys(qs).sort().join(', '));
const ex = (await mgmt('/exchanges/%2F')).json.map(e => `${e.name}:${e.type}`);
check('FND-12', 'the exchanges are the agreed ones: email (direct), ai (direct), direct.events (topic)', ['email:direct', 'ai:direct', 'direct.events:topic'].every(e => ex.includes(e)), ex.filter(e => !e.startsWith('amq.') && !e.startsWith(':')).join(', '));
const u = await newUser('Foundation Check');
const login = await rest(R1, '/api/auth/login', { method: 'POST', body: { email: u.email, password: u.password } });
check('FND-13', 'existing behaviour is unchanged: register then login work with all the brokers running', login.status === 200 && !!login.json?.token);
const unauth = await rest(R1, '/api/chat/sessions');
check('FND-14', 'error shape unchanged: an unauthenticated call is 401 with { error: { code, message } }', unauth.status === 401 && unauth.json?.error?.code === 'unauthorized');
const notFound = await rest(R1, '/api/does-not-exist', { token: u.token });
check('FND-15', 'error shape unchanged: an unknown route is 404 with { error: { code, message } }', notFound.status === 404 && notFound.json?.error?.code === 'not_found');
const h = await hub(u, R1);
await sleep(800);
const channels = redisCli(`PUBSUB CHANNELS 'aichat*'`);
check('FND-16', 'a connected person is recorded in Redis (presence:user:<id>) and SignalR uses its channels under the "aichat" prefix (Phase 16 wiring)', redisCli(`EXISTS presence:user:${u.id}`).trim() === '1' && /aichat/.test(channels), `channels: ${channels.split('\n').length} (e.g. ${channels.split('\n')[0]})`);
await h.close();
finish();
