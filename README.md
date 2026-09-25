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

### Forgot password

Works end to end, but no real email provider is wired up - the "email" is logged to the .NET
console/log instead of actually sent (`ConsoleEmailSender`; see CLAUDE.md's Configuration section
for how to swap in a real provider). To test it yourself: click "Forgot password?" on the login
page, then find the reset link in the terminal running `dotnet run` (search for `DEV EMAIL`):

```bash
grep "DEV EMAIL" /path/to/your/dotnet-run-output.log   # or just watch the terminal
```

Copy the `Link:` value into your browser to actually reset the password.

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
| Infra (later) | Docker Compose |

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

---

## 3. Suggested pacing

| Milestone | Phases | Rough target |
|---|---|---|
| Backend + AI plumbing (Postman-testable) | 1–4 | Week 1 |
| Angular UI | 5–6 | Week 2 |
| Admin + polish | 7–8 | Week 3 |

---

## 4. Next steps after this app

Once this foundation is solid, reuse the same architecture for:
- AI Document Q&A (RAG-based knowledge base)
- AI Support Ticket System
- AI Resume Screener / Job Matcher
- AI Meeting Notes Summarizer (planned in detail separately)

---

*Plan generated September 9, 2026.*
