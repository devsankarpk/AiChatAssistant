# End-to-end suites

These suites drive the **real** stack in Docker: real MySQL, RabbitMQ and Redis, real nginx, several API replicas, real
SignalR clients over WebSockets, and real Chrome windows. They are the checks behind sections 9 to 17 of [`TESTING.md`](../TESTING.md).
Nothing is mocked, with one exception on purpose: in the first configuration the Python AI service is replaced by
[`stub/server.py`](stub/server.py), which speaks the same contract but lets a test choose its latency and failures and
counts every call (so "the AI was called exactly 40 times" can be asserted). The second configuration uses the real
Python service and the real LLM provider.

## Run

```bash
./e2e/run.sh                 # everything, about 45 minutes; builds the images on the first run
./e2e/run.sh 04 09           # only suites whose file name starts with these prefixes
./e2e/run.sh --keep 05       # leave the stack running afterwards (default: empty the test data and stop it)
```

Needs Docker, Node 20+ and Google Chrome (set `CHROME_BIN` if it is not at the usual path). `docker.env` is created by
`scripts/gen-docker-env.sh` (the real LLM suites need `DEEPINFRA_API_KEY` in it). Results go to `e2e/results/*.json` and are
summarised at the end. **The tests create throwaway accounts and delete every row they wrote when they finish; they never
touch a MySQL outside the compose stack.**

## The two configurations

| Phase | Compose files | What is in it | Suites |
|---|---|---|---|
| A | `docker-compose.yml` + `e2e/compose.e2e.yml` | three API replicas (ports 18081-18083), a load balancer (18090) in front of them and the web container, the AI stub (18000), RabbitMQ's management API (15673), short timings | `01` to `09` |
| B | `docker-compose.yml` only | the normal stack: one API behind nginx on 8080, the real Python service, the real LLM provider | `10a`, `10b` |

## Suites

| Suite | What it proves | Covers |
|---|---|---|
| `01-foundation` | the brokers are healthy, private, password-protected and durable; existing behaviour and error shapes are unchanged | Phase 15 |
| `02-email-queue` | password-reset email through RabbitMQ: generic answer, delivery, retries, dead letter, broker down and back, durability, expiry, 40 at once, no broker, fail-fast, a paused broker | Phase 17 |
| `03-presence-and-backplane` | two replicas and Redis: pushes cross replicas, shared presence, atomic first/last connection, heartbeat, a killed replica, Redis down/flushed; the same with Redis off (negative control) and why the browser skips negotiation | Phase 16 |
| `04-direct-chat-browser` | the whole user-to-user chat in two real browsers across replicas: ticks, badge, toast, typing, presence, delete, paging, races, privacy, accessibility, phone width, a replica dying | Phases 9 to 14 |
| `05-async-replies` | async AI replies: 202 contract, retries, dead letter, manual retry, duplicates, load across two replicas, a killed replica, broker and Redis down, flags | Phase 18 |
| `06-outbox-events` | direct-chat events: written with the change, ordered, no text, atomic on MySQL, broker outage, two replicas, backlog, bad events, retention, switches | Phase 19 |
| `07-black-hole-and-scale` | a paused (black-holed) RabbitMQ and Redis, three replicas, a 3,000-event backlog, an AI call slower than the API's timeout | after 18 and 19 |
| `08-async-replies-browser` | async replies in the chat page: pending, replied, failed, Send again, reload, lost push, switching chats, two tabs, accessibility | Phase 18 |
| `09-chaos` | combined and sequential failures: broker stops mid-reply, both brokers stop, rolling restart of every replica under load, MySQL down, everything restarting at once, with final consistency invariants | Phase 20 |
| `10a-real-llm-failure` | the real Python service with an invalid provider key (async and default mode), then the right key | after 19 |
| `10b-regression-real-llm` | register, a real AI reply, forgot-password, login error, direct messages with ticks and events, with async replies on and off | regression |

## Adding a check

Suites are plain Node ES modules using `lib.mjs` (`check(id, name, ok, detail)`, then `finish()`). Prefer asserting on what a person or
the database would see (a status code, a row, a message on screen, a queue depth) over internals, give every check a plain-language
name, and put what you measured in the detail. A new suite named `NN-name.mjs` is picked up automatically.
