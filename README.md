# AI Chat Assistant — Project Plan

**Start date:** September 9, 2026
**Stack:** Angular · .NET 8 Web API · Python (FastAPI) · MySQL 8

---

## 0. Getting started

Everything below has been run end-to-end on a fresh clone as of Phase 8 - not just written down and hoped for.

### Prerequisites

| Tool | Version used in dev | Notes |
|---|---|---|
| .NET SDK | 8.0.404 | Pinned via `global.json`; any 8.0.4xx works |
| Node.js | 22.11.0 | `frontend/` pins `@angular/cli@19` in `package.json` - Angular 20+'s CLI needs Node ≥22.12/24, so an older Node like this still works fine as long as you don't bump that pin |
| Python | 3.12.8 | 3.11+ is fine |
| MySQL | 8-compatible (26.7.0 CalVer in dev) | `ServerVersion.AutoDetect` in `Program.cs` handles version differences |
| A DeepInfra API key | — | Free account at [deepinfra.com](https://deepinfra.com/dash/api_keys); or swap `ai-service-python/app/config.py` + `services/llm_client.py` for a different OpenAI-compatible provider |

### 1. Clone and set up the database

```bash
git clone <repo-url>
cd AiChatAssistant
```

Create the database and a user matching the connection string already committed in
`backend-dotnet/src/AiChatAssistant.Api/appsettings.Development.json` (or edit that file's
`ConnectionStrings:Default` to match your own local MySQL instead):

```sql
CREATE DATABASE AiChatAssistant;
CREATE USER 'chemapp'@'localhost' IDENTIFIED BY 'ChemApp123!';
GRANT ALL PRIVILEGES ON AiChatAssistant.* TO 'chemapp'@'localhost';
```

### 2. Backend (.NET)

```bash
cd backend-dotnet
dotnet tool restore   # dotnet-ef, pinned in .config/dotnet-tools.json
dotnet user-secrets set "Jwt:Key" "$(openssl rand -base64 48)" --project src/AiChatAssistant.Api
dotnet user-secrets set "PythonService:InternalApiKey" "$(openssl rand -hex 32)" --project src/AiChatAssistant.Api
dotnet ef database update --project src/AiChatAssistant.Api
```

Keep the `PythonService:InternalApiKey` value you just generated - it goes into the Python
service's `.env` next, and the two **must** match exactly (see CLAUDE.md's Configuration section).

