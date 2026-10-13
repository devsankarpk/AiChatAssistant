# Testing

The test plan and test-case catalogue for the whole application. It is the single place to answer "what is tested, how, and what is not yet".

**Status today:** 94 automated tests (26 .NET, 12 Python, 56 Angular), all passing. There is no browser E2E suite and no CI; end-to-end checks are a manual checklist (section 8). Test gaps are listed in section 6 and are closed phase by phase (T2 to T5, see `README.md`). Cases for planned features (chat, brokers, hardening) are listed in section 7 and filled in as each phase is built.

## 1. Purpose and scope

- Show, per feature, which behaviour is verified, by which test, and which behaviour is not.
- Cover all four parts: the .NET API, the Python AI service, the Angular app, and the Docker stack.
- **Out of scope:** load/performance testing, penetration testing, and browser-automation E2E (kept manual for now; the app is small and the registration flow needs real test accounts).

## 2. Strategy

| Layer | Tool | What is real | What is faked |
|---|---|---|---|
| .NET API | xUnit, Moq, EF Core InMemory | Controllers, FluentValidation validators, EF queries | The Python client (`IPythonAiClient`), token issuance (`ITokenService`), email (`IEmailSender`) |
| Python AI service | pytest (FastAPI test client) | Routes, request validation, API-key check, error mapping | The LLM SDK call (`app.services.llm_client._client.chat.completions.create` is monkeypatched) |
| Angular app | Karma + Jasmine (ChromeHeadless) | Components, services, interceptors, guards | HTTP (`HttpTestingController`) |
| Whole stack | Manual checklist on the Docker stack | Everything, including MySQL and (optionally) the real LLM | Nothing |

## 3. Environments and safety rules

- **Never** make a real LLM call, send a real email, or hit a real broker from an automated test.
- Automated tests never touch your host MySQL. .NET tests use EF Core InMemory.
- Manual end-to-end checks run on the Docker stack (`docker compose --env-file docker.env up --build -d`), which has its own database volume. Use throwaway accounts, then delete the test rows (or `docker compose --env-file docker.env down -v`).
- Never seed rows into the host dev database to make screenshots or tests easier. For UI checks, fake the API (Puppeteer request interception) against `ng serve`.
- A test that stores a token in `localStorage` needs a real JWT *shape* (`header.payload.signature` with an `exp`), otherwise `AuthService` clears it. Reuse the `fakeJwt()` helpers.
- Build a caller identity in .NET tests with `TestSupport/ClaimsPrincipalFactory.Create(userId, email, ...roles)`.

## 4. How to run

```bash
# .NET (26 tests)
cd backend-dotnet && dotnet test
dotnet test --filter "FullyQualifiedName~ChatControllerTests"          # one class

# Python (12 tests)
cd ai-service-python && source .venv/bin/activate && pytest
pytest tests/test_generate.py::test_returns_reply                      # one test

# Angular (56 tests)
cd frontend && npm test -- --watch=false --browsers=ChromeHeadless
npm test -- --include='**/auth.service.spec.ts' --watch=false --browsers=ChromeHeadless   # one spec
```

