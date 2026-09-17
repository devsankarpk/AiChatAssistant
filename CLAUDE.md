# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Current state

Follow the phase-by-phase plan in `README.md` (Phases 1–8). Progress:

- **Phase 1 (Database & data layer) — done.** `.NET 8` Web API scaffolded at `backend-dotnet/` (pinned via `global.json`), EF Core entities + `AppDbContext` in `src/AiChatAssistant.Api/`, `InitialCreate` migration applied to the local MySQL `AiChatAssistant` database, `Admin`/`User` roles seeded. `dotnet-ef` is a local tool (`dotnet tool restore` after clone).
- **Phase 2 (Authentication) — done.** `AuthController` (`POST /api/auth/register`, `POST /api/auth/login`, `GET /api/auth/me`), JWT issuance via `ITokenService`/`TokenService`, BCrypt.Net-Next password hashing, FluentValidation on register/login DTOs, `ExceptionHandlingMiddleware` for the `{ error: { code, message } }` shape, `AdminController` as an `[Authorize(Roles = "Admin")]` placeholder. `Jwt:Key` is stored via `dotnet user-secrets` (run `dotnet user-secrets set "Jwt:Key" "<value>"` inside `src/AiChatAssistant.Api/` after clone) — `Jwt:Issuer`/`Jwt:Audience`/`Jwt:ExpiryMinutes` live in `appsettings.json`.
- **Phase 5 (Angular: auth) — done, out of order.** Phases 3–4 (Python service, chat wiring) were skipped for now since Phase 5 only depends on Phase 2's auth endpoints; picking those up later still works, nothing here assumes they exist. `frontend/` is an Angular 19 app (project name `ai-chat-assistant-ui`, CLI pinned to `@angular/cli@19` — this machine's Node predates what Angular 20+'s CLI requires), standalone components, `src/app/{auth,core,home,admin}/` (`home/` was a throwaway placeholder — replaced by `chat/` in Phase 6, see below). `AuthService` (signals, `localStorage`-backed session), `authInterceptor`, `authGuard`/`adminGuard`, JWT decoding in `core/utils/jwt.ts`. `AdminController`'s `ping.component.ts` is still a placeholder standing in for Phase 7's real admin page — expect it to be replaced, not extended. Backend gained a `Cors` policy (`Program.cs`, `appsettings.json` `Cors:AllowedOrigins`, default `http://localhost:4200`) so the browser can call the API at all.
- **Phase 3 (Python LLM service) — done.** `ai-service-python/` (FastAPI, Python 3.12, venv at `.venv/`). LLM provider is **DeepInfra** (`https://api.deepinfra.com/v1/openai`, OpenAI-compatible, used via the plain `openai` SDK), model `meta-llama/Meta-Llama-3.1-8B-Instruct-Turbo` (small/cheap, chosen deliberately for a demo — swap `DEEPINFRA_MODEL` in `.env` for anything else in DeepInfra's catalog). `POST /generate` (`app/routes/generate.py`) is gated by `X-Internal-Key` (`app/auth/api_key.py`); `app/services/llm_client.py` maps timeouts/rate-limits to `503` and any other upstream/unexpected error to `502` — never a bare `500`. Secrets live in `ai-service-python/.env` (gitignored; template at `env.example`).
- **Phase 4 (wire .NET ↔ Python ↔ MySQL) — done.** `ChatController` (`POST`/`GET /api/chat/sessions`, `GET`/`POST /api/chat/sessions/{id}/messages`), all `[Authorize]`, ownership enforced via `GetOwnedSessionAsync` (404, not 403, for a session that isn't the caller's — same for User and Admin, Admin just always passes the check). `IPythonAiClient`/`PythonAiClient` is a typed `HttpClient` (`PythonService:BaseUrl` in `appsettings.json`, `PythonService:InternalApiKey` in user-secrets — **must** match `ai-service-python`'s `INTERNAL_API_KEY` exactly, regenerate both together if one changes). Send-message flow: save user message and commit immediately, fetch last 20 prior messages as history, call Python, on `PythonServiceUnavailableException` return `503` (verified live by killing the Python process mid-request — user message stayed persisted); on success save the assistant message + a `UsageLog` row (`CostEstimate` left at its `0` default — .NET deliberately doesn't know Python's model/pricing, that split is architectural, not an oversight).
- **Phase 6 (Angular: chat UI) — done.** `ChatComponent` (`src/app/chat/`) replaces Phase 5's `HomeComponent` placeholder at the root route. `ChatService` (`core/services/chat.service.ts`) wraps the four `/api/chat/*` endpoints. Session selection is a **query param** (`?session=<id>`), not a path segment — two path-based routes pointing at the same lazy component looked equivalent but weren't (Angular's default route reuse strategy destroys/recreates the component between them, orphaning an in-flight subscription); see `app.routes.ts`'s comment. `selectedSessionId` uses `distinctUntilChanged()` (a bare `toSignal(queryParamMap...)` re-emits on every new `ParamMap` object even when the value hasn't changed). `messages` is cleared synchronously at the `send()`/`newChat()` call site, not inside the route-change `effect()` — that effect's own scheduling has no guaranteed ordering against `router.navigate()`'s promise. All three of these were real bugs caught by live browser testing (puppeteer-core against the local Chrome), not review.
- **Phases 7, 8 — not started.**

Local DB connection string lives (by project decision) in `backend-dotnet/src/AiChatAssistant.Api/appsettings.Development.json` under `ConnectionStrings:Default`, in Pomelo key=value form.

The local MySQL server reports version `26.7.0` (CalVer). `ServerVersion.AutoDetect` in `Program.cs` handles it. `DateTime` columns map to `datetime(6)`, so DB-side defaults must be `CURRENT_TIMESTAMP(6)` — plain `CURRENT_TIMESTAMP` is rejected as an invalid default.

## Architecture

Three deployables plus a database, split so that LLM access is isolated from business logic:

```
frontend/          Angular 17+ SPA — REST + JWT to the .NET API
backend-dotnet/     .NET 8 Web API — auth, roles, orchestration, persistence
ai-service-python/  FastAPI service — the only component that calls the LLM provider (DeepInfra)
MySQL 8            users, roles, chat sessions, messages, usage logs
```

Request flow for a chat message: Angular → `.NET POST /api/chat/sessions/{id}/messages` → validate session ownership → persist user message → load recent history → `.NET` calls `Python POST /generate` (typed `HttpClient`, `X-Internal-Key` header) → Python calls the LLM → `.NET` persists assistant message + a `UsageLog` row → returns `{ userMessage, assistantMessage }`.

Key rules that span components:
- The Python service **never** talks to MySQL and has no user concept. It is stateless: `.NET` sends the full history and prompt on every call.
- Python is authenticated only by a shared `INTERNAL_API_KEY` via the `X-Internal-Key` header — it must never be exposed to the browser.
- A Python failure (timeout, rate limit, upstream error) must surface to Angular as `503`, never `500`, and the user message stays persisted.
- Error responses from `.NET` use a consistent shape: `{ "error": { "code", "message" } }`, produced by global exception middleware.
- JWT carries `sub`, `email`, `role`. Client-side JWT decoding is for UI conditionals only; real authorization is server-side (`[Authorize(Roles = "Admin")]`).
- Session ownership is enforced on every chat endpoint: a user may only touch their own sessions unless they are `Admin`.

## Cross-service contracts

`.NET → Python`: `POST /generate` with `{ session_id, history: [{role, content}], prompt }`
`Python → .NET`: `{ reply, tokens_used }`
`.NET → Angular` (send message): `{ userMessage, assistantMessage }`

Changing any of these requires updating both sides and the schema section of `README.md`.

## Commands

These are the expected commands once each part is scaffolded per the plan; they will not work until then.

**backend-dotnet/** (.NET 8, EF Core, Pomelo MySQL)
```
dotnet build
dotnet run --project src/AiChatAssistant.Api
dotnet test
dotnet test --filter "FullyQualifiedName~ChatControllerTests"   # single test class
dotnet ef migrations add <Name> --project src/AiChatAssistant.Api
dotnet ef database update --project src/AiChatAssistant.Api
```

**ai-service-python/** (FastAPI, Uvicorn)
```
python -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
uvicorn app.main:app --reload --port 8000
pytest
pytest tests/test_generate.py::test_returns_reply    # single test
```

**frontend/** (Angular CLI)
```
npm install
npm start                          # ng serve
npm test                           # ng test (Karma)
npm test -- --include='**/auth.service.spec.ts'   # single spec
npm run build
```

**Full stack:** `docker-compose.yml` (added in a later phase) brings up MySQL + all three services.

## Configuration

- Secrets (`DEEPINFRA_API_KEY` — project uses DeepInfra's OpenAI-compatible API rather than OpenAI/Anthropic directly, see Phase 3 — `INTERNAL_API_KEY`, DB connection string, JWT signing key) live in `.env` / user-secrets and are gitignored. Commit `.example` files alongside them (named `env.example`, not `.env.example` — a `.env*` glob is denied to this agent's Read/Write/Bash tools, so the committed template can't use that prefix).
- The `.NET` API and the Python service must agree on `INTERNAL_API_KEY`.
- Default database name: `ai_chat_assistant`.
- `.NET` config layering: `appsettings.json` (shared defaults) → `appsettings.{Development,Production}.json` (per-environment, both committed) → user-secrets (Development only) → environment variables (`Section__Key` syntax, e.g. `Jwt__Key`, `ConnectionStrings__Default`) → command-line args, later wins. `appsettings.Production.json` intentionally has no `ConnectionStrings`/`Jwt` block — those must come from environment variables at deploy time, so the fail-fast checks in `Program.cs` catch a missing secret instead of silently booting unconfigured.

## Testing expectations

Per Phase 8: `.NET` controller tests mock `IPythonAiClient`; Python `/generate` tests mock the LLM SDK; Angular tests cover `AuthService` and the auth interceptor. Do not make real LLM calls in tests.