To send real password-reset emails (optional - skip this and the forgot-password flow still
works, see below), also set SMTP credentials. For Gmail: enable 2-Step Verification on the sending
account, generate an App Password at
[myaccount.google.com/apppasswords](https://myaccount.google.com/apppasswords), then:

```bash
dotnet user-secrets set "Smtp:Username" "<your-gmail-address>" --project src/AiChatAssistant.Api
dotnet user-secrets set "Smtp:Password" "<16-char-app-password-no-spaces>" --project src/AiChatAssistant.Api
```

`Smtp:Host`/`Port`/`FromName` (`appsettings.json`) already default to Gmail's SMTP - override them
if you're using a different provider.

### 3. AI service (Python)

```bash
cd ai-service-python
python -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
cp env.example .env
```

Edit `.env`:
- `DEEPINFRA_API_KEY` - from [deepinfra.com/dash/api_keys](https://deepinfra.com/dash/api_keys)
- `INTERNAL_API_KEY` - the exact value you generated for `PythonService:InternalApiKey` above

### 4. Frontend (Angular)

```bash
cd frontend
npm install
```

### Run all three

```bash
# terminal 1
cd ai-service-python && source .venv/bin/activate && uvicorn app.main:app --reload --port 8000

# terminal 2
cd backend-dotnet && dotnet run --project src/AiChatAssistant.Api

# terminal 3
cd frontend && npm start
```

Open `http://localhost:4200`, register an account, start chatting.

### Run everything in Docker (alternative to running the three services by hand)

One command starts MySQL, the database migration, the .NET API, the Python service and the Angular site (served by nginx) in containers. You do not need .NET, Python, Node or a local MySQL for this, only Docker.

```bash
./scripts/gen-docker-env.sh                       # once: creates docker.env (gitignored) with random secrets
# edit docker.env and set DEEPINFRA_API_KEY=<your key>   (SMTP_* is optional, only for real reset emails)
docker compose --env-file docker.env up --build -d
```

Open **http://localhost:8080**. Stop with `docker compose --env-file docker.env stop` (data is kept in the `mysql-data` volume); `docker compose --env-file docker.env down -v` also deletes the data.

| Service | What it is |
|---|---|
| `mysql` | MySQL 8.4 with its own `mysql-data` volume. It is separate from any MySQL you run on your machine. |
| `migrate` | One-off job: applies the EF Core migrations (a self-contained migration bundle, not run at API startup), then exits. |
| `ai-service` | The Python/FastAPI LLM service. Not published to the host. |
| `api` | The .NET API. Not published to the host; it has a liveness-only `/health`. |
| `web` | nginx serving the Angular build and proxying `/api` to `api`, so the browser is same-origin and needs no CORS. Published on port 8080. |

Notes:
- All settings come from the one gitignored `docker.env` (template: `docker.env.example`). `INTERNAL_API_KEY` is generated once and shared by the API and the Python service.
- The containers run as non-root users. nginx's `proxy_read_timeout` (75 s) must stay above the API's 60 s Python timeout.
- To become an Admin in this stack, run the SQL from "Becoming an Admin" against the `mysql` container (`docker compose --env-file docker.env exec mysql mysql -uaichat -p AiChatAssistant`).

### Forgot password

Sends a real email via SMTP (`SmtpEmailSender`) if you set the `Smtp:Username`/`Smtp:Password`
secrets in step 2 above. Click "Forgot password?" on the login page, enter the account's email,
and the reset link arrives in that inbox.

If you skipped the SMTP secrets, `SmtpEmailSender` will throw at send time (fail-fast, same
pattern as a missing `Jwt:Key`) - swap `Program.cs`'s DI registration back to `ConsoleEmailSender`
(logs the link instead of sending it - see CLAUDE.md's Configuration section) if you'd rather not
set up SMTP for local dev.

On some networks, .NET's TLS revocation check can't reach the mail provider's OCSP responder and
the connection fails with an `SslHandshakeException` even though the certificate is valid -
`SmtpEmailSender` already disables revocation checking for this reason (see its comment).

**No SMTP configured?** The reset email is simply not sent: `POST /api/auth/forgot-password` still returns the same generic `200` for every email (so no account is revealed) and the API logs `Could not send the password reset email for user <id>` with the reason. Set `SMTP_USERNAME` and `SMTP_PASSWORD` (a Gmail App Password) to send real emails. (A bug where this returned `500` for registered emails was found in the 2026-10-20 test run and fixed; see `TESTING.md` section 9.6.)

### Becoming an Admin

There's no self-serve path (by design - see `AuthController.Register` in CLAUDE.md's notes). Grant
it directly in the database for the account you just registered:

```sql
INSERT INTO UserRoles (UserId, RoleId)
SELECT Id, 1 FROM Users WHERE Email = 'you@example.com';
```

(`RoleId` 1 is `Admin`, seeded by the `InitialCreate` migration.) Log out and back in afterward -
roles are baked into the JWT at login, not re-checked per request, so an already-issued token
won't pick this up on its own.

### Running the tests

The full test plan, test-case catalogue and manual smoke checklist are in [`TESTING.md`](TESTING.md).

```bash
# .NET (ChatController mocks IPythonAiClient, AuthController its own dependencies)
cd backend-dotnet && dotnet test

# Python (mocks the LLM SDK - no real DeepInfra calls)
cd ai-service-python && source .venv/bin/activate && pytest

# Angular (AuthService, both interceptors, every component)
cd frontend && npm test -- --watch=false --browsers=ChromeHeadless
```

If Angular's tests can't find Chrome, point `CHROME_BIN` at it first, e.g. on macOS:
```bash
export CHROME_BIN="/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
```

> **What "run end-to-end on a fresh clone" above actually means:** the steps in this section were
> followed literally - `git clone` into an empty directory, new `.venv`, new `node_modules`, freshly
> generated `Jwt:Key`/`PythonService:InternalApiKey`/`INTERNAL_API_KEY` - reusing nothing from any
> other checkout except the already-running MySQL server and its `AiChatAssistant` database/user
> (already matching the committed dev connection string, so the `CREATE DATABASE`/`CREATE USER`
> statements above weren't re-run against it - `dotnet ef database update` still ran for real and
> correctly reported "already up to date"). All three test suites passed from that clone
> (`dotnet test`, `pytest`, `ng test`), and a real browser register → chat → real DeepInfra reply →
> hard-refresh round trip worked end to end. The one thing this doesn't cover: a MySQL server with
> nothing on it yet, so the `CREATE DATABASE`/`CREATE USER` SQL above is standard, unremarkable
> syntax that wasn't separately exercised against this specific server.

---

## 1. Overview

A moderate-depth chat application that integrates an LLM (originally scoped as OpenAI/Anthropic; built against **DeepInfra**'s OpenAI-compatible API for cost reasons — see Phase 3) through a split backend: a .NET Web API for auth, business logic, and orchestration, and a Python FastAPI microservice dedicated to LLM calls. This is the foundation app — later projects (RAG document Q&A, ticket system, resume screener, meeting summarizer) reuse this same skeleton.

### Architecture

```
Angular (UI)
   |  REST + JWT
   v
.NET Web API (auth, roles, orchestration)
   |  internal HTTP call            \
   v                                 v
Python FastAPI (LLM service)      MySQL (users, sessions, messages, usage logs)
   |
   v
OpenAI / Anthropic API
```

### Tech stack

| Layer | Technology |
|---|---|
| Frontend | Angular 17+, Angular Material or Tailwind, RxJS |
| Backend API | .NET 8, EF Core, ASP.NET Identity/JWT, FluentValidation, Serilog |
| AI service | Python 3.11+, FastAPI, `openai` SDK (pointed at DeepInfra's OpenAI-compatible API), Uvicorn |
| Database | MySQL 8, Pomelo.EntityFrameworkCore.MySql |
| Infra | Docker Compose (`docker-compose.yml`, Dockerfiles for the API, migration, Python service and web) |

### Repo structure

```
ai-chat-assistant/
├── frontend/                # Angular app
│   └── src/app/
│       ├── auth/            # login, register, guards, interceptor
│       ├── chat/            # chat UI, session list, message stream
│       ├── admin/           # usage logs page
│       └── core/            # services, models
├── backend-dotnet/          # .NET Web API
│   └── src/
│       ├── Controllers/     # AuthController, ChatController, AdminController
│       ├── Models/          # User, ChatSession, Message, UsageLog
│       ├── Services/        # PythonAiClient (HttpClient wrapper)
│       ├── Data/             # AppDbContext, Migrations
│       └── Middleware/       # ExceptionHandlingMiddleware
├── ai-service-python/       # FastAPI service
│   └── app/
│       ├── main.py
│       ├── routes/generate.py
│       ├── services/llm_client.py
│       └── auth/api_key.py
└── docker-compose.yml
```

### Database schema (MySQL)

```sql
Users (id, name, email, password_hash, created_at)
Roles (id, name)              -- 'Admin', 'User'
UserRoles (user_id, role_id)
ChatSessions (id, user_id, title, created_at)
Messages (id, session_id, role ENUM('user','assistant'), content, created_at)
UsageLogs (id, user_id, session_id, tokens_used, cost_estimate, created_at)
```

### Key contracts

**.NET → Python request**
```json
POST /generate
{
  "session_id": "guid",
  "history": [{"role": "user", "content": "..."}],
  "prompt": "latest user message"
}
```

**Python → .NET response**
```json
{
  "reply": "...",
  "tokens_used": 342
}
```

**.NET → Angular (send-message response)**
```json
{
  "userMessage": {...},
  "assistantMessage": {...}
}
```

---

## 2. Phase-by-phase plan

### Phase 1 — Database & data layer
**Goal:** Running MySQL instance with schema and EF Core models.

- [x] Install/run MySQL (local instance, MySQL 26.7.0), create `AiChatAssistant` database
- [x] `dotnet new sln -n AiChatAssistant` + `dotnet new webapi -n AiChatAssistant.Api` (under `backend-dotnet/`, pinned to .NET 8 via `global.json`)
- [x] Add packages: Pomelo.EntityFrameworkCore.MySql 8.0.3, EF Core Design 8.0.11, JwtBearer 8.0.11, FluentValidation.AspNetCore 11.3.0, Serilog.AspNetCore 8.0.3
- [x] Define entities: `User`, `Role`, `UserRole`, `ChatSession` (Guid PK), `Message` (enum role), `UsageLog`
- [x] Create `AppDbContext`, configure relationships/FKs, seed `Admin`/`User` roles
- [x] `dotnet ef migrations add InitialCreate` → `dotnet ef database update`
- [x] Verified tables + FKs + enum + `CURRENT_TIMESTAMP(6)` defaults via MySQL CLI; insert round-trip tested

**Exit criteria:** Tables exist matching schema; migrations run cleanly on a fresh DB. ✅ Met.

---

### Phase 2 — Authentication (.NET only)
**Goal:** Working register/login/JWT, testable via Postman.

- [x] `POST /api/auth/register` — hash password (BCrypt.Net-Next), create user, assign default `User` role
- [x] `POST /api/auth/login` — verify password, issue JWT (claims: sub, email, role)
- [x] Configure JWT middleware in `Program.cs`
- [x] `GET /api/auth/me` — protected test endpoint returning claims
- [x] `[Authorize(Roles = "Admin")]` placeholder admin endpoint (`AdminController.Ping`)
- [x] FluentValidation for register/login DTOs
- [x] Global exception middleware → consistent `{ error: { code, message } }` shape (`ExceptionHandlingMiddleware`, plus the same shape for model-binding/validation failures)

**Exit criteria:** Register → login → call `/me` with token succeeds; no token → 401. ✅ Met (verified by code review + `dotnet build`; live curl smoke test was blocked by this environment's network permissions — worth a manual pass with `AiChatAssistant.Api.http` or `docker-compose`'s eventual test runner).

---

### Phase 3 — Python LLM service (standalone)
**Goal:** FastAPI service returning LLM replies, testable without .NET.

- [x] `pip install fastapi uvicorn openai python-dotenv` — LLM provider is **DeepInfra**, not OpenAI/Anthropic directly: it exposes an OpenAI-compatible API (`https://api.deepinfra.com/v1/openai`), so the `openai` SDK is used unmodified, just pointed at a different `base_url` with a DeepInfra key. Model: `meta-llama/Meta-Llama-3.1-8B-Instruct-Turbo` (small, ~$0.02/$0.04 per 1M tokens — a deliberately cheap choice for demo purposes, not DeepInfra's larger/flagship models)
- [x] `.env` with `DEEPINFRA_API_KEY` + `INTERNAL_API_KEY` (see `ai-service-python/env.example`)
- [x] `POST /generate` — request/response models (`app/schemas.py`), builds message list (`history` + `prompt`), calls LLM (`app/services/llm_client.py`)
- [x] `X-Internal-Key` header check dependency (`app/auth/api_key.py`) — reject unauthorized calls with `401`
- [x] Error handling — LLM timeout/connection error → `503`; rate limit → `503`; any other upstream or unexpected error → `502`; service never crashes with a bare `500`
- [ ] (Optional) `/generate/stream` via SSE for streaming responses — skipped, still optional

**Exit criteria:** `curl -X POST /generate` with valid key returns a real LLM reply + token count. ✅ Met — verified live: no/wrong `X-Internal-Key` → `401`; valid key + real history → `200 {"reply": "...", "tokens_used": 57}` with a reply that correctly used the conversation history; an intentionally bad model name → `502` with a clean error body, not a crash.

---

### Phase 4 — Wire .NET ↔ Python ↔ MySQL
**Goal:** End-to-end message flow: persist → call LLM → persist reply → log usage.

- [x] Register typed `HttpClient` for Python service in `Program.cs` — `PythonService:BaseUrl` (`appsettings.json`) + `PythonService:InternalApiKey` (user-secrets, must match `ai-service-python`'s `INTERNAL_API_KEY`)
- [x] `IPythonAiClient.GenerateAsync(...)` implementation (`Services/PythonAiClient.cs`) — explicit `[JsonPropertyName]` DTOs keep the snake_case wire contract exact regardless of the rest of the API's naming policy
- [x] `POST /api/chat/sessions` — create session for authenticated user (`ChatController.CreateSession`; untitled sessions default to "New chat")
- [x] `POST /api/chat/sessions/{id}/messages`:
  - [x] Validate session ownership (or Admin) — 404 (not 403) for "not yours", to avoid confirming another user's session exists
  - [x] Save user message — committed on its own, before the Python call
  - [x] Fetch recent history — last 20 messages, chronological order
  - [x] Call Python service
  - [x] Save assistant message + usage log
  - [x] Handle Python failure gracefully (503, not 500) — verified live by killing the Python process mid-flow
- [x] `GET /api/chat/sessions` and `GET /api/chat/sessions/{id}/messages`

**Exit criteria:** Full round trip in Postman produces a real AI reply and correct DB rows. ✅ Met — verified live (Node `fetch`, Postman not installed in this environment): register → create session → two message turns, second turn correctly recalled context from the first (real DeepInfra replies, not a stub) → `GET` sessions/messages both reflect it. Also verified: cross-user access → `404`; no token → `401`; empty content → `400`; Python process killed mid-request → `503` with the user's message still persisted, confirmed via a follow-up `GET`.

---

### Phase 5 — Angular: auth
**Goal:** Login/register UI wired to real API.

- [x] `ng new ai-chat-assistant-ui --routing --style=scss` (Angular 19, standalone components; scaffolded into `frontend/` via `--directory`; CLI pinned to `@angular/cli@19` — Node on this machine predates the Node requirement for Angular 20+)
- [x] `AuthService`: register/login/logout, JWT storage (`localStorage`, signal-based state)
- [x] Reactive forms with validation
- [x] `authInterceptor` — attaches Bearer token (scoped to `environment.apiBaseUrl` only)
- [x] `authGuard` and `adminGuard`
- [x] Decode JWT client-side for UI conditionals (not for real authorization) — `core/utils/jwt.ts`
- [x] `.NET`: added a `Cors` policy (`Program.cs` + `appsettings.json` `Cors:AllowedOrigins`) so the Angular dev origin can call the API — not an explicit checklist line above, but required for any of this to work from a real browser

**Exit criteria:** Register/login works; protected routes redirect correctly; session persists on refresh. ✅ Met — verified live by scripting an actual headless Chrome through register → home → refresh → `/admin/ping` (redirected, correctly, for a `User`) → logout → `/` (redirected to `/login?returnUrl=%2F`) → re-login, then again as a promoted Admin through to a real `pong (admin-only)` from `AdminController`.

---

### Phase 6 — Angular: chat UI
**Goal:** Working chat experience against the real backend.

- [x] `ChatService`: getSessions, createSession, getMessages, sendMessage
- [x] Sidebar: session list + "New chat"
- [x] Chat window: message list, input box, send button
- [x] Optimistic user message render + "thinking" indicator
- [x] Error state with retry if send fails

Replaces Phase 5's `HomeComponent` placeholder (deleted). Session selection is a query param
(`?session=<id>`), not a path segment (`/chat/:id`) — see `app.routes.ts`'s comment: two path-based
routes pointing at the same lazy-loaded component look equivalent but aren't, since Angular's
default route reuse strategy destroys and recreates the component on every transition between
them, silently orphaning any subscription the old instance was holding (a chat send's response
arriving after such a navigation, updating a signal nothing renders anymore, was a real bug caught
by live testing below — not a hypothetical).

**Exit criteria:** Full browser round trip — login → new chat → send → real LLM reply → persists on refresh. ✅ Met — verified live by scripting an actual headless Chrome (puppeteer-core against this machine's installed Chrome) through: register → type directly into the empty state (no session yet, one is lazily created) → real DeepInfra reply → second turn correctly recalls the first → hard refresh → exact same conversation still there → "New chat" → fresh empty session in the sidebar. Separately verified the failure path: killed the Python process → send shows a failed bubble with a "Failed to send." + Retry inline, and the error banner; restarted Python → clicked Retry → succeeds and clears the failed state.

Three real bugs were found and fixed this way, not by review — all in `ChatComponent`'s
interaction with routing/effects, not in `ChatService` or the backend:
1. Two route configs (`''` and `'chat/:sessionId'`) for the same component meant Angular
   destroyed and recreated it on that specific transition, orphaning the send's own subscription.
   Fixed by using one route + a query param instead (see above).
2. `toSignal(route.queryParamMap...)` re-emits on every new `ParamMap` object even when the
   `session` value is unchanged, so the load-messages effect fired more than once per navigation.
   Fixed with `distinctUntilChanged()`.
3. Even after (1) and (2), Angular's effect scheduling has no guarantee of running before
   `router.navigate()`'s returned promise resolves — a `messages.set([])` meant to run "before" a
   send could still land after it. Fixed by clearing `messages` synchronously at the call site
   (`send()`/`newChat()`) instead of inside the effect.

---

### Phase 7 — Admin usage view
**Goal:** Role-based data access end-to-end.

- [x] `.NET`: `GET /api/admin/usage` (Admin-only), paginated (`page`/`pageSize`, capped at 100), date filter (`from`/`to`, `400` if `from` > `to`)
- [x] Angular: `admin/usage` page — table + filter, guarded route (replaces Phase 2's `admin/ping` placeholder page, though the backend `GET /api/admin/ping` endpoint itself is kept as a minimal RBAC smoke-test route)
- [x] (Optional) simple usage-over-time chart — a lightweight CSS bar chart of tokens-per-day, scoped to the currently-loaded page of results (not a separate aggregate endpoint)

**Exit criteria:** `User` role blocked (guard + 403); `Admin` sees all usage data. ✅ Met — verified live: a plain `User` gets `403` calling `GET /api/admin/usage` directly, the "Usage logs" sidebar link isn't even rendered for them, and navigating straight to `/admin/usage` by URL redirects to `/` (`adminGuard`). The same account promoted to `Admin` sees a real row (user name/email, session title, tokens, cost) in the table, and the date-range filter correctly narrows results (a future-only range shows the empty state; an inverted range is rejected with `400`).

---

### Phase 8 — Polish & hardening

- [x] Global HTTP error interceptor (Angular) → toasts + loading states — `core/error.interceptor.ts` + `LoadingService`/`ToastService`, layered on top of (not replacing) each component's own inline handling; toasts only truly unexpected failures (network down, uncaught `500`/`502`) and auto-logs-out + redirects to `/login` on a stale/invalid token
- [x] Confirm consistent error shape across .NET → Angular — audit found a real gap: `[Authorize]` rejections and unmatched routes bypassed MVC entirely, falling through to ASP.NET Core's bare empty-body `401`/`403`/`404`. Fixed via `JwtBearerEvents.OnChallenge`/`OnForbidden` and `app.MapFallback` in `Program.cs`
- [x] Config hygiene: `.gitignore` secrets, commit `.example` config files — audited; only the documented, intentional dev-DB-password exception is committed, everything else is `.env`/user-secrets
- [x] Tests: .NET controller (mocked Python client), Python `/generate` (mocked LLM), Angular `AuthService`/interceptor — `backend-dotnet/tests/AiChatAssistant.Api.Tests/` (xUnit, 23 tests), `ai-service-python/tests/` (pytest, 10 tests), Angular gained `AuthService`/`authInterceptor`/`errorInterceptor` specs (24 tests total across the app)
- [x] Top-level README with full setup instructions — section 0 above

**Exit criteria:** Fresh clone + README gets a new dev to a working app. ✅ Met — verified via an actual fresh `git clone` into a clean directory, following section 0's steps literally (not from a pre-configured environment), through to a real chat exchange in the browser. See the note at the end of section 0 for exactly what that run covered.

### Roadmap — execution order for the planned work

Phases 1–8, test phase T1 and Phases 9–13 are **done**. Everything below is **planned**, and is built **one phase at a time in this order**, with a review checkpoint after each. **Phase numbers are stable IDs, not the order**: they were assigned as each plan was written, so the sequence below differs from the numbers. The detailed checklist for each phase is in its own section further down.

| Step | Phase | What it delivers | Needs first | Status |
|---|---|---|---|---|
| 1 | **T2** | .NET gap tests, including the `WebApplicationFactory` test setup | — | **Next up** |
| 2 | **21** | Rate limiting, lockout, real client IP, security headers | T2 | Planned |
| 3 | **22** | Change password, profile, delete account, optional email verification | — | Planned |
| 4 | **23** | Access and refresh tokens, real logout | 22 | Planned |
| 5 | **24** | Chat rename, delete, search, paging | — | Planned |
| 6 | **25** | Streaming replies, Stop and Regenerate, token-aware history | — | Planned |
| 7 | **26** | Cost tracking, admin user management | — | Planned |
| 8 | **27** | Health/readiness, metrics, CI, backups, retention, data export | — | Planned |
| 9 | **28** | 404 page, accessibility check, optional i18n | — | Planned |
| 10 | **9** | Top navigation, appointments placeholder | — | **Done** (early, at the user's request) |
| 11 | **10** | Direct-messaging tables and REST | — | **Done** (early, at the user's request) |
| 12 | **11** | SignalR, delivered and read ticks | 10 | **Done** (early, at the user's request) |
| 13 | **12** | Messages UI with ticks, badge and toast | 11 | **Done** (early, at the user's request) |
| 14 | **13** | Typing, online / last seen, delete message | 12 | **Done** (early, at the user's request) |
| 15 | **14** | End-to-end check of the chat feature | 13 | Planned |
| 16 | **15** | Redis and RabbitMQ foundation | 11 | Planned |
| 17 | **17** | Async emails (RabbitMQ) | 15 | Planned |
| 18 | **16** | Redis: SignalR backplane and presence | 11, 15 | Planned |
| 19 | **18** | Async AI replies (revisit after streaming, Phase 25) | 11, 15 | Planned |
| 20 | **19** | Direct-chat event fan-out | 11, 15 | Planned |
| 21 | **20** | End-to-end check of the brokers | 16–19 | Planned |
| 22 | **T3** | Python gap tests | — | Planned |
| 23 | **T4** | Angular gap specs | — | Planned |
| 24 | **T5** | Test close-out | T3, T4 | Planned |

**Carry-overs created by this order** (work in an earlier phase that a later phase must honour; each is also a checklist item in the later phase):
- **Phase 23 before 11:** the SignalR hub (Phase 11) must authenticate with the *refreshed* access token (`accessTokenFactory`), not a stale one.
- **Phase 22 before 10:** Phase 10 must extend delete-account to remove or anonymise a user's direct conversations and messages.
- **Phase 26 before 11:** the hub must reject connections from a disabled user.
- **Phase 21 before 11:** the nginx Content-Security-Policy must allow the `/hubs/` WebSocket (`connect-src` for `wss:`/`ws:`), or the browser blocks the hub.
- **Phase 21 before 16:** the rate limiter starts in memory and moves to Redis when Phase 16 lands.

### Post-plan — User-to-user chat (Phases 9–14) — **planned, not started**

> Status: **planned.** Nothing in this section exists in the code yet; the checkboxes below are all open. The schema and contracts in sections 1 and 2 above describe what is built today; this section describes what is *about to be* added. Each phase is built, tested and reviewed on its own, one at a time.

**Goal:** real chat between registered users (separate from the AI chatbot), with WhatsApp-style ticks — one grey tick = sent, two grey ticks = delivered, two blue ticks = read — plus a top navigation bar: **Chatbot**, **Messages**, **Book appointment**.

**Decisions**
- **Appointments:** the "Book appointment" tab is a "coming soon" placeholder for now. Real booking (doctors, slots, bookings) is a separate, later task.
- **Who can chat:** any registered user can message any other, one-to-one only (no groups).
- **Finding people:** search by **name**; results show the name only, never an email.
- **Live updates:** SignalR (WebSocket). New messages and tick changes are pushed instantly.
- **Extras in v1:** typing indicator, online / last seen, delete your own message.
- **Alerts:** a live unread badge on the Messages tab, plus a toast (sender and a preview) when a message arrives and you are not looking at that conversation.
- **Read receipts:** always on; there is no setting to turn them off.

**Planned schema** (one new migration, `AddDirectMessaging`)
```sql
DirectConversations (id GUID, user_low_id, user_high_id, created_at)   -- UNIQUE (user_low_id, user_high_id): one conversation per pair
DirectMessages (id BIGINT, conversation_id, sender_id, content, created_at,
                delivered_at NULL, read_at NULL, deleted_at NULL)
Users  + last_seen_at NULL
```
A message's tick state is derived: `read_at` set = read, else `delivered_at` set = delivered, else sent.

**Tick rules**
- **Sent** (one grey tick): the message is saved.
- **Delivered** (two grey): the recipient had a live connection when it was saved, or connected later (pending messages are marked delivered on connect).
- **Read** (two blue): the recipient has that conversation open with the tab visible; the client calls the read endpoint.
- **Deleted:** only the sender can delete their own message. The text is cleared on the server, and both sides see "This message was deleted".

**Planned REST endpoints** (all require a JWT; errors use the usual `{ error: { code, message } }` shape; a conversation you are not part of returns `404`, not `403`)

| Method and path | Purpose |
|---|---|
| `GET /api/direct/users?search=` | Name search (min 2 characters, max 20 results), returns `{ id, name }`, never yourself |
| `GET /api/direct/conversations` | Your conversations: peer, last message, unread count, `peerOnline`, `peerLastSeenAt` |
| `POST /api/direct/conversations` | Start a conversation with `{ userId }` (idempotent; `400` for yourself, `404` for an unknown user) |
| `GET /api/direct/conversations/{id}/messages` | Messages in a conversation (paged) |
| `POST /api/direct/conversations/{id}/messages` | Send a message |
| `POST /api/direct/conversations/{id}/read` | Mark the peer's messages as read |
| `DELETE /api/direct/conversations/{id}/messages/{messageId}` | Delete your own message |

**Planned SignalR hub:** `/hubs/direct` (JWT passed as the `access_token` query string, since browsers cannot set headers on a WebSocket). Server → client events: `MessageReceived`, `MessageStatusChanged`, `MessagesRead`, `MessageDeleted`, `PeerTyping`, `PeerPresenceChanged`. Client → server: `Typing(conversationId)`. Sending goes over REST, so validation and error handling stay in one place; the hub carries pushes.

**Limits to know about**
- **Direct messages are private.** Admins get no special access; an admin asking for someone else's conversation gets `404` like anyone else.
- **Presence is kept in memory**, so the API must stay at **one replica** (it already is, because the migration initContainer needs that). Scaling out needs a shared presence store, which Phase 16 (Redis) adds.
- Online status and last seen are visible to people you share a conversation with, and cannot be hidden in v1.

#### Phase 9 — Top navigation and appointments placeholder *(frontend only, no database)* — **done**
- [x] `shared/top-nav/`: a **Chatbot** tab (the Messages tab arrives in Phase 12, so there is no dead link before then)
- [x] `/appointments` route and page: "coming soon"
- [x] Chat layout adjusted for the bar above the sidebar; the mobile drawer still works
- [x] Spec for the top nav; screenshots at desktop and 390px width with the API faked

> Done. `shared/top-nav/` (brand, a Chatbot tab, a slot for page controls; `?session=` doesn't deactivate the tab) is used by the chat page and the new `/appointments` page (`appointments/`, guarded by `authGuard`). **Book appointment is a pinned row at the very top of the chat sidebar** (calendar icon and a "Soon" tag; it does not scroll with the chat list), and **New chat is a round icon-only "+" button at the top-right of the sidebar header**, next to the "Chats" title, like a messaging app. The sidebar lost its brand mark, since the top bar carries it. On screens narrower than 40rem the top bar's projected controls wrap onto their own row. 10 new specs (56 Angular tests total); verified with the API faked at desktop width, 390 px, in dark theme, with the mobile drawer, and by behaviour (the pinned row and header stay put while 45 chats scroll, "+" opens a new chat, the pinned row opens `/appointments`). The component style budget warning in `angular.json` is now 7 kB (error stays 10 kB). Done ahead of the Roadmap's order at the user's request (it has no dependencies); the Roadmap's "next up" is still T2.

#### Phase 10 — Direct messaging data layer and REST *(backend, schema only)* — **done**
- [x] Models `DirectConversation` and `DirectMessage`, `User.LastSeenAt`, `AppDbContext` mapping
- [x] Migration `AddDirectMessaging` (generated, **not** applied to the dev database without asking)
- [x] `DirectChatController`: name-only user search, list and start conversations, list and send messages, `404` for non-participants
- [x] DTOs and validators
- [x] *Carry-over from Phase 22:* deleting a user removes their direct conversations and messages (foreign keys cascade; verified on real MySQL). The delete-account feature itself arrives with Phase 22, which must add test ACC-06
- [x] Tests (xUnit, Moq, EF InMemory): pair normalised and idempotent; self, outsider and admin rules; search never returns emails or yourself; send stores a message with status Sent

> Done. **Schema:** migration `20261014165721_AddDirectMessaging` adds `Users.LastSeenAt` and the tables `DirectConversations` (unique lower/higher user-id pair) and `DirectMessages` (`DeliveredAt`, `ReadAt`, `DeletedAt` nullable; the tick state is derived from them). The migration is **applied only to the Docker stack's database**; it has **not** been applied to your local dev MySQL. Run `dotnet ef database update --project src/AiChatAssistant.Api` (from `backend-dotnet/`) when you want it there. **Endpoints** (all require a JWT; `404` for anyone who is not a participant, Admins included): `GET /api/direct/users?search=` (name-only, 2+ characters, max 20, `{ id, name }`), `GET /api/direct/conversations` (peer, last message, unread count, `peerOnline` always `false` and `peerLastSeenAt` until Phase 11), `POST /api/direct/conversations` (`{ userId }`; `201` created, `200` already existed, `400` for yourself, `404` for an unknown user), `GET /api/direct/conversations/{id}/messages?before=&limit=` (oldest first within the page; default 50, max 100), `POST /api/direct/conversations/{id}/messages` (`201`, status `sent`). Not built yet: read receipts and delivered status (Phase 11), delete message and typing (Phase 13). 35 new .NET tests (61 total); the unique index, cascade and new tables were also verified against real MySQL in the Docker stack, with throwaway rows deleted afterwards. Done ahead of the Roadmap's order at the user's request; the Roadmap's "next up" is still T2.

#### Phase 11 — Realtime, delivered and read *(backend)* — **done**
- [x] `IPresenceTracker` and `IDirectNotifier` (wraps `IHubContext`), so controllers and tests need no hub
- [x] `DirectHub` at `/hubs/direct`, JWT via query string, per-user group, pending messages marked delivered on connect
- [x] Send marks **Delivered** when the recipient is online; `POST .../read` sets `read_at` and notifies the sender
- [x] `Program.cs` (SignalR, `MapHub`, CORS `AllowCredentials`) and an `nginx.conf` `/hubs/` WebSocket location with a long read timeout
- [x] *Carry-overs:* these three obligations belong to phases that are not built yet, so they are tracked there and in `TESTING.md` rather than here: the hub must use the refreshed access token (Phase 23, row TOK-04), refuse disabled users (Phase 26, row COST-05), and the nginx CSP must allow the WebSocket (Phase 21, row SEC-06)
- [x] Tests: delivered only when online, connect delivers pending, read notifies the sender, multi-tab presence, `last_seen_at` written only on the last disconnect

> Done. **Realtime:** `Hubs/DirectHub` at `/hubs/direct` (`[Authorize]`; the JWT comes as the `access_token` query string, accepted only for `/hubs` paths via `JwtBearerEvents.OnMessageReceived`; each connection joins the group `user-<id>`). `Services/InMemoryPresenceTracker` (`IPresenceTracker`: first connection = online, last disconnect = offline and writes `User.LastSeenAt`), `Services/SignalRDirectNotifier` (`IDirectNotifier`: best-effort pushes that never throw into a request) and `Services/DirectDeliveryService` (on connect, everything waiting for the user becomes delivered and each sender is told). **Ticks:** sending while the recipient is online stores `DeliveredAt` at once (status `delivered`); otherwise `sent`, and it becomes `delivered` when they connect. New endpoint `POST /api/direct/conversations/{id}/read` marks the peer's unread messages read (`ReadAt`, and `DeliveredAt` if empty) and returns `{ conversationId, readCount, upToMessageId }`; non-participants and Admins get `404`. **Events** (server to client): `MessageReceived` (the message, to the recipient), `MessageStatusChanged` `{ conversationId, messageIds, status }` with status `delivered` or `read` (to the sender), `MessagesRead` `{ conversationId, upToMessageId }` (to the reader's own other tabs, to clear their unread count). `peerOnline` in the conversation list now reflects live presence. **Infra:** CORS now allows credentials (SignalR negotiate), and `frontend/nginx.conf` has a `/hubs/` location with WebSocket upgrade headers and a 1-hour read timeout. **Limits:** presence is in memory, so the `api` must stay at one replica until Phase 16; a sender's other tabs don't get a live copy of their own message (Phase 12 can refetch or add an event). 26 new .NET tests (87 total). Verified end to end on the Docker stack with a real SignalR client over real WebSockets through nginx: all 10 realtime checks passed (see `TESTING.md` section 9). Done ahead of the Roadmap's order at the user's request; the Roadmap's "next up" is still T2.

#### Phase 12 — Messages UI with ticks *(frontend)* — **done**
- [x] `@microsoft/signalr`; an app-wide hub service (starts on login, stops on logout, auto-reconnect), a shared state service (conversations, unread total) and a REST service
- [x] `messages/` page: conversation list, name search to start a chat, thread, plain-text rendering, mark as read when the thread is open and the tab is visible
- [x] `shared/ticks/`: grey single tick, grey double tick, blue double tick, with accessible labels; new `--tick-read` colour token
- [x] **Messages** tab in the top nav with the live unread badge; toast when a message arrives and that conversation is not open
- [x] Specs for the ticks, unread and ordering logic, and the badge

> Done. **New:** `@microsoft/signalr` 8 (loaded on demand after sign-in, so it is not in the initial bundle); `core/services/direct-chat.service.ts` (REST), `direct-hub.service.ts` (one app-wide connection, auto-reconnect, retry when the first attempt fails; the JWT is read through a function each time), `direct-state.service.ts` (conversations, threads, unread total, toasts, mark-read; started and stopped by `AppComponent` with the sign-in state); `shared/ticks/` (one grey tick = sent, two grey = delivered, two blue = read, with an accessible label; new token `--tick-read`, light `#0284c7` and dark `#38bdf8`); `messages/` page at `/messages` (selected conversation in `?c=<id>`, name search with a 250 ms debounce, plain-text bubbles with a soft accent tint for your own so the ticks stay readable, "Show earlier messages" paging, one pane at a time below 48rem). The top bar gained a **Messages** tab with a live unread badge. **Behaviour:** a message for a conversation you are not looking at counts as unread, moves to the top and shows a toast ("Name: preview"); in the open, visible conversation it is marked read at once with no toast; the badge works on every page; on reconnect and when the tab becomes visible again the list and open thread are refetched, which also covers a sender's other tabs. **Tests:** 75 new Angular specs (131 total); a real two-browser run on the Docker stack (Alice and Bob, real WebSockets through nginx) passed all 17 checks, and a regression run of the older features passed (see `TESTING.md` section 10). A unit test caught one real bug on the way (a duplicate delivery showed a second toast), now fixed. Not built yet: typing indicator, online/last seen and delete message (Phase 13). Done ahead of the Roadmap's order at the user's request; the Roadmap's "next up" is still T2.

#### Phase 13 — Typing, online / last seen, delete — **done**
- [x] Hub `Typing` call (membership-checked, sent to the other person only), `PeerTyping` and `PeerPresenceChanged` events
- [x] `DELETE` endpoint: sender only, content cleared on the server, `MessageDeleted` pushed
- [x] UI: "typing…" line, online dot or "last seen …", delete action on your own messages, "This message was deleted" placeholder
- [x] Tests for typing, delete rules and presence events

> Done. **Typing:** the hub method `Typing(conversationId)` (membership-checked; non-participants, unknown ids and anonymous connections are silently ignored) pushes `PeerTyping { conversationId, userId }` to the other participant only. The browser shows italic "typing…" in the thread header and the list preview, clears it after 4 quiet seconds, and sends at most one signal every 3 seconds per conversation (and none for an empty box). **Online / last seen:** the hub pushes `PeerPresenceChanged { userId, online, lastSeenAt }` to everyone who shares a conversation with the person, on their first connection and when their last tab closes; the UI shows a green dot, "Online", or "Last seen today at 8:48 PM" / "yesterday at …" / a date (new token `--online`). **Delete:** `DELETE /api/direct/conversations/{id}/messages/{messageId}` — sender only (`403` for the other participant, `404` for outsiders and Admins and for unknown messages), idempotent, clears the text on the server (`Content` empty, `DeletedAt` set), and pushes `MessageDeleted { conversationId, messageId }` to both people; the UI asks "Delete for everyone?" inline (Delete / Cancel), then shows "This message was deleted" with no ticks, and the other person's unread count is recounted. **Also fixed in this phase (found by its test run):** every date read from the database was sent without a `Z`, so browsers outside UTC showed all times shifted by their UTC offset after a reload (a live push was correct, so the same value showed two times); every date is now marked UTC by an EF value converter and `CreatedAt` is set by the app in UTC (no migration needed; rows written earlier by a non-UTC database server will show shifted times). **Tests:** 27 new .NET and 31 new Angular tests (296 in total), and a two-browser run on the Docker stack passed 13/13, with the Phase 12 run (17/17) and the older-features regression (11/11) repeated; see `TESTING.md` section 11. Limits: typing signals are not rate limited on the server (Phase 21), and last seen cannot be hidden. Done ahead of the Roadmap's order at the user's request; the Roadmap's "next up" is still T2.

#### Phase 14 — End-to-end check, docs, cleanup
- [ ] Docker stack rebuilt; two throwaway accounts in two browser windows; walk through every tick state, typing, presence, delete, badge and toast
- [ ] This section and `CLAUDE.md` changed from *planned* to *done*, with anything that changed during the build
- [ ] Test data deleted from the Docker stack's database and the stack stopped; the dev database stays untouched unless asked

### Post-plan — Message brokers: Redis + RabbitMQ (Phases 15–20) — **planned, not started**

> Status: **planned.** No message broker exists today: there is no RabbitMQ, Redis or Kafka in the code or in `docker-compose.yml`. Everything is a direct call (the API waits for Python for up to 60 s, and password-reset email is sent inline). Nothing in this section is built; the checkboxes are all open. This work builds on the user-to-user chat plan above and starts **after Phase 11** (SignalR must exist first).

**Why both:** Redis holds fast, shared, short-lived state and pub/sub (the SignalR backplane, presence). RabbitMQ holds durable work and events (jobs, retries, dead letters, fan-out).

**What each is used for**

| Purpose | Broker | Result |
|---|---|---|
| SignalR scale-out and presence | Redis | Chat pushes reach users on any API instance, and presence survives restarts, so the API may run more than one replica |
| Async AI replies | RabbitMQ | The send request returns at once; a consumer calls Python and the reply arrives over SignalR |
| Async emails (password reset) | RabbitMQ | A slow mail server no longer slows or fails the request; retries and a dead-letter queue |
| Direct-chat event fan-out | RabbitMQ | Message events (sent, delivered, read, deleted) are published to a topic exchange for any future subscriber |

**Key design points**
- **Python does not change.** It stays stateless: no database, no broker, no user concept. A .NET consumer calls `POST /generate` exactly as today and saves the assistant message and usage log.
- **Async AI replies are behind a flag**, `Chat:AsyncReplies`, off by default. With it on, `POST /api/chat/sessions/{id}/messages` returns `202` `{ userMessage, replyStatus: "pending", jobId }`, and the answer arrives as the SignalR event `AssistantReplied` (or `AssistantFailed`). This changes the `.NET → Angular` send-message contract, so both sides change together.
- **Idempotent jobs:** an `AiReplyJobs` table (id, session id, user message id, status, attempts, error, timestamps) makes a redelivered job a no-op once it is done, and lets a reload show a pending or failed reply.
- **Retries and failure:** 2 retries with backoff, then a dead-letter queue, then the job is marked failed and the UI offers "Send again". Consumer prefetch is limited (about 4) to protect the LLM provider's rate limits. If the broker is unreachable when publishing, the API returns `503` and the user message stays saved, as it does today.
- **Timeouts stay nested:** the consumer's call to Python keeps the existing budget (Python about 50 s worst case, inside .NET's 60 s).
- **Emails:** `forgot-password` publishes only when the user exists and always returns the same generic message. The queued message holds a raw reset link, so the broker is internal-only with credentials and the message TTL equals the token's 1-hour expiry.
- **Event fan-out uses a transactional outbox** (`OutboxMessages` table written in the same transaction as the change), so an event is never lost or invented. **Events carry ids and status only, never message text.** Version 1 ships one subscriber (a structured log/metrics consumer) to prove the fan-out.
- **Library:** MassTransit 8.x, pinned. The first task of Phase 15 is to verify its current licence terms and that its outbox works with MySQL (Pomelo). Fallback: `RabbitMQ.Client` with a small wrapper and a hand-written outbox.
- **Tests never touch a real broker:** the code depends on `IAiReplyQueue`, `IEmailQueue` and `IDirectEventPublisher` (mocked, like `IPythonAiClient`), and consumers are tested with the library's in-memory harness.
- **Infra:** `redis` and `rabbitmq` services in `docker-compose.yml` with healthchecks; passwords come from the gitignored `docker.env`. New config keys: `RabbitMq:Host/Username/Password` and `Redis:ConnectionString` (secrets, same layering as `Jwt:Key`). `/health` stays liveness-only (no broker check), for the same reason it has no database check.

**Planned schema additions** (migrations are generated, never applied to the dev database without asking)
```sql
AiReplyJobs (id GUID, session_id, user_message_id, status ENUM('pending','done','failed'), attempts, error NULL, created_at, updated_at)
OutboxMessages (id, type, payload, created_at, dispatched_at NULL)   -- exact shape follows the chosen library's outbox
```

**Order:** 15, then 17 (lowest risk), 16, 18, 19, then 20. Phases 16, 18 and 19 need SignalR (Phase 11).

#### Phase 15 — Broker foundation *(no behaviour change)*
- [ ] Verify MassTransit's licence terms and MySQL outbox support; pick the library
- [ ] `redis` and `rabbitmq` in `docker-compose.yml` with healthchecks; passwords in `docker.env` generation and `docker.env.example`
- [ ] Config keys and registration behind configuration; the app still works with the brokers switched off
- [ ] README setup steps updated

#### Phase 16 — Redis: SignalR backplane and presence
- [ ] SignalR Redis backplane; `RedisPresenceTracker` (per-user connection set with a TTL and heartbeat); in-memory fallback when Redis is not configured
- [ ] Tests; two API replicas in Docker prove a push crosses instances

#### Phase 17 — RabbitMQ: async emails
- [ ] `IEmailQueue`, `email.send` queue, consumer calling `IEmailSender`, retries, dead-letter queue, message TTL
- [ ] `ForgotPassword` publishes only for existing users; same generic response either way
- [ ] Tests: generic response, retries, dead-lettering

#### Phase 18 — RabbitMQ: async AI replies
- [ ] `AiReplyJobs` migration, flag `Chat:AsyncReplies`, `202` contract, consumer, idempotency, retries, dead-letter queue, SignalR `AssistantReplied` / `AssistantFailed`
- [ ] Angular: pending, replied and failed states; specs and screenshots with the API faked
- [ ] Tests: duplicate delivery is a no-op, failure then retry, broker down returns `503`

#### Phase 19 — RabbitMQ: direct-chat event fan-out
- [ ] `OutboxMessages` migration, dispatcher, `direct.events` topic exchange, log/metrics consumer
- [ ] Tests: the event is written with the change, and the payload never contains message text

#### Phase 20 — End-to-end check, docs, cleanup
- [ ] Docker stack with two API replicas: stop RabbitMQ mid-reply, stop Redis, inspect a dead-letter queue, confirm presence across replicas
- [ ] This section and `CLAUDE.md` changed from *planned* to *done*
- [ ] Docker test data deleted and the stack stopped; the dev database stays untouched unless asked

### Post-plan — Test plan (Phases T1–T5)

> The full test strategy and test-case catalogue live in **[`TESTING.md`](TESTING.md)**. Today there are 296 automated tests (122 .NET, 12 Python, 162 Angular), all passing, and a manual smoke checklist for end-to-end checks. There is no browser E2E suite and no CI. The phases below close the gaps found in `TESTING.md` section 6. They are named T1–T5 so they don't collide with the feature phases; each is built and reviewed on its own.

#### Phase T1 — Test plan documented
- [x] `TESTING.md`: strategy, safety rules, how to run, test-case catalogue (existing tests by name, gaps and future-feature cases as *Planned*), manual smoke checklist, exit criteria
- [x] Links from this README and `CLAUDE.md`

#### Phase T2 — .NET gap tests
- [ ] `AdminControllerTests` (page-size cap of 100, date filter, `from` > `to` is `400`, joined fields)
- [ ] `TokenServiceTests` (claims, expiry, signature)
- [ ] `PythonAiClientTests` with a stubbed `HttpMessageHandler` (snake_case body, `X-Internal-Key`, timeout and non-success become `PythonServiceUnavailableException`)
- [ ] `ValidatorTests` (register, login, reset-password, send-message edge cases)
- [ ] `ExceptionHandlingMiddlewareTests` (`500` in the standard shape, no stack trace)
- [ ] `ErrorShapeIntegrationTests` with `WebApplicationFactory` (adds the test-only package `Microsoft.AspNetCore.Mvc.Testing`): `401`, `403`, `404`, `/health`
- [ ] `SmtpEmailSender` fail-fast test; history-window test in `ChatControllerTests`

#### Phase T3 — Python gap tests
- [ ] Malformed or missing body fields return `422`
- [ ] `history` with an invalid role is rejected

#### Phase T4 — Angular gap specs
- [ ] `authGuard` / `adminGuard`, `jwt.ts`, `extractErrorMessage`, `AppTitleStrategy`
- [ ] `ChatService` and `AdminService` (URLs, params, bodies)
- [ ] More `ChatComponent` flows (send success, `503` failed state, retry, title refresh) and `UsageComponent` (paging, filter, "Not tracked")
- [ ] Login and register error mapping on the page

#### Phase T5 — Close-out
- [ ] `TESTING.md` statuses changed from *Planned* to real test names; before/after counts recorded
- [ ] Mutation spot-check: break one rule (for example the 100 page-size cap, or the `401` body), confirm the matching test fails, revert
- [ ] Full run of all three suites

New feature phases (9–20) add their own rows to `TESTING.md` section 7 as they are built.

### Post-plan — Hardening and product gaps (Phases 21–28) — **planned, not started**

> Status: **planned.** This section comes from a gap analysis of the finished app (Phases 1–8 and the post-plan work). Every gap below was checked against the code; none of the fixes exist yet and all checkboxes are open. Each phase is built, tested and reviewed on its own. Migrations are generated but never applied to the host dev database without asking.

**What is missing today**

| Area | Gap (verified in the code) | Why it matters | Phase |
|---|---|---|---|
| Abuse protection | No rate limiting on the API; no account lockout; no per-user chat quota | Login brute force; anyone can burn LLM credit | 21 |
| Real client IP | No forwarded-headers config behind nginx; `UseHttpsRedirection` warns "Failed to determine the https port" in containers | An IP-based rate limit would only ever see nginx's address | 21 |
| Security headers | None in `nginx.conf` (no CSP, `nosniff`, frame-ancestors, referrer policy); no HSTS; nginx version shown | Standard browser hardening is absent | 21 |
| Account management | No change password, edit profile, delete account or email verification | Users can't fix or remove their own account; anyone can register someone else's address | 22 |
| Registration enumeration | `POST /api/auth/register` returns `409 email_taken`, while forgot-password is deliberately generic | Inconsistent: registered emails can be discovered | 22 |
| Tokens | One 60-minute JWT in `localStorage`; no refresh token; no real logout; a stolen or old token stays valid until it expires | Hard to revoke; hourly forced re-login; readable by any XSS | 23 |
| Chat management | No rename, delete or search; the API has no PUT, PATCH or DELETE endpoints | Chats pile up forever | 24 |
| Unbounded lists | List-sessions and get-messages return every row (no `Take` / `Skip`) | Slow with long chats or many chats | 24 |
| Streaming | Replies appear only when complete ("Thinking…" until then); no Stop or Regenerate | Slow-feeling UX | 25 |
| LLM context | History is the last 20 messages, up to 8000 characters each, not a token budget | Long chats can exceed the model's context and come back as `502` | 25 |
| Cost | `CostEstimate` stays `0` and the usage page says "Not tracked"; usage totals cover the current page only | No view of spend | 26 |
| Admin | No user management; becoming an Admin means raw SQL | Operationally awkward and error-prone | 26 |
| Operations | No `/health/ready`, request or correlation ids, metrics, CI, backup procedure or retention policy for `UsageLogs`; no data export | Hard to run, monitor and recover in production | 27 |
| Compliance | No privacy page, data export or delete-my-data flow | Needed once real users sign up | 22, 27 |
| UX | The wildcard route silently redirects to `/` (no 404 page); no automated accessibility check; no i18n | Polish and inclusivity | 28 |
| Appointment booking | Only a "coming soon" tab is planned (Phase 9) | Real booking (doctors, slots, bookings) needs its **own plan**, not yet written | — |

Already fine: Swagger is development-only, emails are trimmed and lower-cased, the password minimum is 8, and `/health` is liveness-only on purpose.

**Order:** see the **Roadmap** above. Phases 21–28 now come *before* the chat and broker phases (9–20), and test phase T2 comes first because Phase 21's tests need its `WebApplicationFactory` setup.

**Dependencies and interactions to remember**
- **Phase 21 and Redis (Phase 16):** start with an in-memory rate limiter; switch the counters to Redis once the API runs more than one replica.
- **Phase 23 and the SignalR hub (Phase 11):** the hub authenticates with the `access_token` query string. Phase 23 is built first, so Phase 11 must use the refreshed token (`accessTokenFactory`) and keep an open connection working across a refresh.
- **Phase 22 and direct messages (Phase 10):** delete-account is built first, so Phase 10 must extend it to remove or anonymise a user's direct conversations and messages.
- **Phase 21 and the hub (Phase 11):** the Content-Security-Policy added in Phase 21 must allow the `/hubs/` WebSocket (`connect-src` for `ws:`/`wss:`), and Phase 26's disabled users must be refused by the hub.
- **Phase 24:** delete is a **soft delete** (`DeletedAt`), so `UsageLogs` history is kept.
- **Phase 25 and Phase 18:** streaming and async replies through RabbitMQ are alternative designs for the same slow-reply problem. Streaming first is recommended; revisit Phase 18 afterwards (it would have to stream through SignalR).
- **Phase 26 changes a contract:** Python returns prompt and completion tokens and the model, and pricing comes from configuration. README and CLAUDE.md are updated when it is built.
- **Decisions to confirm at the start of a phase** (not made yet): registration enumeration (keep `409` or return a generic message, Phase 22); whether email verification is required (Phase 22); access token in `localStorage` or in memory with the refresh token in an httpOnly cookie (Phase 23); where the first Admin comes from (Phase 26).

**Ideas not planned yet** (no phase; each needs a decision and its own plan before work starts): file or image upload in chats, a per-chat system prompt or persona, background or push notifications beyond the in-app toast (Phase 12), browser end-to-end tests with Playwright (end-to-end checks are a manual checklist in `TESTING.md` for now), and real doctor-appointment booking (doctors, slots, bookings).

#### Phase 21 — Security hardening *(backend + nginx)*
- [ ] API rate limiting: per-IP on login, register and forgot-password; a per-user chat quota; `429` in the standard error shape
- [ ] Forwarded-headers and trusted-proxy configuration so the real client IP is used; fix the HTTPS-redirect warning in containers
- [ ] Account lockout after repeated failed logins (with a clear message and a timed unlock)
- [ ] nginx security headers (CSP, `X-Content-Type-Options`, frame-ancestors, referrer policy, `server_tokens off`); HSTS in production
- [ ] Tests with `WebApplicationFactory` (needs test phase T2)

#### Phase 22 — Account management *(backend + frontend)*
- [ ] Change password (requires the current password), edit profile name, delete account with confirmation
- [ ] Email verification (optional, behind a setting); decide the registration-enumeration behaviour
- [ ] Angular profile page; privacy note
- [ ] Tests: wrong current password, delete removes the user's data, enumeration behaviour as decided

#### Phase 23 — Sessions and tokens *(backend + frontend)*
- [ ] Short-lived access token (about 15 minutes) and a rotating refresh token (`RefreshTokens` table, only a hash stored)
- [ ] Real logout endpoint that revokes the refresh token; tokens invalid after a password change
- [ ] Token storage decision applied; the Angular interceptor refreshes once and retries on `401`
- [ ] Tests: rotation, reuse of an old refresh token is rejected, revoke on logout and password change

#### Phase 24 — Chat management and pagination *(backend + frontend)*
- [ ] `PATCH` rename and `DELETE` (soft delete via `DeletedAt`) for sessions; `GET` search by title
- [ ] Paged sessions and cursor-paged messages (`?before=&limit=`); Angular infinite scroll and a confirm-delete dialog
- [ ] Tests: ownership `404`, deleted chats hidden but usage logs kept, paging bounds

#### Phase 25 — Streaming replies and LLM robustness *(Python + backend + frontend)*
- [ ] Python `POST /generate/stream` (server-sent events); .NET proxies it as `text/event-stream` and saves the final message
- [ ] Angular renders the reply as it arrives, with Stop and Regenerate
- [ ] Token-aware history trimming in place of the fixed 20-message window
- [ ] Keep the nested-timeouts rule and "a failure returns `503` and the user message stays saved"
- [ ] Tests: stream happy path, abort mid-stream, upstream error mid-stream, trimming keeps the newest messages within budget

#### Phase 26 — Cost tracking and admin user management *(Python + backend + frontend)*
- [ ] Python returns prompt and completion tokens and the model; pricing from configuration; `UsageLogs.CostEstimate` filled in
- [ ] Usage page shows cost; server-side aggregates per user and per day; CSV export
- [ ] Admin users page: list, change role, disable user (`DisabledAt`; login and existing tokens rejected); a first-Admin bootstrap setting replaces the raw-SQL steps
- [ ] Tests: cost maths, aggregates, disabled users cannot log in or use an old token, admins cannot read direct messages

#### Phase 27 — Operations and compliance
- [ ] `/health/ready` (database, optionally Python); request and correlation ids in logs; a metrics endpoint (OpenTelemetry or Prometheus)
- [ ] Test-only CI workflow (tests on pull requests)
- [ ] MySQL backup and restore script plus a runbook; configurable retention for `UsageLogs`; user data export
- [ ] Production deployment guide (HTTPS termination, secrets, CORS)

#### Phase 28 — UX and accessibility *(frontend)*
- [ ] A real 404 page instead of the silent redirect
- [ ] Automated accessibility check (axe-core) plus a manual keyboard and screen-reader pass
- [ ] Optional i18n scaffold; edit-and-resend a message if Phase 25 didn't cover it

---

## 3. Suggested pacing

| Milestone | Phases | Rough target |
|---|---|---|
| Backend + AI plumbing (Postman-testable) | 1–4 | Week 1 |
| Angular UI | 5–6 | Week 2 |
| Admin + polish | 7–8 | Week 3 |
| User-to-user chat (planned) | 9–14 | one phase at a time, after Phases 21–28 (see the Roadmap) |
| Message brokers: Redis + RabbitMQ (planned) | 15–20 | after the chat phases (needs Phase 11 SignalR) |
| Test plan and gap tests (T1 done, T2–T5 planned) | T1–T5 | T2 first; T3–T5 last (see the Roadmap) |
| Hardening and product gaps (planned) | 21–28 | right after T2, before the chat phases |

---

## 4. Next steps after this app

Once this foundation is solid, reuse the same architecture for:
- AI Document Q&A (RAG-based knowledge base)
- AI Support Ticket System
- AI Resume Screener / Job Matcher
- AI Meeting Notes Summarizer (planned in detail separately)

---

*Plan generated September 9, 2026.*