If Angular's tests can't find Chrome, set `CHROME_BIN` (see the README's "Running the tests").

## 5. Test-case catalogue

**Status values:** `Automated` (names the test), `Manual` (a human check, section 8), `Planned (Tn)` (a gap that test phase Tn will close, see section 6), `Planned (Phase n)` (a case for a feature that is not built yet, section 7).

### 5.1 AUTH (.NET `AuthControllerTests`, Angular auth specs)

| ID | Scenario | Expected result | Layer | Status |
|---|---|---|---|---|
| AUTH-01 | Register with valid details | User created, default `User` role assigned, JWT returned | .NET | Automated: `Register_CreatesUser_AssignsDefaultUserRole_AndReturnsToken` |
| AUTH-02 | Register with an email already in use | `409` conflict | .NET | Automated: `Register_ReturnsConflict_WhenEmailAlreadyTaken` |
| AUTH-03 | Register with a weak password | `400` | .NET | Automated: `Register_Returns400_ForWeakPassword` |
| AUTH-04 | Login with the correct credentials | JWT returned | .NET | Automated: `Login_ReturnsToken_ForCorrectCredentials` |
| AUTH-05 | Login with a wrong password | `401` | .NET | Automated: `Login_ReturnsUnauthorized_ForWrongPassword` |
| AUTH-06 | Login with an unknown email | `401`, same as a wrong password | .NET | Automated: `Login_ReturnsUnauthorized_ForUnknownEmail` |
| AUTH-07 | `GET /api/auth/me` | Returns id, email and roles from the caller's claims | .NET | Automated: `Me_ReturnsIdEmailAndRoles_FromTheCallersClaims` |
| AUTH-08 | Forgot password for a known email | Reset link sent | .NET | Automated: `ForgotPassword_SendsResetLink_ForKnownEmail` |
| AUTH-09 | Forgot password for an unknown email | Same generic message, nothing sent (no user enumeration) | .NET | Automated: `ForgotPassword_ReturnsTheSameGenericMessage_ForUnknownEmail_AndSendsNothing` |
| AUTH-10 | Request a reset twice | Earlier unused token is invalidated | .NET | Automated: `ForgotPassword_InvalidatesAnyEarlierUnusedToken_WhenRequestedAgain` |
| AUTH-11 | Reset password with a valid token | Password changes; the old password stops working | .NET | Automated: `ResetPassword_ChangesThePassword_AndTheOldPasswordNoLongerWorks` |
| AUTH-12 | Reset with an unknown / reused / expired token | `400` in each case | .NET | Automated: `ResetPassword_Returns400_ForAnUnknownToken`, `..._WhenTheSameTokenIsUsedTwice`, `..._ForAnExpiredToken` |
| AUTH-13 | Token claims and expiry | `sub`, `email`, `role` present; expiry from config; wrong key fails validation | .NET | Planned (T2): `TokenServiceTests` |
| AUTH-14 | Register / login / reset-password input edge cases | Length, email format, whitespace rejected with `400` | .NET | Planned (T2): `ValidatorTests` |
| AUTH-15 | `AuthService` session handling | Starts logged out; register/login store token and user; logout clears; expired token is cleared; valid stored session survives a reload | Angular | Automated: 6 specs in `auth.service.spec.ts` |
| AUTH-16 | Auth interceptor | Adds `Authorization: Bearer` only to our own API, never to another host, nothing when logged out | Angular | Automated: 3 specs in `auth.interceptor.spec.ts` |
| AUTH-17 | Login / register / forgot / reset pages | Invalid forms don't submit; short password rejected; generic forgot-password message; missing token shows an "invalid link" state; mismatched passwords rejected; success messages | Angular | Automated: login 2, register 2, forgot-password 3, reset-password 3 specs |
| AUTH-18 | Route guards | Logged-out user is redirected to login; non-admin cannot open `/admin/usage` | Angular | Planned (T4): `authGuard` / `adminGuard` specs |
| AUTH-19 | JWT decoding helper | Valid, malformed and bad-base64 tokens | Angular | Planned (T4): `jwt.ts` spec |
| AUTH-20 | Password show/hide | Toggles `type` without touching the value | Angular | Automated: `flips the input between password and text without touching its value` |
| AUTH-21 | Login error mapping on the page (wrong password shows the inline error) | Inline error, no toast | Angular | Planned (T4) |
| AUTH-22 | Real forgot-password email arrives and the link works | Email in the inbox, reset succeeds | Manual | Manual: section 8 |

### 5.2 CHAT (.NET `ChatControllerTests`, Angular chat specs)

| ID | Scenario | Expected result | Layer | Status |
|---|---|---|---|---|
| CHAT-01 | Create a session without a title | Title defaults to "New chat" | .NET | Automated: `CreateSession_DefaultsTitle_WhenNoneProvided` |
| CHAT-02 | List sessions | Only the caller's own sessions | .NET | Automated: `ListSessions_OnlyReturnsCallersOwnSessions` |
| CHAT-03 | Send a message | `200` with the Python reply; assistant message and usage log persisted | .NET | Automated: `SendMessage_ReturnsOk_WithReplyFromPythonClient_AndPersistsUsageLog` |
| CHAT-04 | Python unavailable | `503`, but the user message stays persisted | .NET | Automated: `SendMessage_Returns503_AndStillPersistsUserMessage_WhenPythonIsUnavailable` |
| CHAT-05 | First message titles a "New chat" | Whitespace collapsed, kept afterwards | .NET | Automated: `SendMessage_TitlesANewChat_FromTheFirstMessage_AndKeepsItAfterwards` |
| CHAT-06 | Custom title is never overwritten | Title unchanged | .NET | Automated: `SendMessage_DoesNotRename_AChatWithACustomTitle` |
| CHAT-07 | Long first message | Cut at a word boundary with an ellipsis (max 60) | .NET | Automated: `SendMessage_ShortensALongFirstMessage_AtAWordBoundary` |
| CHAT-08 | Title survives a `503` | New title kept even when Python fails | .NET | Automated: `SendMessage_KeepsTheNewTitle_EvenWhenPythonIsUnavailable` |
| CHAT-09 | Empty message | `400` | .NET | Automated: `SendMessage_Returns400_ForEmptyContent` |
| CHAT-10 | Another user's session | `404`, not `403` | .NET | Automated: `SendMessage_Returns404_WhenSessionBelongsToAnotherUser` |
| CHAT-11 | Unknown session | `404` | .NET | Automated: `GetMessages_Returns404_WhenSessionDoesNotExist` |
| CHAT-12 | Admin reads another user's session | Allowed | .NET | Automated: `GetMessages_AllowsAdmin_ToReadAnotherUsersSession` |
| CHAT-13 | History window | Only the last 20 prior messages are sent to Python | .NET | Planned (T2): extend `ChatControllerTests` |
| CHAT-14 | Request JSON to Python | snake_case body, `X-Internal-Key` header; timeout or non-success becomes `PythonServiceUnavailableException` | .NET | Planned (T2): `PythonAiClientTests` |
| CHAT-15 | Send-message input edge cases | Whitespace-only and over-length content rejected | .NET | Planned (T2): `ValidatorTests` |
| CHAT-16 | Composer | Empty or whitespace draft is not sent; example prompt fills the composer | Angular | Automated: `does not send an empty or whitespace-only draft`, `fills the composer when an example prompt is used` |
| CHAT-17 | Copy a reply | Shows "Copied" briefly; does not claim it when the clipboard is blocked | Angular | Automated: `copies a reply to the clipboard and shows "Copied" briefly`, `does not claim "Copied" when the clipboard is blocked` |
| CHAT-18 | Sessions grouped by date | Today, Yesterday, Previous 7 days, Older; empty groups dropped | Angular | Automated: 3 specs in `session-groups.spec.ts` |
| CHAT-19 | Send flow in the component | Success shows the reply; `503` shows "didn't send" with "Send again"; retry works; title refreshes after the first send | Angular | Planned (T4): more `ChatComponent` specs |
| CHAT-20 | `ChatService` HTTP | Correct URLs, methods and bodies for the four chat endpoints | Angular | Planned (T4): `ChatService` spec |
| CHAT-21 | Reply with real LLM | A real answer appears in the UI | Manual | Manual: section 8 |

### 5.3 ADMIN

| ID | Scenario | Expected result | Layer | Status |
|---|---|---|---|---|
| ADM-01 | Usage list paging | `pageSize` capped at 100, page/total counts correct | .NET | Planned (T2): `AdminControllerTests` |
| ADM-02 | Date filter | `from` / `to` inclusive bounds | .NET | Planned (T2) |
| ADM-03 | `from` later than `to` | `400` | .NET | Planned (T2) |
| ADM-04 | Joined display fields | User name, email and session title present | .NET | Planned (T2) |
| ADM-05 | Non-admin calls `/api/admin/usage` | `403` with the standard error body | .NET | Planned (T2): `ErrorShapeIntegrationTests` |
| ADM-06 | Usage page | Applying a filter resets to page 1 | Angular | Automated: `resets to page 1 when a filter is applied` |
| ADM-07 | Usage page paging, filter, "Not tracked" cost | Pager buttons enable/disable correctly; cost shows "Not tracked" | Angular | Planned (T4): more `UsageComponent` specs |
| ADM-08 | `AdminService` HTTP | Correct URL and query parameters | Angular | Planned (T4): `AdminService` spec |

### 5.4 AI SERVICE (Python `test_generate.py`)

| ID | Scenario | Expected result | Layer | Status |
|---|---|---|---|---|
| AI-01 | Missing `X-Internal-Key` | `401` | Python | Automated: `test_missing_internal_key_returns_401` |
| AI-02 | Wrong `X-Internal-Key` | `401` | Python | Automated: `test_wrong_internal_key_returns_401` |
| AI-03 | Valid request | Reply returned with `tokens_used` | Python | Automated: `test_returns_reply` |
| AI-04 | Message construction | Full history plus the prompt as the final user message | Python | Automated: `test_sends_full_history_plus_prompt_as_the_final_user_message` |
| AI-05 | Empty LLM reply | Empty string, not an error | Python | Automated: `test_empty_reply_from_the_llm_becomes_an_empty_string_not_an_error` |
| AI-06 | LLM timeout / connection error / rate limit | `503` | Python | Automated: `test_timeout_maps_to_503`, `test_connection_error_maps_to_503`, `test_rate_limit_maps_to_503` |
| AI-07 | Other upstream error / unexpected exception | `502`, never a bare `500` | Python | Automated: `test_other_upstream_error_maps_to_502_not_a_crash`, `test_unexpected_exception_maps_to_502_never_a_bare_500` |
| AI-08 | Timeout nesting | Python's worst case (about 50 s) stays inside .NET's 60 s | Python | Automated: `test_llm_client_worst_case_fits_inside_dotnet_timeout` |
| AI-09 | Reply length cap | The SDK call receives `max_tokens` equal to `DEEPINFRA_MAX_TOKENS` | Python | Automated: `test_caps_reply_length` |
| AI-10 | Malformed body (missing prompt, wrong types) | `422` | Python | Planned (T3) |
| AI-11 | Invalid role in `history` | Rejected | Python | Planned (T3) |

### 5.5 ERRORS AND CROSS-CUTTING

| ID | Scenario | Expected result | Layer | Status |
|---|---|---|---|---|
| ERR-01 | Missing or invalid token on a protected route | `401` with `{ error: { code, message } }` | .NET | Planned (T2): `ErrorShapeIntegrationTests` |
| ERR-02 | Wrong role | `403` with the standard body | .NET | Planned (T2) |
| ERR-03 | Unknown route | `404` with the standard body | .NET | Planned (T2) |
| ERR-04 | Unhandled exception | `500` with the standard body, no stack trace | .NET | Planned (T2): `ExceptionHandlingMiddlewareTests` |
| ERR-05 | `/health` | `200`, no database check | .NET | Planned (T2) |
| ERR-06 | SMTP not configured | Fails fast with a clear error | .NET | Planned (T2): `SmtpEmailSender` fail-fast test |
| ERR-07 | Global error interceptor | Tracks loading on success and failure; toasts and re-throws on an unexpected `500`; no toast for handled `503` or validation `400`; stale session logs out, toasts and redirects to login; other hosts untouched | Angular | Automated: 6 specs in `error.interceptor.spec.ts` |
| ERR-08 | `extractErrorMessage` | Handles the API error shape, plain text, network errors | Angular | Planned (T4) |

### 5.6 FRONTEND (other)

| ID | Scenario | Expected result | Layer | Status |
|---|---|---|---|---|
| FE-01 | Markdown rendering | Headings, emphasis, lists, code and tables render | Angular | Automated: `renders headings, emphasis, lists, code and tables` |
| FE-02 | Markdown safety | Raw HTML shown as text; images never load; `javascript:` links neutralised; normal links open in a new tab | Angular | Automated: 4 specs in `markdown.component.spec.ts` |
| FE-03 | Theme | Defaults to light; toggle sets `data-theme` and is remembered; restores on startup; still works when storage throws | Angular | Automated: 4 specs in `theme.service.spec.ts` |
| FE-04 | App shell | App component creates | Angular | Automated: `should create the app` |
| FE-05 | Tab titles | Each route sets "Page - AI Chat Assistant" | Angular | Planned (T4): `AppTitleStrategy` spec |
| FE-06 | Top navigation | A Chatbot tab links to `/`; it is the current page (`aria-current="page"`) on `/` and on `/?session=<id>`, and not on another page; projected controls appear at the right-hand end | Angular | Automated: 5 specs in `top-nav.component.spec.ts` |
| FE-08 | Chat sidebar: pinned Book appointment row and "+" new-chat button | The first element in the sidebar is a link to `/appointments`; new chat is an icon-only button labelled "New chat" in the header and starts a new chat | Angular | Automated: `pins a Book appointment row at the top of the sidebar, linking to /appointments`, `starts a new chat from an icon-only "+" button in the sidebar header` |
| FE-07 | Appointments placeholder page | Says booking is coming soon, links back to the chatbot, shows the top nav, Log out returns to login | Angular | Automated: 3 specs in `appointments.component.spec.ts` |

### 5.7 UI/UX (manual)

| ID | Scenario | Expected result | Status |
|---|---|---|---|
| UX-01 | Every page at 390 px wide | No horizontal scroll; the chat sidebar becomes a drawer | Manual |
| UX-02 | Dark theme toggle on chat and usage pages | Readable contrast, state remembered after reload | Manual |
| UX-03 | OS in dark mode, theme not toggled | Page stays light | Manual |
| UX-04 | Keyboard only: Tab, Enter to send, Shift+Enter newline, visible focus rings | Every control reachable and operable | Manual |
| UX-05 | `prefers-reduced-motion` | Animations disabled | Manual |

### 5.8 OPS / DOCKER (manual)

| ID | Scenario | Expected result | Status |
|---|---|---|---|
| OPS-01 | `docker compose --env-file docker.env up --build -d` on a clean machine | All five services start; `api` and `web` report healthy | Manual |
| OPS-02 | Migrations | `migrate` runs once and exits `0`; tables exist | Manual |
| OPS-03 | Same-origin proxy | The site at `:8080` calls `/api` with no CORS errors; deep links (`/reset-password?token=...`) load | Manual |
| OPS-04 | Restart keeps data | `stop` then `up` keeps users and chats; `down -v` removes them | Manual |
| OPS-05 | MySQL stopped while the API runs | Requests fail with the standard error shape; recover after MySQL returns | Manual |

## 6. Coverage gaps and the phases that close them

| Phase | Gap | New tests |
|---|---|---|
| T2 | .NET: admin usage, token service, Python client, validators, exception middleware, `401`/`403`/`404`/`/health` pipeline behaviour, SMTP fail-fast, history window | `AdminControllerTests`, `TokenServiceTests`, `PythonAiClientTests`, `ValidatorTests`, `ExceptionHandlingMiddlewareTests`, `ErrorShapeIntegrationTests` (adds the test-only package `Microsoft.AspNetCore.Mvc.Testing`) |
| T3 | Python: malformed bodies (`422`) and invalid history role | Added to `ai-service-python/tests/` |
| T4 | Angular: guards, `jwt.ts`, `extractErrorMessage`, `ChatService`, `AdminService`, `AppTitleStrategy`, chat send/retry/`503`/title flows, usage paging, login/register error mapping | New specs next to their sources |
| T5 | Close-out | Statuses in this file changed from *Planned* to real test names; before/after counts; mutation spot-check; full run |

## 7. Planned test cases for features not built yet

These rows are filled in (and their test names recorded) when the feature phase is built. Phases are built in the order given by the Roadmap in `README.md` (next up: T2), not in number order; rows marked *because Phase N is built first* are obligations that a later phase picks up. Source plans in `README.md`: "User-to-user chat (Phases 9-14)", "Message brokers (Phases 15-20)" and "Hardening and product gaps (Phases 21-28)".

| ID | Scenario | Expected result | Layer | Status |
|---|---|---|---|---|
| DM-01 | Start a conversation twice with the same person | Same conversation returned (idempotent); the pair is stored lower id first | .NET | Planned (Phase 10) |
| DM-02 | Message yourself / unknown user | `400` / `404` | .NET | Planned (Phase 10) |
| DM-03 | Outsider or admin reads someone else's conversation | `404` for both (messages are private) | .NET | Planned (Phase 10) |
| DM-04 | User search | Matches name only, returns `{ id, name }`, never emails, never yourself, min 2 characters, max 20 results | .NET | Planned (Phase 10) |
| DM-05 | Send a message | Stored with status **sent** | .NET | Planned (Phase 10) |
| DM-06 | Send while the recipient is online | Status **delivered**, sender notified | .NET | Planned (Phase 11) |
| DM-07 | Recipient connects later | Pending messages marked delivered; sender notified | .NET | Planned (Phase 11) |
| DM-08 | Recipient reads the thread | Status **read**, sender notified, unread count clears | .NET | Planned (Phase 11) |
| DM-09 | Presence with several tabs | Online on the first connection; offline and `LastSeenAt` written only when the last one closes | .NET | Planned (Phase 11) |
| DM-10 | Hub authentication | JWT via `access_token` query string accepted; no token rejected | .NET | Planned (Phase 11) |
| DM-11 | Ticks component | One grey tick = sent, two grey = delivered, two blue = read, deleted shows none; accessible labels | Angular | Planned (Phase 12) |
| DM-12 | Unread badge and toast | Badge updates live; toast shows sender and preview only when that conversation isn't open and visible | Angular | Planned (Phase 12) |
| DM-13 | Typing indicator | Sent about every 3 s, clears after 4 s; rejected for non-participants | .NET + Angular | Planned (Phase 13) |
| DM-14 | Delete own message | Sender only; content cleared on the server; both sides see "This message was deleted" | .NET + Angular | Planned (Phase 13) |
| BRK-01 | Presence and push across two API replicas | Event reaches a user connected to the other replica | Manual | Planned (Phase 16) |
| BRK-02 | Forgot password publishes an email job only for existing users | Same generic response either way; retries; dead-letter after the last failure | .NET | Planned (Phase 17) |
| BRK-03 | Async AI reply (`Chat:AsyncReplies` on) | `202` then `AssistantReplied`; a redelivered job is a no-op; failure then retry then `AssistantFailed`; broker down gives `503` with the user message kept | .NET + Angular | Planned (Phase 18) |
| BRK-04 | Direct-chat events | Written in the same transaction as the change; payload never contains message text | .NET | Planned (Phase 19) |
| SEC-01 | More than the allowed login attempts from one IP | `429` in the standard error shape, then allowed again after the window | .NET | Planned (Phase 21) |
| SEC-02 | Behind the proxy, `X-Forwarded-For` | The limiter keys on the real client IP, not the proxy address | .NET | Planned (Phase 21) |
| SEC-03 | Repeated failed logins for one account | Account locked for the configured time; correct password still refused while locked | .NET | Planned (Phase 21) |
| SEC-04 | Per-user chat quota exceeded | `429`, user message not sent to the LLM | .NET | Planned (Phase 21) |
| SEC-05 | Response headers from nginx | CSP, `X-Content-Type-Options`, frame-ancestors and referrer policy present; no server version | Manual | Planned (Phase 21) |
| ACC-01 | Change password with the wrong current password | `400`, nothing changes | .NET | Planned (Phase 22) |
| ACC-02 | Change password successfully | Old password stops working; other sessions' tokens are rejected | .NET | Planned (Phase 22) |
| ACC-03 | Delete account | User and their chats, messages and tokens removed; cannot log in afterwards | .NET | Planned (Phase 22) |
| ACC-04 | Register with an existing email | Behaviour as decided in Phase 22 (generic response or `409`), covered by a test either way | .NET | Planned (Phase 22) |
| ACC-05 | Profile page | Name edit, change-password form validation and confirm-delete dialog | Angular | Planned (Phase 22) |
| TOK-01 | Access token expires | The interceptor refreshes once and retries the request; a second `401` logs out | Angular | Planned (Phase 23) |
| TOK-02 | Refresh token rotation | Each refresh returns a new token; reusing an old one is rejected and revokes the family | .NET | Planned (Phase 23) |
| TOK-03 | Logout and password change | Refresh token revoked; old access token refused | .NET | Planned (Phase 23) |
| TOK-04 | Open SignalR connection across a token refresh | Connection stays usable and uses the refreshed token | Angular + .NET | Planned (Phase 11, because Phase 23 is built first) |
| CHM-01 | Rename a chat | Title changes; another user's chat gives `404` | .NET | Planned (Phase 24) |
| CHM-02 | Delete a chat | Hidden from lists; messages not returned; `UsageLogs` rows kept | .NET | Planned (Phase 24) |
| CHM-03 | Search chats by title | Only the caller's matches, case-insensitive | .NET | Planned (Phase 24) |
| CHM-04 | Paged sessions and messages | Page size capped; `before` cursor returns older messages without gaps or duplicates | .NET | Planned (Phase 24) |
| CHM-05 | Infinite scroll and confirm-delete | Older messages load on scroll; delete needs confirmation | Angular | Planned (Phase 24) |
| STR-01 | Stream a reply | Chunks arrive in order; the final message is saved once | Python + .NET | Planned (Phase 25) |
| STR-02 | Stop mid-stream | Generation stops; behaviour for the partial text as decided in Phase 25 | .NET + Angular | Planned (Phase 25) |
| STR-03 | Upstream error mid-stream | Error event delivered; the user message stays saved | Python + .NET | Planned (Phase 25) |
| STR-04 | Token-aware history trimming | Newest messages kept within the budget; an oversized history no longer causes a `502` | .NET | Planned (Phase 25) |
| COST-01 | Cost calculation | Prompt and completion tokens times configured price, stored in `CostEstimate` | .NET | Planned (Phase 26) |
| COST-02 | Usage aggregates | Totals per user and per day are computed server-side, not from the current page | .NET | Planned (Phase 26) |
| COST-03 | Admin disables a user | Login refused and an existing token rejected; a non-admin gets `403` | .NET | Planned (Phase 26) |
| COST-05 | A disabled user opens the SignalR hub | Connection refused | .NET | Planned (Phase 11, because Phase 26 is built first) |
| ACC-06 | Delete account for a user who has direct conversations | Their conversations and messages are removed or anonymised | .NET | Planned (Phase 10, because Phase 22 is built first) |
| SEC-06 | The CSP allows the `/hubs/` WebSocket | The hub connects in the browser with no CSP violation | Manual | Planned (Phase 11, because Phase 21 is built first) |
| COST-04 | Usage page cost | Shows a currency value instead of "Not tracked" once cost exists | Angular | Planned (Phase 26) |
| OPS-06 | `/health/ready` | `200` when the database is reachable, `503` when it is not | .NET | Planned (Phase 27) |
| OPS-07 | Correlation id | Each request has an id in the logs and the response header | .NET | Planned (Phase 27) |
| OPS-08 | Backup and restore | The script produces a dump that restores into an empty database | Manual | Planned (Phase 27) |
| OPS-09 | Usage-log retention | Rows older than the configured age are purged; newer rows kept | .NET | Planned (Phase 27) |
| A11Y-01 | Unknown URL | A real 404 page with a link home (no silent redirect) | Angular | Planned (Phase 28) |
| A11Y-02 | Automated accessibility scan (axe-core) on login, chat and usage pages | No serious or critical violations | Angular | Planned (Phase 28) |

## 8. Manual smoke checklist and exit criteria

Run on the Docker stack after a change that touches more than one layer. Use throwaway accounts and clean up afterwards.

1. `./scripts/gen-docker-env.sh`, set `DEEPINFRA_API_KEY` in `docker.env`, then `docker compose --env-file docker.env up --build -d`. All services healthy (OPS-01, OPS-02).
2. Open http://localhost:8080, register a new account, and land on the chat page (AUTH-01).
3. Send a message and get a real reply with markdown rendered; use Copy (CHAT-21, CHAT-17).
4. Reload the page: still logged in; the chat is in the sidebar under "Today" (AUTH-15, CHAT-18).
5. Log out and log in again with a wrong password, then the right one (AUTH-04, AUTH-05).
6. Forgot password with a real address (needs Gmail SMTP configured): the email arrives and the link resets the password (AUTH-22).
7. As an admin (see README "Becoming an Admin"): open Usage logs, apply a date filter, page through (ADM-01 to ADM-07).
8. Toggle the dark theme and check 390 px width on chat, login and usage (UX-01 to UX-03).
9. Stop the stack (`stop`), start it again, confirm data survived (OPS-04). Finally delete the test data (`down -v` or SQL on the container).

**Exit criteria for a finished phase:** all three suites pass; every behaviour the phase added has a catalogue row pointing at a real test name; no `Planned` row remains for that phase; the suite counts in the header of this file are up to date.
