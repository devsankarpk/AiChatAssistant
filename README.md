# AI Chat Assistant — Project Plan

**Start date:** September 9, 2026
**Stack:** Angular · .NET 8 Web API · Python (FastAPI) · MySQL 8

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

- [ ] `ChatService`: getSessions, createSession, getMessages, sendMessage
- [ ] Sidebar: session list + "New chat"
- [ ] Chat window: message list, input box, send button
- [ ] Optimistic user message render + "thinking" indicator
- [ ] Error state with retry if send fails

**Exit criteria:** Full browser round trip — login → new chat → send → real LLM reply → persists on refresh.

---

### Phase 7 — Admin usage view
**Goal:** Role-based data access end-to-end.

- [ ] `.NET`: `GET /api/admin/usage` (Admin-only), paginated, date filter
- [ ] Angular: `admin/usage` page — table + filter, guarded route
- [ ] (Optional) simple usage-over-time chart

**Exit criteria:** `User` role blocked (guard + 403); `Admin` sees all usage data.

---

### Phase 8 — Polish & hardening

- [ ] Global HTTP error interceptor (Angular) → toasts + loading states
- [ ] Confirm consistent error shape across .NET → Angular
- [ ] Config hygiene: `.gitignore` secrets, commit `.example` config files
- [ ] Tests: .NET controller (mocked Python client), Python `/generate` (mocked LLM), Angular `AuthService`/interceptor
- [ ] Top-level README with full setup instructions

**Exit criteria:** Fresh clone + README gets a new dev to a working app.

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
