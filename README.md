# AI Chat Assistant — Project Plan

**Start date:** September 9, 2026
**Stack:** Angular · .NET 8 Web API · Python (FastAPI) · MySQL 8

---

## 1. Overview

A moderate-depth chat application that integrates an LLM (OpenAI/Anthropic) through a split backend: a .NET Web API for auth, business logic, and orchestration, and a Python FastAPI microservice dedicated to LLM calls. This is the foundation app — later projects (RAG document Q&A, ticket system, resume screener, meeting summarizer) reuse this same skeleton.

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
| AI service | Python 3.11+, FastAPI, OpenAI/Anthropic SDK, Uvicorn |
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

- [ ] Install/run MySQL (local or Docker `mysql:8`), create `ai_chat_assistant` database
- [ ] `dotnet new sln -n AiChatAssistant` + `dotnet new webapi -n AiChatAssistant.Api`
- [ ] Add packages: Pomelo.EntityFrameworkCore.MySql, EF Core Design, JwtBearer, FluentValidation.AspNetCore, Serilog.AspNetCore
- [ ] Define entities: `User`, `Role`, `UserRole`, `ChatSession`, `Message`, `UsageLog`
- [ ] Create `AppDbContext`, configure relationships/FKs
- [ ] `dotnet ef migrations add InitialCreate` → `dotnet ef database update`
- [ ] Verify tables in MySQL Workbench/CLI, seed test rows

**Exit criteria:** Tables exist matching schema; migrations run cleanly on a fresh DB.

---

### Phase 2 — Authentication (.NET only)
**Goal:** Working register/login/JWT, testable via Postman.

- [ ] `POST /api/auth/register` — hash password (BCrypt.Net-Next), create user, assign default `User` role
- [ ] `POST /api/auth/login` — verify password, issue JWT (claims: sub, email, role)
- [ ] Configure JWT middleware in `Program.cs`
- [ ] `GET /api/auth/me` — protected test endpoint returning claims
- [ ] `[Authorize(Roles = "Admin")]` placeholder admin endpoint
- [ ] FluentValidation for register/login DTOs
- [ ] Global exception middleware → consistent `{ error: { code, message } }` shape

**Exit criteria:** Register → login → call `/me` with token succeeds; no token → 401.

---

### Phase 3 — Python LLM service (standalone)
**Goal:** FastAPI service returning LLM replies, testable without .NET.

- [ ] `pip install fastapi uvicorn openai python-dotenv` (or `anthropic`)
- [ ] `.env` with `OPENAI_API_KEY`/`ANTHROPIC_API_KEY` + `INTERNAL_API_KEY`
- [ ] `POST /generate` — request/response models, builds message list, calls LLM
- [ ] `X-Internal-Key` header check dependency (reject unauthorized calls)
- [ ] Error handling — timeouts/rate limits return clean 502/503, never crash
- [ ] (Optional) `/generate/stream` via SSE for streaming responses

**Exit criteria:** `curl -X POST /generate` with valid key returns a real LLM reply + token count.

---

### Phase 4 — Wire .NET ↔ Python ↔ MySQL
**Goal:** End-to-end message flow: persist → call LLM → persist reply → log usage.

- [ ] Register typed `HttpClient` for Python service in `Program.cs`
- [ ] `IPythonAiClient.GenerateAsync(...)` implementation
- [ ] `POST /api/chat/sessions` — create session for authenticated user
- [ ] `POST /api/chat/sessions/{id}/messages`:
  - [ ] Validate session ownership (or Admin)
  - [ ] Save user message
  - [ ] Fetch recent history
  - [ ] Call Python service
  - [ ] Save assistant message + usage log
  - [ ] Handle Python failure gracefully (503, not 500)
- [ ] `GET /api/chat/sessions` and `GET /api/chat/sessions/{id}/messages`

**Exit criteria:** Full round trip in Postman produces a real AI reply and correct DB rows.

---

### Phase 5 — Angular: auth
**Goal:** Login/register UI wired to real API.

- [ ] `ng new ai-chat-assistant-ui --routing --style=scss`
- [ ] `AuthService`: register/login/logout, JWT storage
- [ ] Reactive forms with validation
- [ ] `authInterceptor` — attaches Bearer token
- [ ] `authGuard` and `adminGuard`
- [ ] Decode JWT client-side for UI conditionals (not for real authorization)

**Exit criteria:** Register/login works; protected routes redirect correctly; session persists on refresh.

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
