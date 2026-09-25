# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Current state

Follow the phase-by-phase plan in `README.md` (Phases 1–8). Progress:

- **Phase 1 (Database & data layer) — done.** `.NET 8` Web API scaffolded at `backend-dotnet/` (pinned via `global.json`), EF Core entities + `AppDbContext` in `src/AiChatAssistant.Api/`, `InitialCreate` migration applied to the local MySQL `AiChatAssistant` database, `Admin`/`User` roles seeded. `dotnet-ef` is a local tool (`dotnet tool restore` after clone).
- **Phase 2 (Authentication) — done.** `AuthController` (`POST /api/auth/register`, `POST /api/auth/login`, `GET /api/auth/me`), JWT issuance via `ITokenService`/`TokenService`, BCrypt.Net-Next password hashing, FluentValidation on register/login DTOs, `ExceptionHandlingMiddleware` for the `{ error: { code, message } }` shape, `AdminController` as an `[Authorize(Roles = "Admin")]` placeholder. `Jwt:Key` is stored via `dotnet user-secrets` (run `dotnet user-secrets set "Jwt:Key" "<value>"` inside `src/AiChatAssistant.Api/` after clone) — `Jwt:Issuer`/`Jwt:Audience`/`Jwt:ExpiryMinutes` live in `appsettings.json`.
- **Phase 5 (Angular: auth) — done, out of order.** Phases 3–4 (Python service, chat wiring) were skipped for now since Phase 5 only depends on Phase 2's auth endpoints; picking those up later still works, nothing here assumes they exist. `frontend/` is an Angular 19 app (project name `ai-chat-assistant-ui`, CLI pinned to `@angular/cli@19` — this machine's Node predates what Angular 20+'s CLI requires), standalone components, `src/app/{auth,core,home,admin}/` (`home/` was a throwaway placeholder — replaced by `chat/` in Phase 6, see below). `AuthService` (signals, `localStorage`-backed session), `authInterceptor`, `authGuard`/`adminGuard`, JWT decoding in `core/utils/jwt.ts`. `ping.component.ts` was a placeholder standing in for Phase 7's real admin page — replaced by `admin/usage/` in Phase 7, see below. Backend gained a `Cors` policy (`Program.cs`, `appsettings.json` `Cors:AllowedOrigins`, default `http://localhost:4200`) so the browser can call the API at all.
- **Phase 3 (Python LLM service) — done.** `ai-service-python/` (FastAPI, Python 3.12, venv at `.venv/`). LLM provider is **DeepInfra** (`https://api.deepinfra.com/v1/openai`, OpenAI-compatible, used via the plain `openai` SDK), model `meta-llama/Meta-Llama-3.1-8B-Instruct-Turbo` (small/cheap, chosen deliberately for a demo — swap `DEEPINFRA_MODEL` in `.env` for anything else in DeepInfra's catalog). `POST /generate` (`app/routes/generate.py`) is gated by `X-Internal-Key` (`app/auth/api_key.py`); `app/services/llm_client.py` maps timeouts/rate-limits to `503` and any other upstream/unexpected error to `502` — never a bare `500`. Secrets live in `ai-service-python/.env` (gitignored; template at `env.example`).
- **Phase 4 (wire .NET ↔ Python ↔ MySQL) — done.** `ChatController` (`POST`/`GET /api/chat/sessions`, `GET`/`POST /api/chat/sessions/{id}/messages`), all `[Authorize]`, ownership enforced via `GetOwnedSessionAsync` (404, not 403, for a session that isn't the caller's — same for User and Admin, Admin just always passes the check). `IPythonAiClient`/`PythonAiClient` is a typed `HttpClient` (`PythonService:BaseUrl` in `appsettings.json`, `PythonService:InternalApiKey` in user-secrets — **must** match `ai-service-python`'s `INTERNAL_API_KEY` exactly, regenerate both together if one changes). Send-message flow: save user message and commit immediately, fetch last 20 prior messages as history, call Python, on `PythonServiceUnavailableException` return `503` (verified live by killing the Python process mid-request — user message stayed persisted); on success save the assistant message + a `UsageLog` row (`CostEstimate` left at its `0` default — .NET deliberately doesn't know Python's model/pricing, that split is architectural, not an oversight).
- **Phase 6 (Angular: chat UI) — done.** `ChatComponent` (`src/app/chat/`) replaces Phase 5's `HomeComponent` placeholder at the root route. `ChatService` (`core/services/chat.service.ts`) wraps the four `/api/chat/*` endpoints. Session selection is a **query param** (`?session=<id>`), not a path segment — two path-based routes pointing at the same lazy component looked equivalent but weren't (Angular's default route reuse strategy destroys/recreates the component between them, orphaning an in-flight subscription); see `app.routes.ts`'s comment. `selectedSessionId` uses `distinctUntilChanged()` (a bare `toSignal(queryParamMap...)` re-emits on every new `ParamMap` object even when the value hasn't changed). `messages` is cleared synchronously at the `send()`/`newChat()` call site, not inside the route-change `effect()` — that effect's own scheduling has no guaranteed ordering against `router.navigate()`'s promise. All three of these were real bugs caught by live browser testing (puppeteer-core against the local Chrome), not review.
- **Phase 7 (Admin usage view) — done.** `AdminController.GetUsage` (`GET /api/admin/usage`, `[Authorize(Roles = "Admin")]`) — paginated (`page`/`pageSize`, capped at 100), `from`/`to` date filter (`400` if `from` > `to`), joins `User`/`ChatSession` for display fields. Angular: `admin/usage/usage.component.ts` (`src/app/admin/`) replaces Phase 2's `ping.component.ts` placeholder — table, date filter, pager, and an optional tokens-per-day CSS bar chart scoped to the current page only (not a separate aggregate query). The backend `GET /api/admin/ping` route itself is kept (harmless, still a minimal RBAC smoke test) — only its frontend placeholder page was replaced. `ChatComponent`'s sidebar links to it as "Usage logs", shown only when `auth.hasRole('Admin')`.
- **Phase 8 (Polish & hardening) — done.** Error-shape audit found and fixed a real gap: `[Authorize]` rejections and unmatched routes previously fell through to ASP.NET Core's bare, empty-body 401/403/404, bypassing the `{ error: { code, message } }` rule entirely - fixed via `JwtBearerEvents.OnChallenge`/`OnForbidden` and `app.MapFallback` in `Program.cs`, all writing through `ErrorResponse.WriteAsync` (the shared helper `ExceptionHandlingMiddleware` was refactored to use too). Angular gained `core/error.interceptor.ts`: a global `LoadingService` (in-flight-request counter, drives a top loading bar in `AppComponent`) and `ToastService`, layered on top of (never replacing) each component's own error handling - it toasts only truly unexpected failures (network-unreachable, uncaught `500`/`502`) and auto-logs-out + redirects to `/login` on error code `"unauthorized"` (a stale/invalid token), leaving everything already well-handled inline (chat's `503`, login's `401`, validation `400`s) alone to avoid double notifications. Verified live with a *real* expired token (temporary `Jwt__ExpiryMinutes=1` env var override, not a simulated 401). Test suites added for the first time: `backend-dotnet/tests/AiChatAssistant.Api.Tests/` (xUnit + Moq + EF Core InMemory - `ChatControllerTests` mocks `IPythonAiClient` per plan, `AuthControllerTests` mocks `ITokenService`, both use the real FluentValidation validators), `ai-service-python/tests/` (pytest, monkeypatches `app.services.llm_client`'s module-level OpenAI client - never a real DeepInfra call; needs `pytest.ini`'s `pythonpath = .` since `app/` and `tests/` are siblings, not a `src` layout), `frontend/src/app/**/*.spec.ts` gained `AuthService`, `authInterceptor`, and `errorInterceptor` specs (a fake-but-correctly-shaped JWT is required for any AuthService-touching test - a plain placeholder string fails AuthService's own expiry check and gets self-cleared, see the `fakeJwt()` helpers). README gained a "Getting started" section (0.) with real, verified setup steps - see below.
- **Post-plan: Forgot/reset password.** Not part of the original 8-phase plan - added after the fact, on request. `POST /api/auth/forgot-password` (always returns the same generic message whether or not the email is registered - no user enumeration) and `POST /api/auth/reset-password`. `PasswordResetToken` (migration `AddPasswordResetTokens`) stores only a SHA-256 hash of the raw token, 1-hour expiry, single-use (`UsedAt`), and requesting a new one invalidates any earlier unused one for that user. `IEmailSender` is the swap point for the provider (`Program.cs`'s DI registration). Angular: `auth/forgot-password/` and `auth/reset-password/` (the latter reads `token` from a query param, once, via `route.snapshot` - not reactively like `ChatComponent`'s `?session=`, since this page is only ever reached via a one-time emailed link). Verified live end to end, twice: once against `ConsoleEmailSender` (registered → logged out → requested a reset → pulled the link out of the dev log → reset the password → confirmed the *old* password now fails login and the *new* one works, plus the invalid/no-token/already-used-token error states), then again after swapping in real SMTP (below) - confirmed an actual email lands in a real Gmail inbox.
- **Post-plan: Real SMTP email.** `SmtpEmailSender` (MailKit, `Services/SmtpEmailSender.cs`) is now the registered `IEmailSender` in `Program.cs`, replacing `ConsoleEmailSender` (which still exists - swap the DI line back to it for email-free local dev). `Smtp:Host`/`Port`/`FromName` default to Gmail (`appsettings.json`); `Smtp:Username`/`Smtp:Password` are required secrets, fail-fast at send time if missing, same pattern as `Jwt:Key`. For Gmail specifically, `Username` is the full address and `Password` is a 16-character **App Password** (`myaccount.google.com/apppasswords`, requires 2-Step Verification on that account first) - not the account's real login password. Hit and fixed one real bug during live verification: `MailKit.Security.SslHandshakeException` on connect - .NET's TLS revocation check (OCSP) couldn't complete on this network even though Gmail's certificate was genuinely valid, so `SmtpClient.CheckCertificateRevocation = false` is set explicitly (see the doc comment on why that's safe here - the host is pinned to `smtp.gmail.com`, not arbitrary).

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
- Error responses from `.NET` use a consistent shape: `{ "error": { "code", "message" } }` - always, including unhandled exceptions (`ExceptionHandlingMiddleware`), missing/invalid-token `401`s and wrong-role `403`s (`JwtBearerEvents.OnChallenge`/`OnForbidden` in `Program.cs` - these bypass MVC entirely by default, which is what made them easy to miss), and unmatched routes (`app.MapFallback`).
- JWT carries `sub`, `email`, `role`. Client-side JWT decoding is for UI conditionals only; real authorization is server-side (`[Authorize(Roles = "Admin")]`).
- Session ownership is enforced on every chat endpoint: a user may only touch their own sessions unless they are `Admin`.

## Cross-service contracts

`.NET → Python`: `POST /generate` with `{ session_id, history: [{role, content}], prompt }`
`Python → .NET`: `{ reply, tokens_used }`
`.NET → Angular` (send message): `{ userMessage, assistantMessage }`

Changing any of these requires updating both sides and the schema section of `README.md`.

## Commands

All of these are live and verified as of Phase 8 - see README.md's "Getting started" (section 0)
for first-time setup (user-secrets, `.env`, database).

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
npm test -- --watch=false --browsers=ChromeHeadless          # ng test (Karma), single run
npm test -- --include='**/auth.service.spec.ts'               # single spec
npm run build
```

**Full stack:** no `docker-compose.yml` - out of scope for this 8-phase plan (there's no later
phase to add it in). Run MySQL + the three services individually; see README.md section 0.

## Configuration

- Secrets (`DEEPINFRA_API_KEY` — project uses DeepInfra's OpenAI-compatible API rather than OpenAI/Anthropic directly, see Phase 3 — `INTERNAL_API_KEY`, DB connection string, JWT signing key) live in `.env` / user-secrets and are gitignored. Commit `.example` files alongside them (named `env.example`, not `.env.example` — a `.env*` glob is denied to this agent's Read/Write/Bash tools, so the committed template can't use that prefix).
- The `.NET` API and the Python service must agree on `INTERNAL_API_KEY`.
- Database name actually in use (per the committed `appsettings.Development.json` connection string): `AiChatAssistant` - README's original plan said `ai_chat_assistant`; this is what's really there, correcting that stale reference.
- `.NET` config layering: `appsettings.json` (shared defaults) → `appsettings.{Development,Production}.json` (per-environment, both committed) → user-secrets (Development only) → environment variables (`Section__Key` syntax, e.g. `Jwt__Key`, `ConnectionStrings__Default`) → command-line args, later wins. `appsettings.Production.json` intentionally has no `ConnectionStrings`/`Jwt` block — those must come from environment variables at deploy time, so the fail-fast checks in `Program.cs` catch a missing secret instead of silently booting unconfigured.
- `Frontend:BaseUrl` (`appsettings.json`, default `http://localhost:4200`) is where password-reset links point — update it (or override via `Frontend__BaseUrl`) if the frontend is ever deployed somewhere other than that origin.
- Real email is wired in via SMTP (`SmtpEmailSender`, MailKit), registered as `IEmailSender` in `Program.cs`. `Smtp:Host`/`Port`/`FromName` (`appsettings.json`) default to Gmail; `Smtp:Username`/`Smtp:Password` are required user-secrets (Gmail: an address + App Password, not the real account password — see the Phase-8-adjacent "Real SMTP email" entry above). `ConsoleEmailSender` (logs the link instead of sending) still exists as an email-free dev alternative — swap the one DI line in `Program.cs` back to it if you don't want to configure SMTP locally; nothing else in `AuthController` needs to change either way.

## Testing expectations

Done as of Phase 8 - keep new tests consistent with these patterns:
- **`.NET`** (`backend-dotnet/tests/AiChatAssistant.Api.Tests/`): controller tests mock `IPythonAiClient`/`ITokenService` with Moq, but use the *real* FluentValidation validators and a real (`EntityFrameworkCore.InMemory`) `AppDbContext` - only the LLM/token-issuance boundary is faked. Build a caller identity with `TestSupport/ClaimsPrincipalFactory.Create(userId, email, ...roles)`, not a hand-rolled `ClaimsPrincipal` - it shapes claims the way they actually look *after* JWT validation (`role` remapped to `ClaimTypes.Role`, etc.), not the raw pre-validation JWT shape.
- **Python** (`ai-service-python/tests/`): monkeypatch `app.services.llm_client._client.chat.completions.create` - never call DeepInfra for real. `conftest.py` sets fake `DEEPINFRA_API_KEY`/`INTERNAL_API_KEY` via `os.environ.setdefault` so tests don't need a real `.env`. `pytest.ini` sets `pythonpath = .` (required: `app/` and `tests/` are siblings).
- **Angular**: `AuthService`, `authInterceptor`, and `errorInterceptor` all have specs. Any test that stores a token in `localStorage` needs a real JWT *shape* (`header.payload.signature`, valid base64) with a real `exp` claim - `AuthService`'s constructor self-clears anything that fails its own expiry check, so a plain placeholder string silently vanishes. Reuse the `fakeJwt()` pattern from `auth.service.spec.ts`/`auth.interceptor.spec.ts`.

Do not make real LLM calls in tests.
