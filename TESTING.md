# Testing

The test plan and test-case catalogue for the whole application. It is the single place to answer "what is tested, how, and what is not yet".

**Status today:** 544 automated tests (344 .NET, 12 Python, 188 Angular), all passing, plus 261 end-to-end checks in 12 committed suites under `e2e/` that drive the real Docker stack (`./e2e/run.sh`, section 17). There is no CI yet. The most recent runs, with results and findings, are in **section 17** (Phase 20, the closing end-to-end run, latest), **section 16** (gap-closing run), **section 15** (Phases 18 and 19), **section 14** (Phase 16), **section 13** (Phases 15 and 17), **section 12** (Phase 14), **section 11** (Phase 13), **section 10** (Phase 12) and **section 9** (Phase 11). Test gaps are listed in section 6 and are closed phase by phase (T2 to T5, see `README.md`). Cases for planned features (chat, brokers, hardening) are listed in section 7 and filled in as each phase is built.

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
# .NET (344 tests)
cd backend-dotnet && dotnet test
dotnet test --filter "FullyQualifiedName~ChatControllerTests"          # one class

# Python (12 tests)
cd ai-service-python && source .venv/bin/activate && pytest
pytest tests/test_generate.py::test_returns_reply                      # one test

# Angular (188 tests)
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
| AUTH-23 | Forgot password when sending the email fails (SMTP down, bad credentials, not configured) | Same `200` and the same generic message as for an unknown email, so no account is revealed; the failure is logged by user id; a cancelled request still cancels | .NET | Automated: `ForgotPassword_ReturnsTheSameGenericMessage_WhenSendingTheEmailFails_SoNoAccountIsRevealed`, `ForgotPassword_StillPropagatesCancellation_RatherThanSwallowingIt` |
| AUTH-13 | Token claims and expiry | `sub`, `email`, `role` present; expiry from config; wrong key fails validation | .NET | `TokenServiceTests` (8): claims `sub`/`email`/`role`, unique `jti`, 60-minute default and configured expiry, issuer/audience defaults, signature valid/invalid, tampered payload, missing key |
| AUTH-14 | Register / login / reset-password input edge cases | Length, email format, whitespace rejected with `400` | .NET | `ValidatorTests` (register name/email/password limits, login, forgot-password, reset-password) |
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
| CHAT-13 | History window | Only the last 20 prior messages are sent to Python | .NET | `ChatControllerTests.SendMessage_SendsPythonOnlyTheLast20PriorMessages_OldestFirst`, `SendMessage_WithShortHistory_SendsAllOfIt_WithLowerCaseRoles`, `SendMessage_HistoryIgnoresOtherSessions` |
| CHAT-14 | Request JSON to Python | snake_case body, `X-Internal-Key` header; timeout or non-success becomes `PythonServiceUnavailableException` | .NET | `PythonAiClientTests` (12): POST `/generate`, snake_case body, `X-Internal-Key`, empty history, reply mapping, `401`/`422`/`500`/`502`/`503` and connection failure and own timeout and `null` body become `PythonServiceUnavailableException`, a caller cancellation does not |
| CHAT-15 | Send-message input edge cases | Whitespace-only and over-length content rejected | .NET | `ValidatorTests.SendMessage_*` (8000 limit, blank and whitespace-only), `ChatControllerTests.SendMessage_Returns400_ForWhitespaceOnlyContent_AndSavesNothing` |
| CHAT-16 | Composer | Empty or whitespace draft is not sent; example prompt fills the composer | Angular | Automated: `does not send an empty or whitespace-only draft`, `fills the composer when an example prompt is used` |
| CHAT-17 | Copy a reply | Shows "Copied" briefly; does not claim it when the clipboard is blocked | Angular | Automated: `copies a reply to the clipboard and shows "Copied" briefly`, `does not claim "Copied" when the clipboard is blocked` |
| CHAT-18 | Sessions grouped by date | Today, Yesterday, Previous 7 days, Older; empty groups dropped | Angular | Automated: 3 specs in `session-groups.spec.ts` |
| CHAT-19 | Send flow in the component | Success shows the reply; `503` shows "didn't send" with "Send again"; retry works; title refreshes after the first send | Angular | Planned (T4): more `ChatComponent` specs |
| CHAT-20 | `ChatService` HTTP | Correct URLs, methods and bodies for the four chat endpoints | Angular | Planned (T4): `ChatService` spec |
| CHAT-21 | Reply with real LLM | A real answer appears in the UI | Manual | Manual: section 8 |

### 5.3 ADMIN

| ID | Scenario | Expected result | Layer | Status |
|---|---|---|---|---|
| ADM-01 | Usage list paging | `pageSize` capped at 100, page/total counts correct | .NET | `AdminControllerTests.Usage_PageSizeIsCappedAt100_AndFloorsAtOne`, `Usage_PagesAreNewestFirst_AndPageBelowOneBecomesOne`, `Usage_EmptyTable_ReturnsAnEmptyPage_NotAnError` |
| ADM-02 | Date filter | `from` / `to` inclusive bounds | .NET | `AdminControllerTests.Usage_DateFilter_IncludesBothBounds`, `Usage_OnlyFrom_And_OnlyTo_WorkAlone`, `Usage_FromEqualToTo_IsAllowed` |
| ADM-03 | `from` later than `to` | `400` | .NET | `AdminControllerTests.Usage_FromLaterThanTo_Is400_InTheStandardShape` |
| ADM-04 | Joined display fields | User name, email and session title present | .NET | `AdminControllerTests.Usage_RowsCarryTheJoinedUserAndSessionFields` |
| ADM-05 | Non-admin calls `/api/admin/usage` | `403` with the standard error body | .NET | `PipelineTests.AdminRoute_AsANormalUser_Is403_InTheStandardShape`, `AdminRoute_AsAnAdmin_Is200` |
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
| ERR-01 | Missing or invalid token on a protected route | `401` with `{ error: { code, message } }` | .NET | `PipelineTests.ProtectedRoute_WithoutAToken_Is401_InTheStandardShape`, `..._WithAGarbageOrMisSignedToken_...`, `..._WithATokenSignedByAnotherKey_Is401` |
| ERR-02 | Wrong role | `403` with the standard body | .NET | `PipelineTests.AdminRoute_AsANormalUser_Is403_InTheStandardShape` |
| ERR-03 | Unknown route | `404` with the standard body | .NET | `PipelineTests.UnknownRoute_Is404_InTheStandardShape` |
| ERR-04 | Unhandled exception | `500` with the standard body, no stack trace | .NET | `ExceptionHandlingMiddlewareTests` (4) and `PipelineTests.UnhandledException_Is500_InTheStandardShape_WithNoStackTraceOrExceptionText` |
| ERR-05 | `/health` | `200`, no database check | .NET | `PipelineTests.Health_Is200_WithoutAToken` |
| ERR-06 | SMTP username or password missing, empty or whitespace | Fails fast with a clear message naming the setting and its environment variable; never attempts a mail login | .NET | Automated: `ABlankOrMissingUsername_FailsFastWithAClearMessage_NotAGmailLoginAttempt` (3 cases), `ABlankOrMissingPassword_FailsFastWithAClearMessage` (3 cases) in `SmtpEmailSenderTests` |
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
| OPS-01 | `docker compose --env-file docker.env up --build -d` on a clean machine | All services (mysql, rabbitmq, redis, migrate, ai-service, api, web) start; `api` and `web` report healthy | Manual |
| OPS-02 | Migrations | `migrate` runs once and exits `0`; tables exist | Manual |
| OPS-03 | Same-origin proxy | The site at `:8080` calls `/api` with no CORS errors; deep links (`/reset-password?token=...`) load | Manual |
| OPS-04 | Restart keeps data | `stop` then `up` keeps users and chats; `down -v` removes them | Manual |
| OPS-05 | MySQL stopped while the API runs | Requests fail with the standard error shape; recover after MySQL returns | Manual |

### 5.9 DIRECT CHAT, REST only (.NET `DirectChatControllerTests`, Phase 10)

| ID | Scenario | Expected result | Layer | Status |
|---|---|---|---|---|
| DM-01 | Start a conversation | Created with `201`; the pair is stored lower id first whoever starts it | .NET | Automated: `StartConversation_CreatesIt_StoresThePairLowerIdFirst_AndReturns201` |
| DM-02 | Start it again, from either side | Same conversation returned with `200`; still exactly one row | .NET | Automated: `StartConversation_IsIdempotent_WhoeverStartsIt` |
| DM-03 | Start a chat with yourself, an unknown user, or an invalid id | `400` / `404` / `400`; nothing created | .NET | Automated: `StartConversation_Returns400_ForYourself`, `StartConversation_Returns404_ForAnUnknownUser`, `StartConversation_Returns400_ForAnInvalidUserId` |
| DM-04 | Someone outside the conversation, or an Admin, sends or reads | `404` (direct messages are private; no Admin exception); nothing stored | .NET | Automated: `SendMessage_Returns404_ForSomeoneWhoIsNotInTheConversation`, `SendMessage_Returns404_ForAnAdminWhoIsNotInTheConversation_BecauseDirectMessagesArePrivate`, `GetMessages_Returns404_ForAnOutsider_AndForAnAdmin` |
| DM-05 | User search | Matches the name only (case-insensitive, anywhere in the name); returns only `{ id, name }`; never the caller; email text never matches | .NET | Automated: `SearchUsers_MatchesByName_AndReturnsOnlyIdAndName`, `SearchUsers_DoesNotMatchOnEmail_SoAnEmailCannotBeProbed`, `SearchUsers_NeverReturnsTheCaller`, `SearchUsers_IsCaseInsensitive_AndMatchesAnywhereInTheName` |
| DM-06 | Search term under 2 characters (null, blank, 1 character) | `400` | .NET | Automated: `SearchUsers_Returns400_ForATermShorterThanTwoCharacters` (5 cases) |
| DM-07 | More than 20 matches | At most 20 results, in name order | .NET | Automated: `SearchUsers_ReturnsAtMostTwentyResults_InNameOrder` |
| DM-08 | Send a message | `201`, stored trimmed with status **sent** | .NET | Automated: `SendMessage_StoresTheMessageAsSent_Trimmed_AndReturns201` |
| DM-09 | Send empty, whitespace-only, over-length, or to an unknown conversation | `400` / `400` / `400` (exactly 8000 is accepted) / `404`; nothing stored | .NET | Automated: `SendMessage_Returns400_ForEmptyOrWhitespaceContent`, `SendMessage_Returns400_ForOver8000Characters_AndAcceptsExactly8000`, `SendMessage_Returns404_ForAnUnknownConversation` |
| DM-10 | List conversations | Only the caller's; newest activity first; last message and unread count; your own messages are not unread; an empty conversation is listed | .NET | Automated: `ListConversations_ReturnsOnlyTheCallersConversations_NewestActivityFirst_WithUnreadCounts`, `ListConversations_DoesNotCountYourOwnMessagesAsUnread_AndShowsAnEmptyConversation` |
| DM-11 | Peer presence fields before the realtime layer exists | `peerOnline` is false; `peerLastSeenAt` comes from the user | .NET | Automated: `ListConversations_ReportsThePeerAsOffline_UntilPresenceExists` |
| DM-12 | Read messages | Oldest first, identical for both participants | .NET | Automated: `GetMessages_ReturnsTheConversationOldestFirst_ForEitherParticipant` |
| DM-13 | Page backwards with `before` | Three pages of 10 cover 25 messages with no gaps or duplicates | .NET | Automated: `GetMessages_PagesBackwardsWithTheBeforeCursor_WithoutGapsOrDuplicates` |
| DM-14 | Page size | Default 50; zero or negative uses 50; capped at 100 | .NET | Automated: `GetMessages_UsesADefaultPageOf50_AndCapsALimitAt100` (4 cases) |
| DM-15 | Tick status and deleted messages | Status derived from the timestamps (sent, delivered, read); a deleted message shows no text and `deleted: true` | .NET | Automated: `GetMessages_DerivesTheStatusFromTimestamps_AndClearsDeletedContent` |
| DM-16 | Unique pair index, foreign-key cascade, new tables and column on real MySQL | Duplicate pair rejected (error 1062); deleting a user removes their conversations and messages; `DirectConversations`, `DirectMessages` and `Users.LastSeenAt` exist | Manual | Manual: verified on the Docker stack's MySQL on 2026-10-14 (the in-memory test provider cannot enforce either rule); `TheModelDeclaresAUniqueIndexOnTheUserPair_SoTheDatabaseCanRejectDuplicates` checks the model |

Not yet covered: the concurrent double-start race (two people start the same chat at the same instant, the unique index rejects one, the controller reopens the winner) because it needs a real database and parallel requests. It is exercised end to end in Phase 14.

### 5.10 REALTIME DIRECT CHAT: delivered and read ticks, presence (.NET, Phase 11)

| ID | Scenario | Expected result | Layer | Status |
|---|---|---|---|---|
| RT-01 | Send while the recipient is online | Status **delivered** at once; the recipient is pushed `MessageReceived` | .NET | Automated: `SendMessage_IsDelivered_WhenTheRecipientIsOnline_AndTheRecipientIsNotified` |
| RT-02 | Send while the recipient is offline, or only the sender is online | Status stays **sent**; a push is still attempted; the sender's own presence does not count | .NET | Automated: `SendMessage_StaysSent_WhenTheRecipientIsOffline_ButStillTriesToNotify`, `SendMessage_OnlyTheRecipientBeingOnlineCounts_NotTheSender` |
| RT-03 | A user connects | Joins their personal group, goes online, and everything waiting for them becomes **delivered** (each sender is told, once per conversation); own, already-delivered, deleted and other people's messages are left alone; a second connection does nothing | .NET | Automated: `Connecting_JoinsThePersonalGroup_GoesOnline_AndDeliversWhatWasWaiting`, `ConnectingMarksEverythingWaitingForTheUserAsDelivered_AndTellsEachSender`, `ItLeavesAlone_TheUsersOwnMessages_AlreadyDeliveredOnes_DeletedOnes_AndOtherPeoplesConversations`, `ASecondConnection_FindsNothingLeftToDo_AndNotifiesNobody` |
| RT-04 | `POST .../read` | The peer's unread messages become **read** (and delivered); the sender gets `MessageStatusChanged`, the reader's other tabs get `MessagesRead`; own messages and deleted ones are untouched; reading again does nothing and pushes nothing; the unread count clears | .NET | Automated: `MarkRead_MarksThePeersMessagesRead_ImpliesDelivered_AndNotifiesBothSides`, `MarkRead_DoesNotTouchTheCallersOwnMessages`, `MarkRead_ReadsNothingTwice_AndNotifiesNobodyWhenThereIsNothingNew`, `MarkRead_SkipsDeletedMessages`, `MarkRead_ClearsTheUnreadCountInTheConversationList` |
| RT-05 | An outsider or an Admin marks a conversation read | `404`, nothing changes, nothing is pushed | .NET | Automated: `MarkRead_Returns404_ForAnOutsider_AndForAnAdmin_AndChangesNothing` |
| RT-06 | Presence with several tabs | Online on the first connection, offline only when the last closes; unknown connections change nothing; users are independent; the conversation list shows `peerOnline` accordingly | .NET | Automated: 4 tests in `PresenceTrackerTests`, `ListConversations_ShowsThePeerOnline_WhileTheyAreConnected_AndOfflineAfterTheLastTabCloses` |
| RT-07 | Last seen | Written only when the last tab closes; never for a connection that was never seen | .NET | Automated: `LastSeenIsWrittenOnlyWhenTheLastTabCloses`, `DisconnectingSomeoneWhoWasNeverConnected_DoesNotTouchLastSeen` |
| RT-08 | A hub connection with no user id | Aborted, never counted online, no group joined | .NET | Automated: `AConnectionWithoutAUserId_IsAbortedAndNeverCountsAsOnline` |
| RT-09 | The push layer | Events go to the right personal group under the agreed names (`MessageReceived`, `MessageStatusChanged`, `MessagesRead`); a failing push never throws into the request | .NET | Automated: 4 tests in `SignalRDirectNotifierTests`, `ThePersonalGroupNameIsStable` |
| RT-10 | The whole flow over real WebSockets through nginx | Sent, delivered, read, presence and deliver-on-reconnect all behave as above; the hub refuses a token-less connection | Manual | Manual: 10 checks in section 9.2 (`E2E-RT-01` to `E2E-RT-10`, `E2E-DM-06`), all passed on 2026-10-20 |

### 5.11 MESSAGES UI: ticks, unread badge, toast (Angular, Phase 12)

| ID | Scenario | Expected result | Layer | Status |
|---|---|---|---|---|
| UIM-01 | Tick icon states | One grey tick = sent; two grey = delivered; two blue = read; an accessible label and title on each (colour is never the only signal); updates when the status moves forward | Angular | Automated: 5 specs in `ticks.component.spec.ts` |
| UIM-02 | REST service | Correct URLs, methods, query parameters and bodies for search, list, start, get messages (optional `before`/`limit`), send, mark read | Angular | Automated: 6 specs in `direct-chat.service.spec.ts` |
| UIM-03 | Hub URL | `/api` becomes `/hubs/direct`; an absolute dev API base keeps its host | Angular | Automated: 2 specs (`hubUrlFromApiBase`) in `direct-hub.service.spec.ts` |
| UIM-04 | Hub connection lifecycle | Connects with the current token (asked again each time, so a refreshed token is used); never opens a second connection; forwards the three events; signals a reconnect; stops; can restart; retries with a growing delay when the first attempt fails and stops retrying after sign-out; closes a connection that finished after sign-out | Angular | Automated: 10 specs in `direct-hub.service.spec.ts` |
| UIM-05 | Unread and live updates | A message in a conversation you are not viewing counts as unread, moves it to the top and toasts "Name: preview"; in the open, visible conversation it is marked read at once with no toast; open but tab hidden counts as unread and toasts, then is marked read when the tab becomes visible; duplicates and your own messages are ignored; an unknown conversation reloads the list without double counting | Angular | Automated: 7 specs in `direct-state.service.spec.ts` |
| UIM-06 | Tick updates from the server | `delivered` and `read` events upgrade the right messages in the thread and the list preview, never move a tick backwards, and only touch the named ids; a "read in another tab" event clears unread | Angular | Automated: 4 specs in `direct-state.service.spec.ts` |
| UIM-07 | Opening, sending, starting, paging | Opening loads the thread and marks read only if something is unread and the tab is visible; sending shows the saved message with its real status and moves the chat to the top; a failed send leaves the thread unchanged; starting a chat adds it at the top without duplicates; earlier messages prepend without duplicates | Angular | Automated: 7 specs in `direct-state.service.spec.ts` |
| UIM-08 | Recovery and sign-out | After a reconnect the list and open thread are refetched; stopping forgets all chats, stops the hub and ignores later events; start is idempotent; a list failure shows a readable error | Angular | Automated: 4 specs in `direct-state.service.spec.ts`, plus `previewOf` (2 specs) |
| UIM-09 | Messages page: list | Name, preview ("You: ..." for your own), unread badge with accessible label (capped at 99+), ticks only on your own last message, empty hint, opening writes `?c=` to the URL | Angular | Automated: 5 specs in `messages.component.spec.ts` |
| UIM-10 | Messages page: thread | Bubbles with ticks only on your own messages, in the right state; text rendered as plain text (HTML shown literally); deleted placeholder with no ticks; hello prompt in an empty thread; pick-a-conversation prompt; unread messages marked read on open; "Chats" back button; a dead conversation id in the URL is dropped | Angular | Automated: 8 specs in `messages.component.spec.ts` |
| UIM-11 | Messages page: sending | Trimmed draft sent, box cleared, new bubble with one grey tick; empty or whitespace draft not sent and button disabled; a failed send keeps the draft and shows an error; Enter sends and Shift+Enter does not | Angular | Automated: 4 specs in `messages.component.spec.ts` |
| UIM-12 | Messages page: name search | Nothing searched under 2 characters; one request after typing pauses; results show names only; "no one found"; clicking a result starts the chat, clears the search and opens it | Angular | Automated: 4 specs in `messages.component.spec.ts` |
| UIM-13 | Top bar | Chatbot and Messages tabs; Messages current on `/messages`; live unread badge (hidden at 0, updates, capped at 99+, accessible text) | Angular | Automated: 9 specs in `top-nav.component.spec.ts` |
| UIM-14 | App shell | The live connection is started when someone signs in and stopped when they sign out, and kept off while signed out | Angular | Automated: 3 specs in `app.component.spec.ts` |
| UIM-15 | Two real browsers on the real stack | Sent, delivered and read ticks, toast, badge, reload, dark theme, mobile, sign-out | Manual | Manual: 17 checks in section 10.2, all passed on 2026-10-21 |

### 5.12 TYPING, ONLINE / LAST SEEN, DELETE, UTC TIMES (Phase 13)

Test cases, with the automated test (or manual check) that covers each. "Hub" = the SignalR server hub; all .NET tests use an in-memory database and mocked pushes, no real network.

| ID | Test case (scenario) | Expected result | Layer | Status |
|---|---|---|---|---|
| TYP-01 | A participant types: the hub is told `Typing(conversationId)` | `PeerTyping` is pushed to the **other** participant only, in either direction, and nothing else is pushed | .NET | Automated: `Typing_IsForwardedToTheOtherParticipantOnly`, `Typing_WorksBothWays` |
| TYP-02 | Someone outside the conversation, an unknown conversation id, or a connection with no user id sends `Typing` | Silently ignored: no push, no error (a probe learns nothing about which ids exist) | .NET | Automated: `Typing_FromSomeoneOutsideTheConversation_IsIgnored_NotForwardedAndNoError`, `Typing_ForAnUnknownConversation_IsIgnored`, `Typing_WithoutAUserId_IsIgnored` |
| TYP-03 | The other person types | "typing…" shows in the thread header and in the list preview; it clears after 4 quiet seconds; each new signal restarts the 4 seconds; your own signal is ignored; each conversation is independent | Angular | Automated: 3 specs in `direct-state.service.spec.ts` (shows and clears, keeps showing, own signal and separate conversations) |
| TYP-04 | Their message arrives, or they go offline, or you sign out | "typing…" is cleared at once; no timers are left running after sign-out | Angular | Automated: 3 specs in `direct-state.service.spec.ts` |
| TYP-05 | You type | One signal for a burst of keystrokes, at most one every 3 seconds per conversation; none for an empty or whitespace box; sending a message resets the throttle | Angular | Automated: 3 specs in `direct-state.service.spec.ts`, `tells the other person you are typing, once for a burst of keystrokes, and not for an empty box` in `messages.component.spec.ts` |
| TYP-06 | The hub client sends typing | Invokes `Typing` with the conversation id; sends nothing before it is connected or after stop; swallows a failed call | Angular | Automated: 3 specs in `direct-hub.service.spec.ts` |
| TYP-07 | Real keystrokes in two real browsers | The header and list show "typing…" on the other screen; 35 keystrokes send exactly 1 signal and a second burst after a quiet spell sends the 2nd (counted from the real WebSocket frames) | Manual | Manual: `E2E-M13-02` to `E2E-M13-05` |
| PRS-01 | A user's first connection | `PeerPresenceChanged(online)` is pushed to every person who shares a conversation with them, and to no one else | .NET | Automated: `ComingOnline_IsBroadcastToEveryoneWhoSharesAConversation_AndNoOneElse` |
| PRS-02 | A second tab opens, or one of two tabs closes | No broadcast (they did not come online or go offline) | .NET | Automated: `ASecondTab_DoesNotBroadcastAgain`, first half of `GoingOffline_IsBroadcastOnlyWhenTheLastTabCloses_WithTheSameLastSeenAsStored` |
| PRS-03 | The last tab closes | `PeerPresenceChanged(offline, lastSeenAt)` is pushed, and `lastSeenAt` equals the value stored on the user | .NET | Automated: `GoingOffline_IsBroadcastOnlyWhenTheLastTabCloses_WithTheSameLastSeenAsStored` |
| PRS-04 | A person with no conversations connects | Nothing is pushed to anyone (empty recipient list) | .NET | Automated: `APersonWithNoConversations_BroadcastsToNobody` |
| PRS-05 | The presence event arrives in the browser | Online and last seen update for the right person only; a "came online" event keeps the earlier last-seen time | Angular | Automated: 2 specs in `direct-state.service.spec.ts` |
| PRS-06 | The thread header and list | "Online" in green with a dot; "Last seen today at …", "yesterday at …" or a date; no line at all for someone never seen; "typing…" overrides the rest; no dot when offline | Angular | Automated: 4 specs in `messages.component.spec.ts` (online, last seen, never seen, older times in either locale order) |
| PRS-07 | Two real browsers | No status line for a never-connected person; "Online" and a green dot appear live when they sign in; "Last seen today at 20:48" appears live when they sign out and is identical after a reload | Manual | Manual: `E2E-M13-01`, `E2E-M13-11` |
| DEL-01 | The sender deletes their message | `200`; response has `deleted: true` and empty content; the database row has `Content` empty and `DeletedAt` set (the text is really gone) | .NET | Automated: `DeleteMessage_BySender_ClearsTheTextForGood_AndReturnsItAsDeleted` |
| DEL-02 | Who is told | `MessageDeleted` is pushed to the other person and to the sender (their other tabs) | .NET | Automated: `DeleteMessage_PushesMessageDeletedToBothPeople` |
| DEL-03 | Deleting the same message again | `200`, still deleted, and no second push | .NET | Automated: `DeleteMessage_Twice_IsAHarmlessNoOp_WithNoSecondPush` |
| DEL-04 | The other participant tries to delete it | `403 forbidden` ("You can only delete your own messages."), nothing changes, nothing is pushed | .NET | Automated: `DeleteMessage_ByTheOtherParticipant_Returns403_AndChangesNothing` |
| DEL-05 | An outsider or an Admin tries | `404` (direct messages stay private), nothing changes | .NET | Automated: `DeleteMessage_ByAnOutsiderOrAnAdmin_Returns404_AndChangesNothing` |
| DEL-06 | An unknown message id, or a message that belongs to another conversation | `404` in both cases, the other conversation's message is untouched | .NET | Automated: `DeleteMessage_Returns404_ForAnUnknownMessage_AndForOneInAnotherConversation` |
| DEL-07 | Deleting an unread message | The recipient's unread count drops by one | .NET | Automated: `DeleteMessage_RemovesAnUnreadMessageFromTheRecipientsUnreadCount` |
| DEL-08 | After deletion, both people load the thread and list | Placeholder with empty text, `deleted: true`, in the thread and in the list preview | .NET | Automated: `DeleteMessage_ShowsAsADeletedPlaceholderToBothPeople_WithNoText` |
| DEL-09 | The push reaches the browser | The message becomes a placeholder in the thread and the list preview; the unread count is recounted when it was the other person's message; a deletion of your own message from another tab needs no recount; an unknown message id is harmless | Angular | Automated: 3 specs in `direct-state.service.spec.ts` |
| DEL-10 | You delete through the state service | Text cleared in the thread and the list preview; a failed delete leaves the message and rejects | Angular | Automated: 2 specs in `direct-state.service.spec.ts`, `deletes a message with DELETE on the message url` in `direct-chat.service.spec.ts` |
| DEL-11 | The Delete button and its confirm step | Delete only on your own, not-yet-deleted messages (accessible label); "Delete for everyone?" asks first; Cancel backs out; Confirm shows the placeholder with no text, no ticks and no Delete; a failed delete keeps the text and shows an error | Angular | Automated: 4 specs in `messages.component.spec.ts` |
| DEL-12 | The pushes themselves | `MessageDeleted`, `PeerTyping` (to the recipient only) and `PeerPresenceChanged` (one call, one group per peer, nothing when there are no peers) go to the right groups under the agreed names | .NET + Angular | Automated: 4 new specs in `SignalRDirectNotifierTests`, `forwards the delete, typing and presence events too` in `direct-hub.service.spec.ts` |
| DEL-13 | Two real browsers | Cancel keeps it; confirm removes it from both screens live, and the original text is nowhere in either page or in the database; the other person has no Delete button and the API answers `403`; deleting an unread message lowers the other person's badge from 2 to 1 live | Manual | Manual: `E2E-M13-06` to `E2E-M13-10` |
| UTC-01 | A date read from the database | Marked UTC without changing the moment; sent to the browser with a trailing `Z` | .NET | Automated: `ReadingADateFromTheDatabase_MarksItUtc_WithoutChangingTheValue`, `ADateReadFromTheDatabase_IsSentToTheBrowserWithATrailingZ` |
| UTC-02 | A date written to the database | A local time is converted to UTC; a UTC time is left alone; a nullable date keeps `null` | .NET | Automated: `WritingALocalDate_ConvertsItToUtc_AndLeavesAUtcDateAlone`, `ANullableDate_KeepsNull_AndMarksAValueUtc` |
| UTC-03 | Every date column in the model, now and in future | Uses the UTC converter, so a new column cannot be missed | .NET | Automated: `EveryDateColumnInTheModel_UsesTheUtcConverter_SoNoneCanBeMissed` |
| UTC-04 | A new row's creation time | Comes from the app in UTC, not from the database server's clock or time zone, for all 7 tables with a `CreatedAt` | .NET | Automated: `NewRows_GetTheirCreationTimeFromTheApp_InUtc_NotFromTheDatabaseClock` |
| UTC-05 | A real browser in a non-UTC time zone (this machine is IST, UTC+5:30) | The same time live and after a reload; "Last seen today at 20:48" matches the real clock | Manual | Manual: `E2E-M13-11` (this failed before the fix, see finding F8) |

### 5.13 CONNECTION LIFECYCLE AND WHOLE-FEATURE CHECKS (Phase 14)

| ID | Test case (scenario) | Expected result | Layer | Status |
|---|---|---|---|---|
| CON-01 | The page is hidden for navigation or the back/forward cache (`pagehide`) | The browser closes its own socket at once, so the server sees the disconnect and shows the person offline (before this, a reload left them "Online" for up to 30 s) | Angular | `DirectHubService` spec: "closes its own socket when the page is hidden for navigation or the cache, so the server does not keep showing the person online" |
| CON-02 | The page returns from the back/forward cache (`pageshow` with `persisted`) | The connection is rebuilt | Angular | "reconnects when the browser restores the page from its back/forward cache" |
| CON-03 | An ordinary `pageshow`, or one after sign-out | No reconnect | Angular | "does not reconnect on an ordinary pageshow, or after the person signed out" |
| CON-04 | SignalR gives up after a long outage | The service builds a fresh connection itself and signals a resync, so lists are refetched | Angular | "builds a fresh connection itself when SignalR gives up after a long outage, and tells the state to resync" |
| CON-05 | The server stays down after SignalR gave up | Retries with a growing delay (1 s, 2 s, 4 s ... capped at 30 s) | Angular | "keeps retrying with a growing delay while the server stays down after SignalR gave up" |
| CON-06 | Sign-out, or the page being left, during an outage | No further retries | Angular | "does not retry after sign-out, or while the page is being left" |
| CON-07 | Real 130 s API outage with both people connected | Both recover on their own 15 s after the API answers; a live message gets through | Manual | Manual: outage script (section 12.4) |
| CON-08 | API restart (3 s) with both people connected | Reconnect within 12 s; messages, ticks and presence work again | Manual | Manual: `E2E-F-15` |
| CON-09 | Reload or close of a tab | The other person sees "Last seen ..." within a few seconds and `LastSeenAt` is saved | Manual | Manual: `E2E-F-10` (failed before the fix, finding F12) |
| A11Y-01 | axe-core WCAG 2 A/AA scan of the Messages screen, light and dark | No serious or critical violations (colour contrast of muted text and the green "Online" words) | Manual | Manual: `E2E-F-16` (54 serious nodes before the fix, finding F13) |
| FEAT-01 to FEAT-17 | Whole-feature walkthrough: find by name, ticks (one grey, two grey, blue), toast and badge, typing, hidden tab, two tabs, delete, last seen, 66-message paging, simultaneous start of the same chat, outsider and Admin 404, API restart, accessibility, no JavaScript errors | All pass | Manual | Manual: `E2E-F-01` to `E2E-F-17` (section 12.2) |

### 5.14 MESSAGE BROKERS: foundation and async password-reset email (Phases 15 and 17)

"Processor" = `PasswordResetEmailProcessor` (the worker's decisions, tested without a broker). The RabbitMQ glue (publish, consume, retry and dead-letter queues, reconnect) is verified against a **real RabbitMQ** in the Docker run (section 13); no unit test connects to a broker.

| ID | Test case (scenario) | Expected result | Layer | Status |
|---|---|---|---|---|
| QUE-01 | Send succeeds | Outcome `Sent`, the sender called once with the message's address, name and link | .NET | `PasswordResetEmailProcessorTests.Sends_AndReportsSent_WhenTheSenderSucceeds` |
| QUE-02 | Send fails, retries remain (first and second failure) | Outcome `Retry` with the configured delay (10 s, then 60 s) and the error text | .NET | `AskForARetryWithTheConfiguredDelay_WhenSendingFailsAndRetriesRemain` (2 cases) |
| QUE-03 | Send fails with no retries left, or none configured | Outcome `Dead` | .NET | `DeadLetters_WhenSendingFailsAndNoRetriesRemain`, `DeadLettersAtOnce_WhenNoRetriesAreConfigured` |
| QUE-04 | The link has expired (older than its lifetime) | Outcome `Expired`; the sender is never called | .NET | `DropsAMessageWhoseLinkHasExpired_WithoutSendingIt` |
| QUE-05 | Shutdown during a send | Cancellation propagates; it is not counted as a failed send | .NET | `PropagatesCancellation_RatherThanTreatingItAsAFailedSend` |
| QUE-06 | No broker host configured | `IEmailQueue` is the inline queue and no worker is registered | .NET | `EmailQueueWiringTests.WithoutABrokerHost_EmailIsSentInline_AndNoWorkerIsRegistered` |
| QUE-07 | Broker host configured | `IEmailQueue` is the RabbitMQ queue and the worker is registered; nothing connects at start-up | .NET | `WithABrokerHost_EmailIsQueued_AndTheWorkerIsRegistered_WithoutConnectingYet` |
| QUE-08 | Host set but username or password blank | The API refuses to start; the message names `RabbitMq__Password` | .NET | `ABrokerHostWithoutCredentials_FailsFastAtStartup_WithAClearMessage` (3 cases) |
| QUE-09 | `Email:Provider` | Default and `smtp` give the SMTP sender, `console` the console sender, anything else fails at start-up | .NET | `EmailProvider_PicksTheSender` (3 cases), `AnUnknownEmailProvider_FailsFast` |
| QUE-10 | Retry delays from configuration with an environment-style override | An override of one index replaces it and keeps the other (`[2, 60]`) | .NET | `RetryDelaysAreBoundFromConfiguration_AndAnEnvironmentOverrideReplacesByIndex` |
| QUE-11 | Retry queue names | Carry their delay (`email.retry.10s`) so changing the delays never clashes with an existing queue | .NET | `EmailTopologyTests.RetryQueueNamesCarryTheirDelay_...` |
| QUE-12 | Inline queue | Delivers through the sender | .NET | `InlineQueue_DeliversThroughTheSender` |
| QUE-13 | `ForgotPassword` | Queues the link only for an existing user; the same generic message for known, unknown and failing-queue cases | .NET | `AuthControllerTests`: `ForgotPassword_QueuesTheResetLink_ForKnownEmail`, `..._ForUnknownEmail_AndQueuesNothing`, `..._WhenSendingOrQueuingTheEmailFails_SoNoAccountIsRevealed`, `..._StillPropagatesCancellation_...` |
| FND-01 to FND-13 | Broker foundation on the Docker stack: healthy, not published, passwords enforced, no `guest`, topology durable, `/health` 200, worker consuming, register/login/401 unchanged | All pass | Manual | Manual: `P15-01` to `P15-13` (section 13.2) |
| QUE-E01 to QUE-E28 | Async email against a real RabbitMQ: generic answer, delivery and password reset, retries, dead letter, broker outage and recovery, broker and API restart, expiry, 40 at once, no broker, fail-fast | All pass | Manual | Manual: `P17-01` to `P17-28` (section 13.3) |
| REG-01 to REG-09 | Browser regression with the brokers running: register, AI reply, forgot-password page, wrong password, direct messages with live delivery and blue ticks, no JavaScript errors | All pass | Manual | Manual: `R-01` to `R-09` (section 13.4) |

### 5.15 REDIS: SignalR backplane and shared presence (Phase 16)

"Store" = `IPresenceStore` (the Redis operations behind an interface). The tracker's decisions and its fallback when Redis is down are tested with a fake store; the real Lua scripts, the real backplane and a real second replica are verified against a **real Redis and two real replicas** in the Docker run (section 14). No unit test connects to Redis.

| ID | Test case (scenario) | Expected result | Layer | Status |
|---|---|---|---|---|
| PRS-R01 | First connection reported by the shared store | `UserConnectedAsync` returns what the store says (false when the user already has a tab on another replica, true when first anywhere) | .NET | `RedisPresenceTrackerTests.UserConnected_ReportsWhatTheSharedStoreSays_NotJustThisServer` |
| PRS-R02 | What is passed to the store | Same user, connection, the clock's time and the configured lifetime | .NET | `UserConnected_PassesTheUserConnectionTimeAndTtlToTheStore` |
| PRS-R03 | Last connection reported by the shared store | `UserDisconnectedAsync` returns what the store says (false while another replica still holds a tab) | .NET | `UserDisconnected_ReportsWhatTheSharedStoreSays` |
| PRS-R04 | Online checks | Single and many-at-once checks are answered by the store | .NET | `OnlineChecks_AskTheSharedStore` |
| PRS-R05 | Redis down: connect and disconnect | Never throws; falls back to this server's own view (first tab here = came online, last tab here = went offline) | .NET | `WhenRedisIsDown_ConnectAndDisconnectFallBackToThisServersOwnView_AndNeverThrow` |
| PRS-R06 | Redis down: online checks | Never throws; answers from this server's own connections | .NET | `WhenRedisIsDown_OnlineChecksUseThisServersOwnView_AndNeverThrow` |
| PRS-R07 | Heartbeat | Sends exactly the connections this server holds (not ones already closed) with the configured lifetime | .NET | `Refresh_SendsEveryConnectionThisServerHolds_WithTheConfiguredTtl` |
| PRS-R08 | Heartbeat with nothing to refresh | No call to the store | .NET | `Refresh_DoesNothing_WhenThisServerHoldsNoConnections` |
| PRS-R09 | Heartbeat when Redis is down | Swallowed, so the timer loop keeps running | .NET | `Refresh_SwallowsAStoreFailure_SoTheHeartbeatLoopKeepsRunning` |
| PRS-R10 | Connections made during an outage | Re-created in Redis by the next heartbeat after Redis returns | .NET | `ConnectionsMadeDuringARedisOutage_AreRecreatedInRedisByTheNextRefresh` |
| PRS-R11 | Redis key naming | One key per user, `presence:user:{id}` | .NET | `RedisKeys_AreNamedPerUser` |
| PRS-R12 | Timing defaults | The heartbeat is at most a third of the lifetime, so a slow beat never makes a live user look offline | .NET | `PresenceTimingDefaults_KeepTheHeartbeatWellInsideTheTtl` |
| PRS-M01 | In-memory tracker, async API | Behaves exactly like the synchronous one (first/last connection, online) | .NET | `InMemoryPresenceTrackerAsyncTests.AsyncMethods_BehaveLikeTheSyncOnes` |
| PRS-M02 | In-memory "online among" | Returns only the online users; empty input gives empty output | .NET | `OnlineAmong_ReturnsOnlyTheOnlineUsers` |
| PRS-M03 | In-memory snapshot | Lists every live (user, connection) pair | .NET | `Snapshot_ListsEveryConnection` |
| PRS-M04 | In-memory heartbeat | No-op | .NET | `Refresh_IsANoOp` |
| PRS-W01 | No Redis configured | In-memory presence, no heartbeat service, default SignalR | .NET | `RealtimeWiringTests.WithoutRedis_PresenceIsInMemory_AndThereIsNoHeartbeat` |
| PRS-W02 | Redis configured | Shared presence store and tracker, heartbeat registered, nothing connects at start-up | .NET | `WithRedis_PresenceIsShared_AndTheHeartbeatIsRegistered_WithoutConnectingYet` |
| PRS-W03 | SignalR backplane | `RedisHubLifetimeManager` when Redis is set, `DefaultHubLifetimeManager` when not | .NET | `WithRedis_SignalRUsesTheRedisBackplane_AndWithoutRedisItDoesNot` |
| PRS-W04 | Malformed Redis connection string | Fails at start-up, not on the first message | .NET | `ABadRedisConnectionString_FailsAtStartup_NotOnTheFirstMessage` |
| PRS-X01 to PRS-X32 | Two real replicas with a real Redis: pushes cross replicas, shared presence, atomic first/last connection, heartbeat, replica kill, Redis stop/start/flush | All pass | Manual | Manual: `P16-01` to `P16-32` (section 14.2) |
| PRS-N01 to PRS-N04 | Negative control: Redis off, two replicas must NOT see each other | All behave as expected (broken across replicas, working on one) | Manual | Manual: `N-01` to `N-04` (section 14.3) |
| PRS-T01, PRS-T02 | The proxy with two replicas: negotiation fails sometimes, skipping it never | 14/30 against 30/30 | Manual | Manual: `X-01`, `X-02` (section 14.4) |
| PRS-B01 to PRS-B16 | Real browsers through nginx with `--scale api=2`: ticks, badge, typing, online, delete, reload, last seen, replica killed | All pass | Manual | Manual: `P16-B01` to `P16-B16` (section 14.5) |
| REG-R01 to REG-R08 | Browser regression on the standard stack (one api, Redis and RabbitMQ on) | All pass | Manual | Manual: `R-01` to `R-08` (section 14.6) |

### 5.16 ASYNC AI REPLIES (Phase 18)

"Processor" = `AiReplyProcessor` (the worker's decisions, no broker). The in-memory test store has no transactions, so "all or nothing" is proved on real MySQL in the Docker run (section 15).

| ID | Test case (scenario) | Expected result | Layer | Status |
|---|---|---|---|---|
| ASY-01 | Send with the flag on | `202` with `replyStatus: pending` and a job id; the question and a pending job are saved; the job id is queued; Python is not called | .NET | `AsyncReplyControllerTests.AsyncOn_Answers202_SavesTheQuestionAndAJob_QueuesTheJobId_AndNeverCallsPython` |
| ASY-02 | Untitled chat | Still titled from the question before the reply | .NET | `AsyncOn_StillTitlesAnUntitledChat_BeforeTheReply` |
| ASY-03 | Broker refuses the job | `503 ai_service_unavailable`; the question stays saved; the orphan job is removed | .NET | `AsyncOn_WhenTheBrokerRefusesTheJob_Answers503_KeepsTheQuestion_AndRemovesTheOrphanJob` |
| ASY-04 | Flag off, queue available | Old synchronous `200` with both messages; no job; queue untouched | .NET | `AsyncOff_StaysSynchronous_EvenWhenAQueueIsAvailable` |
| ASY-05 | Someone else's chat | `404`, nothing queued, no job | .NET | `AsyncOn_ForSomeoneElsesChat_Is404_AndQueuesNothing` |
| ASY-06 | Open jobs list | Pending and failed (not done) jobs, "processing" shown as pending, owner only | .NET | `OpenJobs_ListsPendingAndFailed_NotDone_AndOnlyForTheOwner` |
| ASY-07 | Retry a failed job | Back to pending with attempts reset, re-queued, no new question | .NET | `Retry_PutsAFailedJobBackInTheQueue_WithoutANewQuestion` |
| ASY-08 | Retry a pending job | Idempotent `202`; not queued twice | .NET | `Retry_OfAJobThatIsAlreadyPending_IsIdempotent_AndDoesNotQueueTwice` |
| ASY-09 | Retry a delivered reply | `409 reply_already_delivered` | .NET | `Retry_OfADeliveredReply_Is409` |
| ASY-10 | Retry someone else's job / unknown job / as Admin | `404`, `404`, allowed | .NET | `Retry_OfSomeoneElsesJob_Is404_ButAnAdminMayRetryAnyJob` |
| ASY-11 | Retry while the broker is down | `503`, job stays failed | .NET | `Retry_WhenTheBrokerRefuses_Is503_AndTheJobStaysFailed` |
| ASY-12 | Success | Reply, usage log and job saved; job done after 1 attempt and linked to the reply; `AssistantReplied` pushed once; AI called once with the question read from the database | .NET | `AiReplyProcessorTests.Success_SavesTheReplyAndUsage_MarksTheJobDone_AndPushesAssistantReplied` |
| ASY-13 | Duplicate delivery of a done job | No second AI call, reply, usage log or push | .NET | `ADuplicateDelivery_OfADoneJob_DoesNothing_NoSecondCallNoSecondReply` |
| ASY-14 | Job does not exist | Dropped (`Missing`) | .NET | `AJobThatDoesNotExist_IsDropped` |
| ASY-15 | AI fails, retries left | `Retry` with the configured delay (5 s, then 20 s); job pending; nobody told | .NET | `AFailedAiCall_WithRetriesLeft_ParksTheJobForTheConfiguredDelay_AndTellsNoOne` |
| ASY-16 | AI fails after the last retry | Job failed after 3 attempts; the user told exactly once; later deliveries ignored; AI called 3 times | .NET | `AfterTheLastRetry_TheJobFails_TheUserIsToldOnce_AndLaterDeliveriesAreIgnored` |
| ASY-17 | No retries configured | First failure is final | .NET | `WithNoRetriesConfigured_TheFirstFailureIsFinal` |
| ASY-18 | Job held by a live worker | `Deferred` for the lease; AI not called | .NET | `AJobHeldByALiveWorker_IsLeftAlone_AndLookedAtAgainAfterTheLease` |
| ASY-19 | Job abandoned by a dead worker | Taken over after the lease; attempts incremented | .NET | `AJobAbandonedByADeadWorker_IsTakenOverOnceTheLeaseHasPassed` |
| ASY-20 | Two workers race for one job | Exactly one wins; the loser never calls the AI | .NET | `TwoWorkersRacingForTheSameJob_OnlyOneWins_TheOtherStandsDown` |
| ASY-21 | Claim lost during the AI call | The late reply is discarded; the new owner's reply stands; the user is told once | .NET | `IfTheClaimIsLostDuringTheAiCall_TheLateReplyIsDiscarded_AndTheNewOwnersReplyStands` |
| ASY-22 | Context sent to the AI | Question read from the database; only the last 20 earlier messages, oldest first; nothing after the question | .NET | `TheQuestionIsReadFromTheDatabase_AndOnlyTheLast20EarlierMessagesAreSentAsContext_OldestFirst` |
| ASY-23 | Question deleted | Job fails at once, AI not called | .NET | `IfTheQuestionWasDeleted_TheJobFailsAtOnce_WithoutCallingTheAi` |
| ASY-24 | Unexpected error | Not swallowed (the worker parks the message); job left "processing" under its lease | .NET | `AnUnexpectedError_IsNotSwallowed_SoTheWorkerCanParkTheMessage` |
| ASY-25 | Flag on without a broker | API refuses to start naming `RabbitMq:Host` | .NET | `AsyncReplyInfrastructureTests.AsyncRepliesOn_WithoutABroker_FailsAtStartup_WithAClearMessage` |
| ASY-26 | Flag on with a broker | Queue, processor and worker registered | .NET | `AsyncRepliesOn_WithABroker_RegistersTheQueueTheProcessorAndTheWorker` |
| ASY-27 | Flag off with a broker | No queue and no worker | .NET | `AsyncRepliesOff_RegistersNoQueueAndNoWorker_EvenWithABroker` |
| ASY-28 | Queue message | Contains the job id and nothing else | .NET | `ReplyQueueMessage_CarriesTheJobIdAndNothingElse` |
| ASY-29 | Retry queue names | Named after their delay; the lease delay is included for deferred jobs | .NET | `RetryQueues_AreNamedAfterTheirDelay_AndIncludeTheLeaseForDeferredJobs` |
| ASY-30 | Push of a reply | `AssistantReplied` to `user-<id>` with the job and reply | .NET | `TheNotifier_SendsAssistantReplied_ToTheUsersGroup_WithTheJobAndTheReply` |
| ASY-31 | Push of a failure | `AssistantFailed` sent; a failing push never throws | .NET | `TheNotifier_SendsAssistantFailed_AndNeverThrowsWhenThePushFails` |
| ASY-32 | Shared history helper | Oldest first, excludes the question | .NET | `TheSharedHistoryHelper_ReturnsOldestFirst_AndExcludesTheQuestionItself` |
| ASY-A01 | 202: question stays, "Thinking…", composer locked | As stated | Angular | `chat-async.spec.ts`: "a 202 keeps the question on screen and shows "Thinking…" until the reply arrives, with the composer locked" |
| ASY-A02 | `AssistantReplied` | Reply appended, "Thinking…" gone, composer unlocked | Angular | "the AssistantReplied event adds the reply, clears "Thinking…" and unlocks the composer" |
| ASY-A03 | Same reply twice | Shown once | Angular | "the same reply delivered twice is shown once" |
| ASY-A04 | Reply for a chat that is not open | Not drawn here | Angular | "a reply for a chat that is not open is not drawn here ..." |
| ASY-A05 | Reply finishes while another chat is open | Pending state of its own chat still ends | Angular | "a reply that finishes while another chat is open still ends the pending state ..." |
| ASY-A06 | `AssistantFailed` | "The assistant couldn't reply." with Send again under the question | Angular | "the AssistantFailed event shows ... with Send again under the question" |
| ASY-A07 | Send again | Retries the same job, no new question, back to "Thinking…" | Angular | "Send again asks the server to retry the same job, with no new question, and goes back to "Thinking…"" |
| ASY-A08 | Retry refused (503) | Error shown, failed state kept | Angular | "a retry the server refuses (broker down) shows the error and keeps the failed state" |
| ASY-A09 | Reload with a pending job | "Thinking…" restored from the server | Angular | "opening a chat after a reload shows a reply that is still being produced" |
| ASY-A10 | No open jobs | Nothing extra shown | Angular | "a chat with no open jobs shows nothing extra" |
| ASY-A11 | Hub reconnect | Messages and jobs refetched; a missed reply appears | Angular | "after a reconnect it refetches messages and jobs, picking up a reply whose push was missed" |
| ASY-A12 | Safety-net poll | Looks every 15 s while a reply is outstanding, stops once delivered | Angular | "while a reply is outstanding it looks for a missed push every 15 seconds, and stops once it is delivered" |
| ASY-A13 | Synchronous 200 | Both messages at once, never pending | Angular | "a synchronous 200 (async mode off) still shows both messages at once ..." |
| ASY-A14 | Second question while pending | Not sent | Angular | "does not send a second question while the first reply is still being produced" |
| ASY-A15 | Hub service | `AssistantReplied` / `AssistantFailed` forwarded to their streams | Angular | `direct-hub.service.spec.ts`: "forwards the async-reply events (AssistantReplied, AssistantFailed) to their streams" |
| ASY-A16 | `ChatService` | Send (200 and 202 shapes, `isAccepted`), open jobs, retry, and the older endpoints | Angular | `chat.service.spec.ts` (5 tests) |
| ASY-E01 to ASY-E40 | End to end on the Docker stack: 202 timing, event on the other replica, retries, dead letter, manual retry, duplicate and bad messages, 40 at once over two replicas, replica killed, broker down, Redis down, flag off, flag on without a broker | All pass | Manual | Manual: `P18-01` to `P18-40` (section 15.2) |
| ASY-B01 to ASY-B17 | Real browsers: pending, replied, failed, Send again, reload, lost push, switching chats, two tabs, accessibility, phone width | All pass | Manual | Manual: `B18-01` to `B18-17` (section 15.5) |

### 5.17 DIRECT-CHAT EVENT FAN-OUT (Phase 19)

| ID | Test case (scenario) | Expected result | Layer | Status |
|---|---|---|---|---|
| OBX-01 | Send a message | One `direct.message.sent` row with the message id, conversation, author, other person; no text | .NET | `OutboxWritingTests.SendingAMessage_WritesASentEvent_WithIdsAndNoText` |
| OBX-02 | Send to someone online | `sent` then `delivered` events | .NET | `SendingToSomeoneOnline_WritesSentAndDeliveredEvents` |
| OBX-03 | Mark read | One `read` event; author is the peer, recipient is the reader; no text | .NET | `MarkingRead_WritesAReadEvent_AuthorIsThePeer_RecipientIsTheReader` |
| OBX-04 | Mark read with nothing unread | No event | .NET | `MarkingReadWhenNothingIsUnread_WritesNoEvent` |
| OBX-05 | Delete (twice) | One `deleted` event; no text | .NET | `DeletingAMessage_WritesADeletedEvent_OnlyOnce_EvenIfRepeated` |
| OBX-06 | Recipient connects later | One `delivered` event per sender and conversation | .NET | `ConnectingAfterMessagesWereSent_WritesOneDeliveredEvent_PerSenderAndConversation` |
| OBX-07 | Outbox off | No rows; the chat still works | .NET | `WithTheOutboxOff_NoRowsAreWritten_AndTheChatStillWorks` |
| OBX-08 | The change fails to save | No event written; the queued event is kept for a retry | .NET | `IfTheChangeFailsToSave_NoEventIsWritten_AndTheQueuedEventIsKeptForARetry` |
| OBX-09 | Two saves in a row | Each event written once | .NET | `QueuedEvents_AreWrittenOnce_NotAgainOnTheNextSave` |
| OBX-10 | Save with no queued events | Behaves as before | .NET | `APlainSave_WithNoQueuedEvents_BehavesAsBefore` |
| OBX-11 | Payload shape | Ids sorted; exact JSON shape `conversationId, messageIds, senderId, recipientId, status, at` | .NET | `EventPayloads_SortTheirIds_AndHaveTheAgreedJsonShape` |
| OBX-12 | Dispatch order | Pending rows published in id order and marked dispatched | .NET | `OutboxDispatcherTests.PublishesPendingRowsInIdOrder_AndMarksThemDispatched` |
| OBX-13 | Nothing to do | Nothing published | .NET | `NothingToDo_PublishesNothing` |
| OBX-14 | Batch size | Respected; the rest next round | .NET | `RespectsTheBatchSize_AndTakesTheRestNextRound` |
| OBX-15 | A publish fails | Round stops; row released and attempt counted; later rows not skipped ahead | .NET | `APublishThatFails_StopsTheRound_ReleasesTheRow_CountsTheAttempt_AndNeverSkipsAhead` |
| OBX-16 | Broker recovers | Next round publishes the rest, in order | .NET | `AfterTheBrokerRecovers_TheNextRoundPublishesTheRest_InOrder` |
| OBX-17 | Row claimed by a live replica | Skipped until its lease runs out, then taken | .NET | `ARowClaimedByALiveReplica_IsSkipped_UntilItsLeaseRunsOut` |
| OBX-18 | Already dispatched | Never published again | .NET | `AlreadyDispatchedRows_AreNeverPublishedAgain` |
| OBX-19 | Two replicas race for a row | Only one publishes it | .NET | `TwoReplicasRacingForTheSameRow_OnlyOnePublishesIt` |
| OBX-20 | Retention | Old dispatched rows deleted; recent and pending kept | .NET | `EventsPublishedBeforeTheRetentionPeriod_AreKept_AndOlderOnesAreDeletedByTheLoop` |
| OBX-21 | Subscriber handles a valid event | Handled and counted (meter, tagged by type) | .NET | `DirectEventHandlerTests.AValidEvent_IsHandled_AndCounted` |
| OBX-22 | Repeat of an event | Recognised by outbox id and ignored | .NET | `ARepeatedDelivery_IsRecognisedByItsOutboxId_AndIgnored` |
| OBX-23 | Repeat landing on another replica | Ignored when the memory is shared (as Redis does) | .NET | `TwoSubscribersSharingOneDeduplicator_HandleAnEventOnlyOnce_LikeTwoReplicasWithRedis` |
| OBX-24 | Bad events (6 cases: no type, empty type, no id, not JSON, empty conversation id, no message ids) | Rejected for the dead-letter queue | .NET | `AnEventMissingItsTypeIdOrPayload_IsRejected_ForTheDeadLetterQueue` (6 cases) |
| OBX-25 | Rejected event | Not remembered as seen | .NET | `AnInvalidEvent_IsNotRememberedAsSeen` |
| OBX-26 | Topology names | `direct.events`, `direct.events.log`, `direct.events.dead` | .NET | `TheTopologyUsesTheAgreedNames` |
| OBX-27 | In-memory de-duplication | First sighting once; bounded memory forgets the oldest | .NET | `EventDeduplicatorTests.InMemory_ReportsFirstSightingOnce`, `InMemory_IsBounded_ForgettingTheOldestFirst` |
| OBX-28 | Redis de-duplication | Fails open when Redis is down; keys namespaced per event | .NET | `Redis_FailsOpen_WhenRedisIsDown_SoAnEventIsNeverLostToTheDeduplicator`, `Redis_KeysAreNamespaced_AndEachEventHasItsOwn` |
| OBX-29 | Wiring with a broker | Writer, dispatcher, subscriber and publisher registered | .NET | `OutboxWiringTests.WithABroker_TheOutboxWriter_Dispatcher_AndSubscriber_AreRegistered` |
| OBX-30 | Wiring without a broker, or with the outbox off | No-op writer, nothing runs | .NET | `WithoutABroker_NothingIsWritten_AndNoDispatcherRuns`, `WithABroker_ButTheOutboxSwitchedOff_NothingIsWritten` |
| OBX-31 | Defaults | Lease far longer than one poll; retention at least an hour | .NET | `TheOutboxDefaults_AreSensible` |
| OBX-E01 to OBX-E30 | End to end on the Docker stack: written with the change, ordered, no text, atomic on real MySQL, broker outage, two replicas, replica killed, bad events, retention, switches | All pass | Manual | Manual: `P19-01` to `P19-30` (section 15.3) |
| REG-A01 to REG-A10, REG-S01 to REG-S10 | Browser regression with the real AI service, async on and off | All pass | Manual | Manual: `R-01` to `R-10` (section 15.6) |

### 5.18 BROKER DEADLINE AND GAP-CLOSING CHECKS (after Phases 18 and 19)

| ID | Test case (scenario) | Expected result | Layer | Status |
|---|---|---|---|---|
| BHL-01 | A broker call that never answers | Becomes `BrokerUnavailableException` after the deadline; not a cancellation | .NET | `BrokerTimeoutTests.ABrokerCallThatNeverAnswers_BecomesBrokerUnavailable_NotACancellation` |
| BHL-02 | The caller cancels | An ordinary cancellation still comes through | .NET | `ACallerWhoCancels_StillGetsAnOrdinaryCancellation` |
| BHL-03 | The library times out | Also reported as broker unavailable | .NET | `ALibraryTimeout_IsAlsoReportedAsBrokerUnavailable` |
| BHL-04 | A fast call, and other errors | Runs normally; other errors pass through unchanged | .NET | `AFastCall_RunsNormally_AndOtherErrorsPassThroughUnchanged` |
| BHL-05 | Default deadline | Between 1 and 10 s | .NET | `TheDefaultTimeout_IsShortEnoughToAnswerARequestPromptly` |
| BHL-E01 to BHL-E24 | Black-holed RabbitMQ and Redis, three replicas, 3,000-event backlog, AI call slower than the API timeout | All pass | Manual | Manual: `P20-01` to `P20-24` (sections 16.2 and 16.3) |
| BHL-M01 | After the deadline change, a password-reset email is still published and consumed by the worker | Generic 200; first send attempt logged | Manual | Manual: `P20-EM` (below) |
| BHL-F01 to BHL-F07 | Real Python service and real provider: invalid key (async and default mode), then the right key | All pass | Manual | Manual: `P20F-01` to `P20F-07` (section 16.4) |

### 5.19 THE COMMITTED END-TO-END SUITES (`e2e/`, Phase 20)

The suites are listed with every check in section 17. They run with `./e2e/run.sh` (all, about 80 minutes) or `./e2e/run.sh 04 09` (some). New checks only need a new `NN-name.mjs` file or a `check(...)` call; see `e2e/README.md`.

| Suite | Layer | Status |
|---|---|---|
| `e2e/suites/01-foundation.mjs`: Phase 15: broker foundation (16 checks) | Docker, real services | Built, passing (section 17.3) |
| `e2e/suites/02-email-queue.mjs`: Phase 17: password-reset email through RabbitMQ (30 checks) | Docker, real services | Built, passing (section 17.4) |
| `e2e/suites/03-presence-and-backplane.mjs`: Phase 16: Redis backplane and shared presence (and the negative control) (38 checks) | Docker, real services | Built, passing (section 17.5) |
| `e2e/suites/04-direct-chat-browser.mjs`: Phases 9 to 14: the whole user-to-user chat in real browsers across replicas (19 checks) | Docker, real services | Built, passing (section 17.6) |
| `e2e/suites/05-async-replies.mjs`: Phase 18: async AI replies (40 checks) | Docker, real services | Built, passing (section 17.7) |
| `e2e/suites/06-outbox-events.mjs`: Phase 19: direct-chat events through the outbox (30 checks) | Docker, real services | Built, passing (section 17.8) |
| `e2e/suites/07-black-hole-and-scale.mjs`: After 18 and 19: black-holed broker and Redis, three replicas, 3,000-event backlog, slow AI (24 checks) | Docker, real services | Built, passing (section 17.9) |
| `e2e/suites/08-async-replies-browser.mjs`: Phase 18: async replies in the chat page (17 checks) | Docker, real services | Built, passing (section 17.10) |
| `e2e/suites/09-chaos.mjs`: Phase 20: combined and sequential failures with consistency invariants (20 checks) | Docker, real services | Built, passing (section 17.11) |
| `e2e/suites/10a-real-llm-failure.mjs`: Real Python service and provider: invalid key, then the right key (7 checks) | Docker, real services | Built, passing (section 17.12) |
| `e2e/suites/10b-regression-sync.mjs`: Regression with the real AI service, default (synchronous) mode (10 checks) | Docker, real services | Built, passing (section 17.13) |
| `e2e/suites/10b-regression-async.mjs`: Regression with the real AI service, Chat:AsyncReplies on (10 checks) | Docker, real services | Built, passing (section 17.14) |

## 6. Coverage gaps and the phases that close them

| Phase | Gap | New tests |
|---|---|---|
| T2 (done) | .NET: admin usage, token service, Python client, validators, exception middleware, `401`/`403`/`404`/`/health` pipeline behaviour, SMTP fail-fast, history window | `AdminControllerTests`, `TokenServiceTests`, `PythonAiClientTests`, `ValidatorTests`, `ExceptionHandlingMiddlewareTests`, `ErrorShapeIntegrationTests` (adds the test-only package `Microsoft.AspNetCore.Mvc.Testing`) |
| T3 | Python: malformed bodies (`422`) and invalid history role | Added to `ai-service-python/tests/` |
| T4 | Angular: guards, `jwt.ts`, `extractErrorMessage`, `ChatService`, `AdminService`, `AppTitleStrategy`, chat send/retry/`503`/title flows, usage paging, login/register error mapping | New specs next to their sources |
| T5 | Close-out | Statuses in this file changed from *Planned* to real test names; before/after counts; mutation spot-check; full run |

## 7. Planned test cases for features not built yet

The last rows (`LIM-01` to `LIM-10`) are test cases for the known limits and improvements listed in `README.md` ("Known limits and improvement backlog"); the first column says what the behaviour is today and which existing check records it.

These rows are filled in (and their test names recorded) when the feature phase is built. Phases are built in the order given by the Roadmap in `README.md` (next up: Phase 21), not in number order; rows marked *because Phase N is built first* are obligations that a later phase picks up. Source plans in `README.md`: "User-to-user chat (Phases 9-14, done)", "Message brokers (Phases 15-20)" and "Hardening and product gaps (Phases 21-28)".

| ID | Scenario | Expected result | Layer | Status |
|---|---|---|---|---|
| BRK-01 | Presence and push across two API replicas | Event reaches a user connected to the other replica | Manual | Built in Phase 16, see 5.15 (`PRS-*`) and section 14 |
| BRK-02 | Forgot password publishes an email job only for existing users | Same generic response either way; retries; dead-letter after the last failure | .NET | Built in Phase 17, see 5.14 (`QUE-*`) |
| BRK-03 | Async AI reply (`Chat:AsyncReplies` on) | `202` then `AssistantReplied`; a redelivered job is a no-op; failure then retry then `AssistantFailed`; broker down gives `503` with the user message kept | .NET + Angular | Built in Phase 18, see 5.16 (`ASY-*`) and section 15 |
| BRK-04 | Direct-chat events | Written in the same transaction as the change; payload never contains message text | .NET | Built in Phase 19, see 5.17 (`OBX-*`) and section 15 |
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
| ACC-06 | Delete account for a user who has direct conversations | Their conversations and messages are removed or anonymised | .NET | Planned (Phase 22). The foreign keys already cascade (verified on real MySQL in Phase 10); this row covers the delete-account feature itself |
| SEC-06 | The CSP allows the `/hubs/` WebSocket | The hub connects in the browser with no CSP violation | Manual | Planned (Phase 11, because Phase 21 is built first) |
| COST-04 | Usage page cost | Shows a currency value instead of "Not tracked" once cost exists | Angular | Planned (Phase 26) |
| OPS-06 | `/health/ready` | `200` when the database is reachable, `503` when it is not | .NET | Planned (Phase 27) |
| OPS-07 | Correlation id | Each request has an id in the logs and the response header | .NET | Planned (Phase 27) |
| OPS-08 | Backup and restore | The script produces a dump that restores into an empty database | Manual | Planned (Phase 27) |
| OPS-09 | Usage-log retention | Rows older than the configured age are purged; newer rows kept | .NET | Planned (Phase 27) |
| A11Y-01 | Unknown URL | A real 404 page with a link home (no silent redirect) | Angular | Planned (Phase 28) |
| A11Y-02 | Automated accessibility scan (axe-core) on login, chat and usage pages | No serious or critical violations | Angular | Planned (Phase 28) |
| LIM-01 | MySQL down (L-01) | A database connection failure answers `503 service_unavailable` in the standard error shape, and the chat page shows "try again shortly" (today: `500 internal_error`) | .NET + `e2e/` | Planned (backlog L-01); today's behaviour is checked by `X-13` |
| LIM-02 | Reset email while RabbitMQ is down (L-02) | The email is queued in the outbox with the reset token and delivered after the broker returns; the answer stays the same generic `200` (today: that email is not sent) | .NET + `e2e/` | Planned (backlog L-02); today's behaviour is checked by `E-13`, `E-14` and `P20-03` |
| LIM-03 | Outbox throughput (L-03) | At least 200 events/s across three replicas; a 3,000-event backlog drains in under 20 s (today: about 85 s) | `e2e/` | Planned (backlog L-03); today's figure is recorded by `P20-21` |
| LIM-04 | Crashed replica (L-04) | Within the mark lifetime plus one sweep the person is announced offline and their last-seen time is saved (today: neither) | .NET + `e2e/` | Planned (backlog L-04); today's behaviour is recorded by `P-21` |
| LIM-05 | Redis down (L-05) | Same-replica pushes still arrive, or an alert fires (today: pushes stop; the page recovers within about 15 s) | .NET + `e2e/` | Planned (backlog L-05); today's behaviour is checked by `P-24` to `P-29` and `B18-11` |
| LIM-06 | Event order with several replicas (L-06) | Events of one conversation reach RabbitMQ in id order whichever replica dispatched them | `e2e/` | Planned (backlog L-06); today only one replica is ordered (`P19-04`) |
| LIM-07 | Several pending replies per chat (L-07) | Two questions can be pending; replies arrive in the order asked | Angular + `e2e/` | Planned (backlog L-07); today's lock is checked by `ASY-A14` |
| LIM-08 | High availability (L-08) | Killing one RabbitMQ or Redis node loses no message and the app keeps working | `e2e/` | Planned (backlog L-08) |
| LIM-09 | Wider coverage (L-09) | 5 replicas, a 30,000-event backlog, a one-hour soak with periodic failures, Firefox and WebKit, real SMTP | `e2e/` | Planned (backlog L-09) |
| LIM-10 | Restoring an older database (L-10) | Events with reused outbox ids are not ignored after a restore | .NET + `e2e/` | Planned (backlog L-10) |

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

## 9. Latest full-application test run (2026-10-20)

Run after Phase 11 (SignalR, delivered and read ticks), against the **real Docker stack** (MySQL 8.4, migration bundle, .NET API, Python service, nginx + Angular), with a **real DeepInfra LLM call**, real WebSockets through nginx, and a real headless Chrome. Throwaway accounts only; every test row was deleted afterwards (users, chats, messages, roles), the schema and the 2 seeded roles were kept, and the stack was stopped. The host dev MySQL was not touched.

### 9.1 Automated suites (re-run after the F1 fix)

| Suite | Result |
|---|---|
| .NET (`dotnet test`) | **95 passed, 0 failed** (26 before Phase 10 + 35 Phase 10 + 26 Phase 11 + 8 for the F1 fix) |
| Python (`pytest`) | **12 passed, 0 failed** |
| Angular unit (`ng test`, ChromeHeadless) | **56 passed, 0 failed** |
| Angular production build | Succeeded, no warnings |
| **Total** | **163 passed, 0 failed** |

### 9.2 End-to-end checks against the running stack: API, AI and realtime (37/37 passed)

**Platform**

| ID | Check | Result | Evidence |
|---|---|---|---|
| E2E-PLAT-01 | Web app is served (index.html) through nginx | PASS | HTTP 200 |
| E2E-PLAT-02 | SPA deep link falls back to index.html | PASS | HTTP 200 |

**Errors**

| ID | Check | Result | Evidence |
|---|---|---|---|
| E2E-ERR-01 | Unauthenticated protected call returns 401 in the { error: { code, message } } shape | PASS | 401 unauthorized |
| E2E-ERR-02 | Unknown API route returns 404 in the standard shape | PASS | 404 not_found |

**Auth**

| ID | Check | Result | Evidence |
|---|---|---|---|
| E2E-AUTH-01 | Register three users (Alice, Bob, Carol) | PASS | statuses 201,201,201 |
| E2E-AUTH-02 | Duplicate email is rejected with 409 email_taken | PASS | 409 |
| E2E-AUTH-03 | Weak password is rejected with 400 | PASS | 400 |
| E2E-AUTH-04 | Login with the right password returns a token | PASS | 200 |
| E2E-AUTH-05 | Login with a wrong password returns 401 | PASS | 401 |
| E2E-AUTH-06 | GET /auth/me returns the caller from the token | PASS | e2e.alice.1792480460255@example.test |
| E2E-AUTH-07 | Forgot password gives the same generic answer for a known and an unknown email | PASS | 200 + identical message (no user enumeration) |
| E2E-AUTH-08 | Reset password with an invalid token returns 400 | PASS | 400 |

**AI chat**

| ID | Check | Result | Evidence |
|---|---|---|---|
| E2E-CHAT-01 | Create a chat session ("New chat") | PASS | id 62894b75-a77c-48ec-9356-0c861fd85df8 |
| E2E-CHAT-02 | Send a message and get a REAL LLM reply (DeepInfra) through Python | PASS | HTTP 200 in 1346 ms, reply: "Table" |
| E2E-CHAT-03 | Both messages are stored and the chat is auto-titled from the first message | PASS | 2 messages, title "Reply with exactly one word: pong" |
| E2E-CHAT-04 | Another user cannot read someone else's chat (404, not 403) | PASS | 404 |
| E2E-CHAT-05 | Empty message is rejected with 400 | PASS | 400 |

**Admin**

| ID | Check | Result | Evidence |
|---|---|---|---|
| E2E-ADM-01 | A normal user gets 403 on the usage endpoint | PASS | 403 forbidden |
| E2E-ADM-02 | After the Admin role is granted (SQL, as in the README) and a fresh login, usage shows the chat call | PASS | totalCount 1, tokens 173, user Alice Rao |
| E2E-ADM-03 | Date filter with from later than to returns 400 | PASS | 400 |
| E2E-ADM-04 | Page size is capped at 100 | PASS | pageSize 100 |

**Direct chat**

| ID | Check | Result | Evidence |
|---|---|---|---|
| E2E-DM-01 | People search: name only, no email, never yourself | PASS | fields: id,name only; email never matches |
| E2E-DM-02 | Start a conversation (201) and reopen it from the other side (200, same id) | PASS | idempotent |
| E2E-DM-03 | Chat with yourself is 400; an unknown user is 404 | PASS | 400 / 404 |
| E2E-DM-04 | Privacy: an outsider and an ADMIN (Alice) both get 404 on a conversation they are not in | PASS | 404 x4 (admin has no special access) |
| E2E-DM-05 | Message validation: empty and over-8000-character content are rejected | PASS | 400 / 400 |
| E2E-DM-06 | Hub rejects a connection without a token (401) | PASS | 401 |

**Realtime**

| ID | Check | Result | Evidence |
|---|---|---|---|
| E2E-RT-01 | Alice connects to /hubs/direct over a real WebSocket (through nginx) | PASS | connected, transport WebSockets |
| E2E-RT-02 | ONE grey tick: Alice sends while Bob is offline -> status "sent" | PASS | status sent, peerOnline=false |
| E2E-RT-03 | TWO grey ticks: when Bob connects, Alice is told m1 is now "delivered" | PASS | pushed + persisted as delivered |
| E2E-RT-04 | Online peer: Alice sends m2 -> status "delivered" at once, Bob receives it live | PASS | delivered immediately; MessageReceived pushed |
| E2E-RT-05 | Presence: Alice sees Bob online while he is connected | PASS | peerOnline=true; Bob unread=2 |
| E2E-RT-06 | TWO BLUE ticks: Bob reads -> Alice told "read" for m1 and m2; Bob's other tab clears unread | PASS | read pushed to sender; MessagesRead pushed to reader's other tab; unread=0 |
| E2E-RT-07 | Reading again is a no-op (readCount 0) and pushes nothing | PASS | readCount 0, no event |
| E2E-RT-08 | Reverse direction: Bob sends m3 to online Alice -> delivered and pushed | PASS | delivered + pushed |
| E2E-RT-09 | Presence/last seen: Bob stays online with one tab left; offline only after the last tab; lastSeenAt set | PASS | offline after last tab; lastSeenAt 2026-10-20T07:14:30.342205 |
| E2E-RT-10 | Deliver-on-reconnect works for messages sent while offline (Bob offline -> Alice sends -> Bob reconnects) | PASS | delivered on reconnect |

### 9.3 End-to-end checks in a real browser: UI (14/14 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| E2E-UI-01 | Register page: create an account through the form and land on the chat page | PASS | redirected to / |
| E2E-UI-02 | Login page: wrong password shows an inline error, no navigation | PASS | inline error: Email or password is incorrect. |
| E2E-UI-03 | Chat page: top bar, pinned Book appointment row at the top, round "+" new-chat button | PASS | plus button 36x36, round, aria-label "New chat" |
| E2E-UI-04 | Chat: "+" creates a new empty chat, opens it (?session=<id>), and adds a "New chat" row | PASS | POST 201; rows 0->1; opened empty "New chat" |
| E2E-UI-05 | Chat: send a message in the UI and see a REAL AI reply rendered as markdown | PASS | assistant reply rendered (markdown) after sending from the composer |
| E2E-UI-06 | Chat: the sidebar title updates from the first message and groups under Today | PASS | "Give me a two item bullet list about fruit. Use markdown…" under Today |
| E2E-UI-07 | Chat: Copy button on a reply shows "Copied" | PASS | label "Copied" |
| E2E-UI-08 | Pinned "Book appointment" row opens the placeholder page; top bar has Log out + theme toggle | PASS | heading "Doctor appointments are coming soon"; buttons: Dark theme, Log out |
| E2E-UI-09 | Dark theme toggle switches the theme and survives a reload | PASS | data-theme=dark before and after reload |
| E2E-UI-10 | Reload keeps the user logged in; Log out returns to /login and protects / | PASS | session persisted across reload; logout + guard redirect OK |
| E2E-UI-11 | Admin: "Usage logs" link is visible for an Admin and the usage page lists the AI calls | PASS | 2 usage rows; stat cards 2 / 370 |
| E2E-UI-12 | Admin: a non-admin is redirected away from /admin/usage | PASS | redirected to / |
| E2E-UI-13 | Mobile (390 px): no horizontal scroll on login, chat and appointments; drawer opens | PASS | no overflow; drawer opened |
| E2E-UI-14 | No uncaught JavaScript errors on any page during the whole run | PASS | 0 uncaught page errors |

Server log check for this run: no unhandled exceptions; nginx logged 4 WebSocket upgrades (`101`) and the expected `401` for a token-less negotiate; the only `fail:` line is the handled, logged forgot-password send failure (SMTP is unset in this environment); the only warnings are the two start-up warnings in F3.

### 9.4 Findings

| # | Severity | Finding | Evidence and cause | Suggested fix |
|---|---|---|---|---|
| F1 | **Medium (real defect, predates Phase 11). FIXED, see 9.6** | `POST /api/auth/forgot-password` returns **500** for a registered email when the email cannot be sent, but 200 for an unknown email. Besides breaking the feature, this **reveals which emails have accounts**, defeating the "same generic answer" rule. Failed `E2E-AUTH-07`. | The Docker stack has no SMTP settings, and compose passes them as empty strings (`Smtp__Username: ${SMTP_USERNAME:-}`). `SmtpEmailSender` treats an empty string as configured, tries Gmail, and Gmail rejects it (`5.7.8 Username and Password not accepted`). `AuthController.ForgotPassword` does not catch the send failure. | Treat blank `Smtp:Username`/`Smtp:Password` as missing (fail fast with a clear log), and catch send failures in `ForgotPassword`: log the error and still return the generic message. Phase 17 (async emails) also removes the coupling. Add a unit test where `IEmailSender` throws. |
| F2 | Low (behaviour since Phase 6) | Every click on the new-chat "+" immediately creates a database row ("New chat") and opens it, even if the user never types. Empty chats accumulate. | Observed: one click gave `POST /api/chat/sessions` 201 and a second sidebar row. (My earlier message that "+" shows an empty new-chat screen was inaccurate for the real app.) | Phase 24 (chat management): create the chat on the first message, or let users delete empty chats. |
| F3 | Info | The API logs two start-up warnings in containers: HTTPS redirection cannot find an HTTPS port, and Data Protection keys are stored in a non-persistent folder. | Both appear on every start. The app does not use cookies or anti-forgery, so Data Protection has no effect today. | HTTPS warning: Phase 21. Data Protection: persist keys or switch the middleware off if it is not needed. |
| F4 | Limitation (by design, Phase 11) | A sender's **other tabs** do not get a live copy of a message sent from one tab (only the recipient is pushed `MessageReceived`). | Design choice in `DirectChatController.SendMessage`. | Phase 12: reload the conversation on focus, or push a `MessageSent` event to the sender's group. |

### 9.5 Not covered by this run

- **Direct-chat UI** (conversation list, thread, tick icons, unread badge, toast): not built until Phase 12. The realtime behaviour was tested with a real SignalR client instead.
- **Real forgot-password email and the reset-password happy path:** no SMTP credentials in this environment (see F1). The unit tests cover the reset logic.
- **Typing indicator, delete message, presence events:** Phase 13.
- **Rate limiting, lockout, refresh tokens, streaming, cost, admin user management, brokers:** planned, not built.
- Load, security and penetration testing are out of scope (section 1).

### 9.6 Follow-up: F1 fixed and re-verified

The **first run** of this section found 36/37 API checks (`E2E-AUTH-07` failed) and 12/13 UI checks (`E2E-UI-04` had a wrong expectation about "+", corrected; an empty "no JavaScript errors" check was replaced by a real one). F1 (forgot-password `500` for registered emails when SMTP fails) was then fixed, and the **whole run above was repeated against a freshly rebuilt stack: 37/37 and 14/14, plus all 163 automated tests.** What the fix changed:
- `SmtpEmailSender` now treats a blank, whitespace or missing `Smtp:Username` / `Smtp:Password` as not configured and fails fast with a clear message (no Gmail login attempt).
- `AuthController.ForgotPassword` catches a failed send, logs it by user id, and returns the same generic `200` as for an unknown email. A cancelled request still cancels.
- 8 new .NET tests (`AUTH-23`, `ERR-06`): .NET is now **95 passed**, 163 automated tests overall.
- Verified on the real Docker stack (rebuilt, SMTP still unset): registered email `200`, unknown email `200`, **identical bodies**; the API log shows `Could not send the password reset email for user <id>` with `Smtp:Username is not configured`. All throwaway users and data were deleted and the stack stopped afterwards.
- Still true: with no SMTP credentials no email is sent (`E2E-AUTH-07` now passes, but the real email and the reset-password happy path still need real credentials to test). The remaining enumeration paths, the registration `409` and the slower response for known emails, are Phase 22 and Phase 17.

## 10. Phase 12 test run (2026-10-21)

Run after Phase 12 (the Messages screen with ticks, unread badge and toast) against the **real Docker stack** (MySQL, migration, .NET API, Python service, nginx + Angular), with **two real Chrome browsers acting as two people** (Alice and Bob, separate profiles, real WebSockets through nginx) and a **real DeepInfra LLM call**. Throwaway accounts only; all test data was deleted afterwards (users, chats, messages, roles) and the stack stopped. The host dev MySQL was not touched. The backend did not change in Phase 12, so the API-level realtime checks of section 9 were not repeated; the browser run below exercises the same hub end to end.

### 10.1 Automated suites

| Suite | Result |
|---|---|
| .NET (`dotnet test`) | **95 passed, 0 failed** (unchanged) |
| Python (`pytest`) | **12 passed, 0 failed** (unchanged) |
| Angular unit (`ng test`, ChromeHeadless) | **131 passed, 0 failed** (56 before + 75 new in Phase 12) |
| Angular production build | Succeeded, no warnings. Initial bundle 323.7 kB; the Messages page is its own 15.9 kB chunk and the SignalR client loads on demand after sign-in, not in the initial bundle |
| **Total** | **238 passed, 0 failed** |

Phase 12 changed these Angular specs: new `ticks` (5), `direct-chat.service` (6), `direct-hub.service` (12), `direct-state.service` (25), `messages.component` (21); extended `top-nav` (5 to 9) and `app.component` (1 to 3).

### 10.2 Two browsers, one conversation (17/17 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| E2E-M12-01 | Messages tab is in the top bar after sign-in, with no badge yet | PASS | tabs: Chatbot \| Messages |
| E2E-M12-02 | Messages page opens from the tab and says there are no conversations yet | PASS | /messages, empty list hint shown |
| E2E-M12-03 | Search by name finds Bob (name only), and clicking him opens a new empty conversation | PASS | result "BBob Tester"; thread opened at ?c=a0d0dbf4-… |
| E2E-M12-04 | ONE grey tick: Alice sends while Bob is offline -> bubble with a single sent tick | PASS | 1 bubble, tick status=sent, 1 tick shape |
| E2E-M12-05 | TWO grey ticks: Bob signs in (anywhere in the app) -> Alice's tick becomes delivered live, with no refresh | PASS | status=delivered, 2 tick shapes, still grey |
| E2E-M12-06 | Bob sees an unread badge on the Messages tab while on the CHATBOT page (app-wide connection) | PASS | badge "1" on the Messages tab, Bob is on the chatbot page |
| E2E-M12-07 | A new message pops a toast with the sender and a preview, and bumps the badge to 2 | PASS | toast "Alice Tester: ping from Alice", badge 2 |
| E2E-M12-08 | Bob opens Messages: the conversation shows 2 unread; opening it clears the badge | PASS | list showed 2 unread; after opening: badge and list badge gone |
| E2E-M12-09 | TWO BLUE ticks: Alice's ticks flip to read live when Bob opens the conversation | PASS | 2 ticks, status=read, blue stroke rgb(2, 132, 199) (the --tick-read token) |
| E2E-M12-10 | Live both ways: Bob replies; it appears on Alice's open thread at once with NO toast, and Bob's tick turns blue because Alice has it open | PASS | reply shown live, no toast, Bob's tick status=read |
| E2E-M12-11 | After a reload the thread and every tick state come back from the server | PASS | 3 messages restored; Bob's tick still read |
| E2E-M12-12 | The Messages list shows the last message, newest first, with no ticks when the last message is the other person's | PASS | "yes, I am here", no unread, no ticks |
| E2E-M12-13 | Dark theme: the read-tick colour switches to the dark token (screenshot taken) | PASS | dark read-tick colour rgb(56, 189, 248) |
| E2E-M12-14 | Mobile (390 px): list first, tapping opens the thread, "Chats" goes back; no horizontal scroll | PASS | list -> thread -> back works, no overflow |
| E2E-M12-15 | Sign out stops the live connection and forgets the chats | PASS | back at /login, no leftover chat text |
| E2E-M12-16 | The AI chatbot page is unaffected (sidebar, pinned Book appointment, "+") | PASS | chat page intact |
| E2E-M12-17 | No uncaught JavaScript errors in either browser during the whole run | PASS | 0 uncaught page errors |

### 10.3 Regression of the older features in a real browser (11/11 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| E2E-R-01 | Register through the form and land on the chat page with the new top bar | PASS | tabs Chatbot \| Messages |
| E2E-R-02 | AI chat: send a message in the UI and get a REAL LLM reply rendered as markdown | PASS | reply rendered (68 chars of HTML) |
| E2E-R-03 | AI chat: the chat is auto-titled, grouped under Today, and "+" creates a new empty chat | PASS | title "Reply with a short markdown list of two colours.", rows 1 -> 2 |
| E2E-R-04 | Pinned "Book appointment" opens the placeholder; its top bar has Messages with no badge, theme toggle and Log out | PASS | buttons: Dark theme, Log out |
| E2E-R-05 | Messages tab opens the Messages page from another page, and the page says there are no conversations | PASS | /messages |
| E2E-R-06 | Theme toggle works and is remembered after a reload | PASS | dark, remembered across reload |
| E2E-R-07 | Admin: after the Admin role is granted (SQL as in the README) the Usage logs link and table work | PASS | Re-run after fixing a quoting bug in the test script: roles Admin,User, link "Usage logs", 1 usage row(s), stat cards 1/184, usage top bar present |
| E2E-R-08 | A non-admin is redirected away from /admin/usage | PASS | redirected to / |
| E2E-R-09 | Login page: a wrong password shows an inline error and stays on /login | PASS | inline error: Email or password is incorrect. |
| E2E-R-10 | Forgot-password page: submitting shows the generic confirmation (SMTP is not configured here, and the page must not break) | PASS | generic message shown, no error |
| E2E-R-11 | No uncaught JavaScript errors during the whole regression run | PASS | 0 uncaught page errors |

`E2E-R-07` failed on its first attempt because of a shell-quoting bug in my test script (the email address in the SQL), not because of the app; after fixing the script it was re-run on the same data and passed, as recorded above.

### 10.4 Findings

| # | Severity | Finding | Evidence and cause | Suggested fix |
|---|---|---|---|---|
| F5 | Fixed during the phase | A duplicate delivery of the same message for a conversation whose thread was not loaded still showed a **second toast** (the unread count was right). | Caught by the unit test `ignores a duplicate delivery of the same message`; `DirectStateService.onMessageReceived` now drops a message equal to the conversation's last message. | None needed (fixed and tested). |
| F4 | Mitigated (see 9.4) | A sender's other tabs do not receive a live copy of a message sent from one tab. | The tab resyncs the list and open thread when it becomes visible again and after a reconnect. | A `MessageSent` push to the sender's group if instant sync across tabs is wanted. |
| F6 | Resolved in Phase 13 | Presence (online dot, last seen) and the typing indicator were not shown yet. | Built in Phase 13 (`PeerPresenceChanged`, `Typing`). | None needed. |
| F7 | Info | Toasts have no click-through to the conversation. | `ToastService` shows text only. | Optional: make a message toast a link to `/messages?c=<id>`. |

### 10.5 Not covered by this run

- **Delete message, typing, online/last seen:** built in Phase 13 (section 11).
- **Hidden-tab behaviour in a real browser:** covered by unit tests (visibility is faked); headless Chrome could not simulate a background tab here.
- **Real password-reset emails:** no SMTP credentials in this environment.
- **Very large conversations** (the 50-message page and "Show earlier messages") were unit-tested but not exercised against a real conversation of more than 50 messages.

## 11. Phase 13 test run (2026-10-21)

Run after Phase 13 (typing indicator, online / last seen, delete message) against the **real Docker stack** (MySQL, migration, .NET API, Python service, nginx + Angular), with **two real Chrome browsers acting as two people** (separate profiles, real WebSockets through nginx), a real DeepInfra LLM call in the regression run, and typing signals counted from the real WebSocket frames. Throwaway accounts only; all test data was deleted afterwards and the stack stopped. The host dev MySQL was not touched. No database migration was needed in Phase 13 (`dotnet ef migrations has-pending-model-changes`: no changes).

### 11.1 Automated suites

| Suite | Result |
|---|---|
| .NET (`dotnet test`) | **122 passed, 0 failed** (95 before + 27 new: delete 8, hub typing 5, presence broadcasts 4, push events 4, UTC dates 6) |
| Python (`pytest`) | **12 passed, 0 failed** (unchanged) |
| Angular unit (`ng test`, ChromeHeadless) | **162 passed, 0 failed** (131 before + 31 new: state service 16, Messages page 10, hub service 4, REST service 1) |
| Angular production build | Succeeded, no warnings. Initial bundle 326.2 kB; the Messages page chunk is 20.0 kB |
| Database migration drift | None: Phase 13 and the UTC fix change no tables or columns |
| **Total** | **296 passed, 0 failed** |

### 11.2 Phase 13 in two browsers (13/13 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| E2E-M13-01 | No status line for someone who has never connected; then "Online" and a green dot appear live when Bob signs in | PASS | status "Online", green dot in header and list, colour rgb(22, 163, 74) |
| E2E-M13-02 | "typing…" appears on Alice's screen (header and list) while Bob types without sending | PASS | header "typing…" and list preview "typing…" while Bob's message is unsent |
| E2E-M13-03 | Throttle: a burst of 35 keystrokes sends ONE typing signal; after a quiet spell the next burst sends the second | PASS | bursts of 35 and 20 keystrokes -> 2 signals in total |
| E2E-M13-04 | "typing…" clears by itself about 4 s after Bob stops (back to "Online") | PASS | cleared 2.6 s after the last typing signal was seen |
| E2E-M13-05 | Sending ends the typing state at once and the message appears on Alice's screen | PASS | message shown, status "Online" (no stale "typing…") |
| E2E-M13-06 | Delete asks first ("Delete for everyone?"); Cancel keeps the message | PASS | confirm text: "Delete for everyone?DeleteCancel"; Cancel kept the message |
| E2E-M13-07 | Confirming Delete: the text disappears on Alice's screen AND, live, on Bob's screen (placeholder, no text, no ticks) | PASS | both screens show "This message was deleted"; the original text is nowhere in either page |
| E2E-M13-08 | The text is really gone from the database (content empty, DeletedAt set), not just hidden | PASS | content=[] deleted=yes; rows still containing the old text: 0 |
| E2E-M13-09 | The other person cannot delete it: no Delete button on their view, and the API says 403 | PASS | API 403 forbidden: "You can only delete your own messages." |
| E2E-M13-10 | Deleting an UNREAD message lowers the other person's unread badge live (Bob is on the chatbot page) | PASS | Bob's badge 2 -> 1 without a refresh |
| E2E-M13-11 | When Bob signs out Alice sees "Last seen today at …" live; the green dot goes; a reload keeps it | PASS | "Last seen today at 20:48" live, and the same after a reload |
| E2E-M13-12 | Dark theme and mobile (390 px): the status line, delete placeholder and presence stay readable (screenshots) | PASS | no overflow at 390 px |
| E2E-M13-13 | No uncaught JavaScript errors in either browser during the whole run | PASS | 0 uncaught page errors |

### 11.3 Regression: Phase 12 (ticks, badge, toast) in two browsers (17/17 passed)

All 17 checks of section 10.2 were repeated unchanged on the rebuilt stack and passed (sent, delivered and read ticks, unread badge on every page, toast, reload, dark theme, mobile, sign-out, chatbot page intact, no JavaScript errors).

### 11.4 Regression: older features in a real browser (11/11 passed)

Register through the form, a real LLM reply rendered as markdown, auto-titled chats, "+" creating a chat, the pinned Book appointment page, the Messages tab from another page, theme toggle remembered, admin Usage logs after the SQL role grant (its dates now correct), non-admin redirect, wrong-password error, forgot-password page, no JavaScript errors: **all 11 passed**.

### 11.5 Findings

| # | Severity | Finding | Evidence and cause | Resolution |
|---|---|---|---|---|
| F8 | **Medium (real bug, predates Phase 13, found by this run)** | **Every time shown after a reload was off by the viewer's UTC offset** (5½ hours on this IST machine): message times, conversation list times, last seen, usage-page dates. A time pushed live was correct, so the same value showed two different times. | The API sent `"2026-10-21T15:13:56"` with no `Z`: dates are stored in UTC but EF reads `datetime(6)` back as "Unspecified", so JSON had no zone marker and browsers read it as local time. Check `E2E-M13-11` failed ("20:42" live, "15:12" after reload). | **Fixed in this phase:** every date is now marked UTC by an EF value converter (`UtcDateTimeConverter`, applied to every `DateTime` column by convention) so the API sends `...Z`; and `CreatedAt` is set by the app in UTC instead of the database server's clock. 6 regression tests (`UTC-01` to `UTC-04`); the same browser check now passes (20:48 live and after reload). **Effect on existing data:** rows created before the fix by a database server that is not on UTC (for example a host MySQL on local time) will show shifted times; the Docker stack's MySQL is on UTC, so it is unaffected. |
| F9 | Info (test setup, not the app) | The first run of this phase's browser script failed 9 checks because I did not clean the database after the previous run, so Alice's search found an older "Bob Tester". | The clean-up step was missing between runs. | Re-run on a clean database: 13/13. The script now cleans between runs. |
| F10 | Low | Typing signals are not rate limited on the server (a malicious signed-in client could send many). | The client sends one every 3 s; the server forwards each one to the other participant only. | Covered by API rate limiting in Phase 21. |
| F11 | Info | A last-seen time is visible to everyone who shares a conversation, and cannot be hidden. | Documented design limit (README, direct chat plan). | A privacy setting is not planned. |

### 11.6 Not covered by this run

- **Hidden-tab behaviour** in a real browser: covered later by `E2E-F-07` (section 12).
- **A conversation of more than 50 messages** against the real server: covered later by `E2E-F-12` (section 12).
- **Real password-reset emails:** no SMTP credentials in this environment.
- **Older API-level checks** (the 37-check API run of section 9): not repeated, because no endpoint they cover changed except through the UTC converter, which the browser regression run exercised end to end (auth, chat with a real LLM reply, admin usage, direct chat).

## 12. Phase 14 test run (2026-10-21)

Run as the closing check of the user-to-user chat (Phases 9-14), against the **real Docker stack** (MySQL, migration, .NET API, Python service, nginx + Angular), with **two real Chrome browsers acting as two people** (Alice, with a second tab, and Bob; separate profiles, real WebSockets through nginx). The database was emptied before every browser run. Throwaway accounts only; all test data was deleted afterwards and the stack stopped. The host dev MySQL was not touched. No database migration was needed in Phase 14 (`dotnet ef migrations has-pending-model-changes`: no changes).

### 12.1 Automated suites

| Suite | Result |
|---|---|
| .NET (`dotnet test`) | **122 passed, 0 failed** (hub logging changed a constructor; no new tests) |
| Python (`pytest`) | **12 passed, 0 failed** (unchanged) |
| Angular unit (`ng test`, ChromeHeadless) | **168 passed, 0 failed** (162 before + 6 new: pagehide/pageshow 3, long-outage retry 3) |
| Angular production build | Succeeded, no warnings. Initial bundle 327.0 kB |
| axe-core scan (Messages screen) | No violations, light or dark |
| API log during the runs | 0 unhandled exceptions; 0 "ClientTimeoutInterval" disconnects |
| **Total** | **302 passed, 0 failed** |

### 12.2 Whole feature in two browsers (17/17 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| E2E-F-01 | Find someone by name (no email on the page) and start a chat | PASS | one result, email not on the page |
| E2E-F-02 | One grey tick (sent) while the other person is offline | PASS | m1 status=sent |
| E2E-F-03 | Two grey ticks when Bob signs in; Alice sees "Online"; Bob has badge 1 and no toast | PASS | delivered, "Online", badge 1, no toast |
| E2E-F-04 | Live message while Bob is on another page: toast with sender and preview, badge 2 | PASS | toast "Alice Rao: m2 are you there?" |
| E2E-F-05 | Bob opens the chat: badge clears and Alice's ticks turn blue | PASS | both ticks read |
| E2E-F-06 | Typing shows then clears on send; the message arrives; read tick live | PASS | "typing..." shown then cleared |
| E2E-F-07 | Hidden tab: counts as unread and toasts, not marked read; marked read when visible again | PASS | tick stayed delivered, then read |
| E2E-F-08 | Two tabs of the same person stay in step | PASS | badge cleared in both after one read it |
| E2E-F-09 | Delete from tab 2: placeholder for Alice and, live, for Bob; text gone from the database | PASS | row content empty, deleted set |
| E2E-F-10 | Bob signs out: "Last seen today at ..." live and after reload; next message stays at one tick | PASS | m6 status=sent |
| E2E-F-11 | Bob signs back in: m6 becomes delivered live; Bob has 1 unread | PASS | delivered, badge 1 |
| E2E-F-12 | More than 50 messages: newest 50, then "Show earlier messages" with no gaps or duplicates | PASS | 66 shown = 66 in the database, in order |
| E2E-F-13 | Eight pairs start the same chat at the same instant | PASS | one conversation per pair; "created" answered once per pair |
| E2E-F-14 | An Admin and an outsider get 404 on someone else's conversation and see nothing listed | PASS | 8 probes, all 404 |
| E2E-F-15 | API restarts while both are connected | PASS | back after 3 s; live message through 12 s after restart; ticks and presence work |
| E2E-F-16 | Accessibility scan (axe-core, WCAG 2 A/AA), light and dark | PASS | no violations |
| E2E-F-17 | No uncaught JavaScript errors in any tab | PASS | 0 errors |

### 12.3 Regressions on the final build

| Run | Result |
|---|---|
| Phase 13 (typing, presence, delete) | 13/13 passed |
| Phase 12 (ticks, badge, toast) | 17/17 passed |
| Older features (register, AI reply as markdown, titles, appointments page, theme, admin usage, error states) | 11/11 passed |

### 12.4 Long outage

The API was stopped for 130 s with both people connected (more than SignalR's reconnect window, about 42 s). After the API answered again the browser recovered by itself within 15 s and a live message reached the other person. Before the fix it never recovered without a reload.

### 12.5 Findings

| # | Severity | Finding | Evidence and cause | Resolution |
|---|---|---|---|---|
| F12 | **Medium (real defect, found by this run)** | **Stale "Online" after a reload or closed tab**, and `LastSeenAt` not saved. | `E2E-F-10` failed. Added hub logging showed the old WebSocket survived the page leaving until the server's 30 s `ClientTimeoutInterval`; the user stayed online while a new connection was already open. | **Fixed:** the browser closes the connection on `pagehide` and reopens it on `pageshow` from the cache (CON-01 to CON-03). The hub now logs every connect and disconnect. A lost network without closing the tab still shows as online for up to 30 s (limit). |
| F13 | **Medium (real defect, accessibility)** | Colour contrast below 4.5:1: muted text (54 serious axe nodes in light) and the green "Online" words. | `E2E-F-16`. `--text-muted` was too light; the green dot colour was reused for text. | **Fixed:** `--text-muted` `#5d6d83`; new `--online-text` for the words (dot unchanged); documented in the ui-design skill. Scan: no violations in both themes. |
| F14 | **Medium (real defect, found by this run)** | After a server outage of about a minute or longer, the app stayed offline until a manual reload. | SignalR's automatic reconnect gives up after about 42 s (CON-07 reproduced it at 130 s). | **Fixed:** the service retries itself (1 s doubling to 30 s) and refetches on return (CON-04 to CON-06). Re-test recovered in 15 s. |
| F15 | Info (test setup, not the app) | Early runs of the script showed hidden-tab stalls and stale rows from earlier runs. | A second browser tab in front makes the first genuinely `hidden`, and actions stall; rows from a previous run confused name search. | The script brings tabs to the front and empties the database between runs. |
| F16 | Low (open, by design) | Typing signals are not rate limited on the server; last seen cannot be hidden; presence is in memory (one `api` replica). | See Phase 13 findings F10 and F11, and Phase 16. | Phases 21 and 16. |

### 12.6 Not covered by this run

- **A real phone or a real network drop** (airplane mode): the lost-network case is covered by the server timeout only; the stop/start of the API stands in for it.
- **Real password-reset emails:** no SMTP credentials in this environment.
- **More than two people / more than one API replica:** covered by Phase 16 (section 14).
- Load, security and penetration testing are out of scope (section 1).

## 13. Phases 15 and 17 test run (2026-10-22)

Run after Phase 15 (RabbitMQ and Redis foundation) and Phase 17 (password-reset email through RabbitMQ), against the **real Docker stack** with a **real RabbitMQ 3.13** and Redis 7, real MySQL, the .NET API, the Python service and nginx. The queue tests used the real broker (stopped, started and restarted during the run) and read the dead-letter queue through RabbitMQ's management API; a test-only compose override (kept outside the repo) switched the email provider and the retry delays per scenario. Throwaway accounts only; all test data was deleted afterwards (users and tokens removed, queues emptied) and the stack stopped. **The host dev MySQL was not touched** (it was not running during this work).

### 13.1 Automated suites

| Suite | Result |
|---|---|
| .NET (`dotnet test`) | **141 passed, 0 failed** (122 before + 19 new: processor 7, wiring 10, topology and inline queue 2; the forgot-password controller tests were adapted to `IEmailQueue`) |
| Python (`pytest`) | **12 passed, 0 failed** (unchanged) |
| Angular unit (`ng test`, ChromeHeadless) | **168 passed, 0 failed** (unchanged; no frontend change) |
| Database migration drift | **Not re-run**: `dotnet ef migrations has-pending-model-changes` starts the app, which connects to the host MySQL, and that server was not running. No file under `Models/`, `Data/` or `Migrations/` was changed (`git diff` is empty for them), so no migration is needed |
| **Total** | **321 passed, 0 failed** |

### 13.2 Phase 15: broker foundation (13/13 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| P15-01 | rabbitmq and redis containers are running and healthy | PASS | rabbitmq=healthy redis=healthy |
| P15-02 | only the web container publishes a port; neither broker is reachable from outside | PASS | published: web:8080 |
| P15-03 | redis refuses a client without the password | PASS | NOAUTH Authentication required. |
| P15-04 | redis refuses a wrong password | PASS | AUTH failed: WRONGPASS invalid username-password pair or user is disabled. NOAUTH Authentication required. |
| P15-05 | redis answers PONG with the real password | PASS |  |
| P15-06 | rabbitmq has no default 'guest' login | PASS | user 'guest' - invalid credentials |
| P15-07 | rabbitmq accepts the generated 'aichat' login | PASS |  |
| P15-08 | rabbitmq rejects a wrong password for aichat | PASS |  |
| P15-09 | API /health is 200 (liveness only, no broker check) | PASS | status 200 |
| P15-10 | API connected to the broker: the email worker is consuming email.send | PASS |  |
| P15-11 | topology exists and is durable: email.send, email.dead, email.retry.10s, email.retry.60s | PASS | email.dead, email.retry.10s, email.retry.60s, email.send |
| P15-12 | existing behaviour unchanged: register and login still work with the brokers running | PASS | register 201, login 200 |
| P15-13 | error shape unchanged: unauthenticated call is 401 with { error: { code, message } } | PASS |  |

### 13.3 Phase 17: async email against a real RabbitMQ (28/28 passed)

Scenarios: S1 happy path (console provider); S2 every send fails (retries after 2 s and 3 s); S3 broker stopped then started; S4 broker restart and API restart while a message waits; S5 message TTL (1 minute); S6 40 requests at once; S7 no broker configured; S8 host set with a blank password.

| ID | Check | Result | Evidence |
|---|---|---|---|
| P17-01 | same status and body for a registered and an unregistered email | PASS | 200 / 200: If that email is registered, a password reset link has been sent. |
| P17-02 | the worker delivered the registered user's email (via the queue) | PASS | link found in the delivery log |
| P17-03 | no email was produced for the unregistered address | PASS |  |
| P17-04 | end to end: the emailed link resets the password (new works, old fails) | PASS | reset 200, new 200, old 401 |
| P17-05 | queues are empty afterwards (message acknowledged, nothing stuck in retry or dead-letter) | PASS | {"email.dead":{"m":0,"c":0},"email.retry.2s":{"m":0,"c":0},"email.retry.30s":{"m":0,"c":0},"email.send":{"m":0,"c":1},"email.retry.3s":{"m":0,"c":0... |
| P17-06 | the worker holds exactly one consumer on email.send | PASS |  |
| P17-07 | the request is not slowed or failed by the mail failure: 200 generic answer right away | PASS | 200 in 422 ms |
| P17-08 | after 3 failed attempts (waits 2 s and 3 s) the email lands in the dead-letter queue | PASS | dead-lettered after 7.1 s (waits sum to 5 s) |
| P17-09 | logs show attempt 1 of 3, attempt 2 of 3 retrying, then giving up on attempt 3 of 3 | PASS |  |
| P17-10 | the dead-letter message carries the attempt count and the error, and the other queues are empty | PASS | x-attempt=3, x-error="Smtp:Username is not configured. Set it via 'dotnet user-sec..." |
| P17-11 | the dead-letter message is persistent and holds the original payload | PASS | delivery_mode=2 |
| P17-12 | the retry queues were named after their delays (email.retry.2s / email.retry.3s) | PASS |  |
| P17-13 | broker down: the answer is still the same 200 generic message (no 500, no account revealed) | PASS | known 200 in 258 ms, unknown 200 in 42 ms |
| P17-14 | broker down: the failure is logged for the operator (by user id, not email) | PASS |  |
| P17-15 | broker down: the rest of the app keeps working (login 200) | PASS | login 200 |
| P17-16 | broker back: the worker reconnects by itself (a consumer is attached again, no API restart) | PASS | {"m":0,"c":1} |
| P17-17 | broker back: a new request is queued and delivered normally | PASS |  |
| P17-18 | after the first failure the email waits in email.retry.20s | PASS | {"email.dead":{"m":0,"c":0},"email.retry.2s":{"m":0,"c":0},"email.retry.30s":{"m":0,"c":0},"email.send":{"m":0,"c":1},"email.retry.3s":{"m":0,"c":0... |
| P17-19 | broker restarted: the waiting email is still there (durable queue, persistent message) | PASS | {"email.dead":{"m":0,"c":0},"email.retry.2s":{"m":0,"c":0},"email.retry.30s":{"m":0,"c":0},"email.send":{"m":0,"c":1},"email.retry.3s":{"m":0,"c":0... |
| P17-20 | API restarted too: the message is still processed to the end (nothing lost, nothing duplicated) | PASS | {"email.dead":{"m":1,"c":0},"email.retry.2s":{"m":0,"c":0},"email.retry.30s":{"m":0,"c":0},"email.send":{"m":0,"c":1},"email.retry.3s":{"m":0,"c":0... |
| P17-21 | the stale message is dropped by the worker with a log line, never sent | PASS |  |
| P17-22 | and nothing is left in any queue, including the dead-letter queue | PASS | {"email.dead":{"m":0,"c":0},"email.retry.2s":{"m":0,"c":0},"email.retry.30s":{"m":0,"c":0},"email.send":{"m":0,"c":1},"email.retry.3s":{"m":0,"c":0... |
| P17-23 | 40 simultaneous requests: all answered 200 with the same message | PASS | slowest 410 ms |
| P17-24 | every one of the 40 emails was delivered exactly once | PASS | 40/40 delivered once each, in 8.8 s |
| P17-25 | queues drain to empty | PASS | {"email.dead":{"m":0,"c":0},"email.retry.2s":{"m":0,"c":0},"email.retry.30s":{"m":0,"c":0},"email.send":{"m":0,"c":1},"email.retry.3s":{"m":0,"c":0... |
| P17-26 | no broker: the email is still produced (inline) and the answer is the same generic 200 | PASS | 200 in 169 ms |
| P17-27 | no broker: no worker starts and the app never tries to connect to RabbitMQ | PASS |  |
| P17-28 | the API refuses to start with a clear message naming the missing setting | PASS | Unhandled exception. System.InvalidOperationException: RabbitMq:Host is set but RabbitMq:Username / RabbitMq:Password are not. Set them via user-se... |

### 13.4 Browser regression with the brokers running (9/9 passed)

Real Chrome windows against the stack with RabbitMQ on and no SMTP credentials.

| ID | Check | Result | Evidence |
|---|---|---|---|
| R-01 | register through the form lands on the chat page | PASS | http://localhost:8080/ |
| R-02 | a chat message gets a real AI reply (Angular -> .NET -> Python -> LLM -> MySQL) | PASS | pong |
| R-03 | forgot-password page shows the same confirmation for a registered and an unknown email | PASS | Check your email /  |
| R-04 | with SMTP unconfigured the queued email retries and is dead-lettered (one message, for the registered address only) | PASS |  |
| R-05 | wrong password shows the inline error | PASS | Email or password is incorrect. |
| R-06 | direct message: Bob receives Alice's message (REST + SignalR through nginx) | PASS |  |
| R-07 | Alice's message shows the blue read tick once Bob has it open | PASS |  |
| R-08 | Bob's reply reaches Alice live | PASS |  |
| R-09 | no uncaught JavaScript errors in any window | PASS |  |

### 13.5 Findings

| # | Severity | Finding | Evidence and cause | Resolution |
|---|---|---|---|---|
| F17 | **Medium (real defect, found by this run, before release)** | After the broker was stopped and started, the email worker never reconnected: no consumer on `email.send`, and later emails stayed unsent. | `P17-16` and `P17-17` failed on the first run. A publish that arrived while the broker was down found the shared connection mid-recovery, disposed it and created a new one, which also disposed the channel the worker's consumer was attached to. A plain broker restart with no publish in between (`P17-19`) recovered fine, which is why only the outage case failed. | **Fixed:** `RabbitMqConnection` keeps its connection for good and leaves recovery to the client library; a publish during the outage simply fails. Both checks and all 28 now pass. |
| F18 | Info (test setup, not the app) | Several checks failed on the first runs for harness reasons: the dead-letter read used `localhost` (which resolved to IPv6 inside the container), a Redis check read only stdout, and the browser script waited on the wrong `role=status` element (the page-wide loading bar) and the wrong tick selector. | The app behaved correctly each time; the scripts were corrected and re-run. | None needed. |
| F19 | Low (by design) | With the broker unreachable, a request for a **registered** email takes a little longer than one for an unknown email (258 ms against 42 ms here; up to the 5 s connection timeout on a black-holed network). | The publish has to try to connect. Before the queue existed, a slow or failing SMTP send inside the request made the same difference, and a bigger one. | Accepted; status and body are identical either way. Rate limiting (Phase 21) would limit probing. |
| F20 | Low (by design) | If the broker is down when a reset is requested, that one email is not sent (the token is saved, nothing is queued, no inline fallback). | Falling back to an inline send would bring back the slow, failure-prone path, and a `503` would reveal which emails have accounts. | Logged by user id; the person can request a new link. |
| F21 | Low (by design) | Delivery is at-least-once: a crash between sending and acknowledging can send one email twice. | Standard queue behaviour; a reset email sent twice is harmless. | None needed. |
| F22 | Info | The long browser suites for Phases 12 to 14 were kept outside the repo and were lost when the scratch folder was cleared, so they could not be repeated. | There is no committed browser suite (section 1). | A shorter regression (13.4) covers register, AI reply, forgot-password, login error and live direct messages with ticks. The long suites should be rebuilt and committed in a later test phase. |

### 13.6 Not covered by this run

- **Real SMTP delivery:** no credentials here. The `console` provider stood in for a successful send and the unconfigured SMTP sender for a failing one.
- **Redis:** it only has to start, be protected and be reachable; nothing uses it until Phase 16.
- **More than one API replica:** each replica runs its own email worker on the same queue (safe); presence and pushes across replicas are covered in section 14.
- **A black-holed network** (packets dropped rather than refused): the 5 s connection timeout is configured but was not exercised.
- **The long Phase 12 to 14 browser suites** (see F22) and the **migration drift check** (see 13.1).
- Load, security and penetration testing are out of scope (section 1).

## 14. Phase 16 test run (2026-10-23)

Run after Phase 16 (Redis: SignalR backplane and shared presence), against the **real Docker stack** with a **real Redis 7**, **two API replicas** sharing one MySQL, Redis and RabbitMQ, real nginx, real SignalR clients over real WebSockets, and real Chrome windows. Two set-ups were used: (A) a test-only compose override that publishes the two replicas on separate host ports (18081 and 18082; 8081 is used by Docker Desktop's Kubernetes here) so each client can be pointed at a chosen replica, and (B) `docker compose up --scale api=2` behind nginx, the way a real deployment would run. Redis was stopped, restarted and flushed, and a replica was killed with SIGKILL, during the run. A short presence lifetime (6 s, heartbeat 2 s) was used in set-up A so expiry could be observed; set-up B used the real defaults (45 s / 15 s). Throwaway accounts only; all test data was deleted afterwards (users removed, Redis flushed, queues emptied) and the stack stopped. **The host dev MySQL was not touched.**

### 14.1 Automated suites

| Suite | Result |
|---|---|
| .NET (`dotnet test`) | **161 passed, 0 failed** (141 before + 20 new: shared presence tracker 12, in-memory tracker async 4, wiring 4) |
| Python (`pytest`) | **12 passed, 0 failed** (unchanged) |
| Angular unit (`ng test`, ChromeHeadless) | **168 passed, 0 failed** (unchanged; the one-line change to the real hub factory is not reachable from unit tests, it is verified in 14.5) |
| Angular production build | Succeeded, no warnings. Initial bundle 327.1 kB |
| Database migration drift | **Not re-run**: it starts the app, which connects to the host MySQL, and that server was not running. No file under `Models/`, `Data/` or `Migrations/` changed, so no migration is needed |
| **Total** | **341 passed, 0 failed** |

### 14.2 Two replicas with a real Redis: API and SignalR level (32/32 passed)

Alice on replica 1, Bob on replica 2, and so on, using real SignalR clients. "Section" letters match the scenario groups in the script.

| ID | Check | Result | Evidence |
|---|---|---|---|
| P16-01 | two API replicas are up and healthy (ports 18081 and 18082), sharing one MySQL, Redis and RabbitMQ | PASS |  |
| P16-02 | both people are recorded in Redis presence (one key per user) | PASS | presence:user:1, presence:user:2 |
| P16-03 | each presence key has an expiry, so an abandoned key cleans itself up | PASS | PTTL 17736 ms |
| P16-04 | Alice (replica 1) sees Bob (connected to replica 2) as online in the conversation list | PASS |  |
| P16-05 | Bob (replica 2) sees Alice (replica 1) as online | PASS |  |
| P16-06 | a message sent through replica 1 is "delivered" at once because Bob is online on replica 2 (shared presence) | PASS | status=delivered |
| P16-07 | Bob, connected to replica 2, receives the live MessageReceived push from replica 1 (Redis backplane) | PASS | payload: "hello bob (sent through replica 1)" |
| P16-08 | Bob marks it read on replica 2; Alice's read tick arrives live on replica 1 | PASS |  |
| P16-09 | Alice's typing signal (replica 1) reaches Bob (replica 2) | PASS |  |
| P16-10 | delete through replica 1 is pushed to both people, on both replicas | PASS | delete status 200 |
| P16-11 | Carol's first tab (replica 1) makes her come online: Alice is told exactly once | PASS |  |
| P16-12 | Carol's second tab on the OTHER replica does not announce her online again | PASS |  |
| P16-13 | closing tab 1 (replica 1) does not take her offline: no event, still online per the other replica | PASS |  |
| P16-14 | closing the last tab (replica 2) takes her offline: exactly one event, with a last-seen time | PASS |  |
| P16-15 | afterwards the list shows Carol offline with the saved last-seen time | PASS | 2026-10-23T05:52:49.500775Z |
| P16-16 | exactly ONE "came online" announcement per person, even with two simultaneous first connections on different replicas (atomic Lua) | PASS | counts: 1 over 20 people |
| P16-17 | exactly ONE "went offline" announcement per person after both tabs close at the same instant | PASS | counts: 1 over 20 people |
| P16-18 | Bob has been idle for 12 s (twice the 6 s mark lifetime) and is still online: the heartbeat refreshes his mark | PASS |  |
| P16-19 | Bob's replica is killed: within the mark lifetime (plus one beat) Alice's replica reports him offline, with no manual cleanup | PASS |  |
| P16-20 | a message to him now stays at "sent" (one grey tick), not falsely "delivered" | PASS | status=sent |
| P16-21 | KNOWN LIMIT: a crashed replica sends no "went offline" event and saves no last-seen time (recorded, not a pass of a requirement) | PASS | offline events for Bob: 0 |
| P16-22 | Bob reconnects to the surviving replica: his waiting message becomes "delivered" and Alice is told live | PASS |  |
| P16-23 | the killed replica starts again and is healthy | PASS |  |
| P16-24 | Redis down: sending a message still works (201, saved) and is not a 500 | PASS | status 201 in 2603 ms, delivery status=sent |
| P16-25 | Redis down: the request is slowed by seconds at most, not left hanging (2 s Redis timeouts) | PASS | 2603 ms |
| P16-26 | Redis down: the conversation list still loads (presence falls back to this server) | PASS | status 200 in 16 ms |
| P16-27 | Redis down: /health stays 200 on both replicas (liveness only, no dependency check) | PASS |  |
| P16-28 | Redis down: the message was saved and Bob can read it (REST is unaffected) | PASS |  |
| P16-29 | Redis back: live pushes cross replicas again WITHOUT reconnecting Bob or restarting anything | PASS | delivery status=delivered |
| P16-30 | after FLUSHALL no presence keys remain (simulated data loss) | PASS |  |
| P16-31 | within a heartbeat the replicas re-create their connections' marks and Bob shows online again, with nobody reconnecting | PASS |  |
| P16-32 | when everyone has disconnected no presence keys are left in Redis | PASS |  |

### 14.3 Negative control: Redis switched off, two replicas (4/4 passed)

Both replicas were restarted with `Redis:ConnectionString` empty. Everything then stays in memory, so the two replicas must NOT see each other. This is what Phase 16 fixes, and it proves the checks in 14.2 are not vacuous.

| ID | Check | Result | Evidence |
|---|---|---|---|
| N-01 | without Redis, a message to someone on the OTHER replica is only "sent" (presence is not shared) | PASS | status=sent |
| N-02 | without Redis, the other replica gets NO live push | PASS |  |
| N-03 | without Redis, Alice cannot see that Bob is online (he is on the other replica) | PASS |  |
| N-04 | without Redis, two people on the SAME replica still work (delivered and pushed): the single-replica mode is intact | PASS | status=delivered |

### 14.4 Why the browser client skips negotiation (2/2 passed)

30 connections each through nginx with two replicas behind it.

| ID | Check | Result | Evidence |
|---|---|---|---|
| X-01 | WITH the negotiate step, connections through the proxy fail when negotiate and socket land on different replicas | PASS | 14/30 connected (failures are the proof the change is needed) |
| X-02 | WITHOUT the negotiate step (what the app now does), every connection through the proxy succeeds | PASS | 30/30 connected |

### 14.5 Real browsers, nginx and two replicas (16/16 passed)

Two Chrome windows (Alice and Bob) through nginx with `--scale api=2`. The script confirms from the replicas' own logs that the two people are connected to different replicas.

| ID | Check | Result | Evidence |
|---|---|---|---|
| P16-B01 | Alice and Bob are connected to DIFFERENT API replicas behind the same nginx | PASS | Alice -> aichat-api-2, Bob -> aichat-api-1 (after 1 page load(s) for Bob) |
| P16-B02 | Alice's message shows two grey ticks (delivered) because Bob is online on the other replica | PASS |  |
| P16-B03 | Bob's list shows Alice's message live, with an unread count (cross-replica push) | PASS |  |
| P16-B04 | Bob opens the chat: Alice's ticks turn blue live (read receipt crosses replicas) | PASS |  |
| P16-B05 | Bob's unread count clears | PASS |  |
| P16-B06 | Alice sees "typing…" while Bob types on the other replica | PASS |  |
| P16-B07 | Bob's reply reaches Alice live | PASS |  |
| P16-B08 | Alice sees Bob as "Online" while he is connected to the other replica | PASS | Online |
| P16-B09 | Alice deletes her message; Bob sees "This message was deleted" live and the text is gone from his page | PASS | the transient toast from the first arrival may still be on screen; the thread and list do not show the text |
| P16-B10 | Bob reloads four times (each may land on either replica): live messages from Alice arrive every time | PASS | 4/4 |
| P16-B11 | Bob signs out: Alice sees "Last seen today at …" (offline event crosses replicas) | PASS | Last seen today at 11:21 |
| P16-B12 | Bob signs back in: Alice sees him Online again, live | PASS |  |
| P16-B13 | Bob's replica is killed: his browser reconnects to the surviving replica on its own and the message reaches him | PASS | received 0.653 s after the kill |
| P16-B14 | the new connection is on the other replica | PASS | aichat-api-2 -> aichat-api-1 |
| P16-B15 | Alice's message after the crash shows at least two ticks (delivered) once Bob is back | PASS |  |
| P16-B16 | no uncaught JavaScript errors in either window | PASS |  |

### 14.6 Browser regression on the standard stack: one API, Redis and RabbitMQ on (8/8 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| R-01 | register through the form lands on the chat page | PASS | http://localhost:8080/ |
| R-02 | a chat message gets a real AI reply (Angular -> .NET -> Python -> LLM -> MySQL) | PASS | pong |
| R-03 | forgot-password page shows the same confirmation for a registered and an unknown email | PASS |  |
| R-04 | wrong password shows the inline error | PASS | Email or password is incorrect. |
| R-05 | direct message: Bob receives Alice's message | PASS |  |
| R-06 | Alice's message shows the blue read tick once Bob has it open | PASS |  |
| R-07 | Bob's reply reaches Alice live | PASS |  |
| R-08 | no uncaught JavaScript errors in any window | PASS |  |

### 14.7 Findings

| # | Severity | Finding | Evidence and cause | Resolution |
|---|---|---|---|---|
| F23 | **Medium (real defect for any multi-replica set-up, found before release)** | With two API replicas behind the proxy, the browser's SignalR connection fails about half the time. | `X-02`/`X-01`: 14 of 30 connections worked with the negotiate step, 30 of 30 without. SignalR negotiates on one request and then opens the WebSocket on another; behind a round-robin proxy these can land on different replicas, and the second replica does not know the connection token. | **Fixed:** the Angular client now connects with WebSockets only and `skipNegotiation: true` (`direct-hub.service.ts`). WebSockets are supported by every browser this app targets; the long-polling fallback is gone. |
| F24 | Low (by design, recorded) | If a replica **crashes** (not a clean stop), its users drop offline after the presence lifetime (45 s by default) but no "went offline" event is sent and their last-seen time is not saved. | `P16-21`: zero offline events for Bob. The clean-stop path writes last-seen in the hub's disconnect handler, which a SIGKILL never reaches. | Accepted for now. If it matters, a sweeper that notices lapsed marks can emit the event and save last-seen; not planned. |
| F25 | Low (by design) | While Redis is down, **live pushes stop** for everyone (SignalR's Redis backplane carries even same-replica pushes), although REST keeps working. A message sent in that time is saved but not pushed; the receiver sees it after their next refetch (tab becomes visible again, or a reconnect). | `P16-24`/`P16-28`: the send returns 201 and the message is readable, but no live event. `P16-29`: pushes resume by themselves, with no reconnect and no restart, when Redis returns. | Documented. Redis availability should be monitored (Phase 27). |
| F26 | Low | With Redis down, a request that touches presence or a push waits for the Redis timeout (2 s by default in this app; the library default is 5 s) before continuing. | `P16-24`: a send took 2.8 s. | Timeouts set to 2 s so the slowdown is bounded; presence then falls back to this server's own view. |
| F27 | Low | A message that is deleted for everyone can still be visible in the **toast** that announced it, for up to 6 s (the toast's lifetime). | `P16-B09` run 1: the first message's text was still inside `.toast-stack` after the delete. The thread and list were correct. | Not changed (the recipient had already seen the message when it arrived). Could dismiss the matching toast on `MessageDeleted`; not planned. |
| F28 | Info (environment, not the app) | Host port 8081 is already used by Docker Desktop's Kubernetes (a leftover `aichat` namespace), so the first two-replica run silently hit the wrong service (a 502 from a different nginx). | `lsof` showed the Docker backend listening on 8081. | Tests use ports 18081 and 18082. |
| F29 | Info (test setup, not the app) | Several harness slips: nginx's round robin kept putting both browser users on the same replica (page loads make an even number of API calls), Bob's page was a hidden tab so a read receipt was not sent, and `zsh` did not split a command held in a variable. | Each time the app behaved correctly; the scripts were corrected (shift the rotation, `bringToFront()`, run through `bash`). | None needed. |
| F30 | Info | The long browser suites for Phases 12 to 14 are still not available (lost with the earlier scratch folder), see F22. The checks in 14.5 repeat the key parts across two replicas. | | Rebuild and commit the long suites in a later test phase. |

### 14.8 Not covered by this run

**These gaps (and those in 13.6) are tracked as Phase T6 in `README.md` ("Test plan"), to be done later.**

- **More than two replicas** and a **rolling restart** under load (two replicas were used, one killed and restarted).
- **Redis Sentinel/Cluster or a managed Redis** (a single Redis container was used).
- **A real network partition** between the API and Redis (the container was stopped and started, not firewalled).
- **A crashed replica's last-seen and offline event** (F24, a known limit).
- **nginx with a replica added or replaced while running:** nginx resolves the `api` name when it starts, so after scaling or replacing replicas run `docker compose restart web`.
- **The long Phase 12 to 14 browser suites** (F30) and the **migration drift check** (14.1).
- Load, security and penetration testing are out of scope (section 1).

## 15. Phases 18 and 19 test run (2026-10-23)

Run after Phase 18 (async AI replies through RabbitMQ) and Phase 19 (direct-chat events through a transactional outbox), against the **real Docker stack**: two API replicas, a real RabbitMQ 3.13 (with its management API used to publish, peek and count), a real Redis 7, a real MySQL 8.4, real nginx, real SignalR clients over real WebSockets and real Chrome windows. For the failure and timing tests the Python AI service was replaced by a **stub with the same contract** that counts calls and can be told to delay or fail (a test-only compose override outside the repo also shortened the retry delays to 2 s and 3 s and the job lease to 12 s, and published the two replicas on ports 18081 and 18082). One run used the **real Python service and DeepInfra** with async replies on, and one with them off. Chaos used during the run: a replica killed with SIGKILL, RabbitMQ and Redis stopped and started, the `OutboxMessages` table renamed away, and rows back-dated with SQL. Throwaway accounts only; all test data (users, chats, jobs, outbox rows, Redis, queues) was deleted afterwards and the stack stopped. **The host dev MySQL was not touched** (the new migration is applied only to the Docker stack's database).

### 15.1 Automated suites

| Suite | Result |
|---|---|
| .NET (`dotnet test`, run repeatedly, stable) | **232 passed, 0 failed** (161 before + 71 new: async replies 32, outbox 39) |
| Python (`pytest`) | **12 passed, 0 failed** (unchanged) |
| Angular unit (`ng test`, ChromeHeadless) | **188 passed, 0 failed** (168 before + 20 new: chat page async states 14, hub 1, chat service 5) |
| Angular production build | Succeeded, no warnings. Initial bundle 327.3 kB; the chat chunk 72.4 kB |
| Database migration drift | **None**: `ASPNETCORE_ENVIRONMENT=Production dotnet ef migrations has-pending-model-changes` (works with no database at all) reports no pending changes |
| **Total** | **432 passed, 0 failed** |

### 15.2 Phase 18 end to end: two replicas, real RabbitMQ, Redis and MySQL, stub AI service (40/40 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| P18-01 | two API replicas, RabbitMQ, Redis, MySQL and the stub AI service are up; async replies are on (Chat:AsyncReplies=true) | PASS |  |
| P18-02 | POST a message: 202 with replyStatus "pending" and a job id, in well under the 3 s the AI takes (the request does not wait) | PASS | status 202 in 49 ms |
| P18-03 | the question is saved at once (alone), and the chat lists the reply as pending | PASS | messages=1, open jobs=["pending"] |
| P18-04 | Alice's browser (connected to the OTHER replica) receives AssistantReplied with the job, chat, question id and the reply | PASS | after 3110 ms: "stub reply to: What is DNS?" |
| P18-05 | the reply took about as long as the AI did (3 s), proving the work happened in the background | PASS | 3110 ms |
| P18-06 | database: the job is done after 1 attempt and points at the reply; the chat has 2 messages and 1 usage log | PASS | {"status":"done","attempts":1,"hasReply":true} |
| P18-07 | reading the chat returns question then reply, and no open jobs remain | PASS |  |
| P18-08 | the second question sent the first exchange as context (history of 2) and the AI was called exactly once per job | PASS | histories=[0,2], calls=2 |
| P18-09 | the queues are empty afterwards (acknowledged; nothing in retry or dead-letter) | PASS | {"email.dead":{"m":0,"c":0},"email.send":{"m":0,"c":2},"ai.retry.3s":{"m":0,"c":0},"ai.retry.2s":{"m":0,"c":0},"ai.reply":{"m":0,"c":2},"ai.retry.1... |
| P18-10 | one failed AI call is retried by itself: the reply still arrives, attempt 2, no failure shown to anyone | PASS | {"status":"done","attempts":2,"hasReply":true} |
| P18-11 | when every attempt fails (waits of 2 s and 3 s) Alice is told with AssistantFailed, carrying a safe message and no internals | PASS | after 5.1 s: "The assistant couldn't reply this time." |
| P18-12 | database: the job is failed after exactly 3 attempts (the first try plus two retries), with no reply; the AI was called 3 times | PASS | {"status":"failed","attempts":3,"hasReply":false} |
| P18-13 | the dead-letter queue holds that one job, and the message contains only the job id (no question, no answer) | PASS | {"JobId":"59260116-fad0-4be7-a699-776651edc060"} |
| P18-14 | after a reload the chat still shows that question with a failed reply (it survives the page) | PASS |  |
| P18-15 | Send again (retry): 202, the SAME job is run again and succeeds with no new question; attempts start over at 1 | PASS | {"status":"done","attempts":1,"hasReply":true} |
| P18-16 | retrying a reply that is already delivered is 409; someone else retrying it is 404 | PASS |  |
| P18-17 | DUPLICATE DELIVERY: the broker delivers an already-finished job again: no second AI call, no second reply, no second usage log | PASS |  |
| P18-18 | an unreadable message is moved to the dead-letter queue, and the worker keeps going | PASS |  |
| P18-19 | a message for a job that does not exist is dropped quietly (not retried forever, not dead-lettered) | PASS |  |
| P18-20 | the worker is still healthy after those: a normal question gets its reply | PASS |  |
| P18-21 | another person cannot see this chat's jobs (404) | PASS |  |
| P18-22 | all 40 requests were accepted (202) and all 40 replies were produced | PASS | accepted 40, done 45 in total |
| P18-23 | every person received exactly one AssistantReplied per question (5 each), none missing, none doubled | PASS |  |
| P18-24 | the AI was called exactly 40 times: one call per job even with two competing workers | PASS | calls=40 |
| P18-25 | backpressure: never more than 8 AI calls at once (prefetch 4 per replica x 2), and more than 4 (both replicas worked) | PASS | max concurrent = 8 |
| P18-26 | database: 40 more done jobs, each pointing at its own reply; replies = jobs; no job done twice | PASS |  |
| P18-27 | killing one replica mid-work loses nothing: all 6 jobs still finish (the survivor takes over after the lease) | PASS | 24 s after the kill |
| P18-28 | each job has exactly ONE reply (no duplicates) even though some were started twice | PASS | attempts: 2,1,2,1,2,1 |
| P18-29 | at least one job was taken over (attempts > 1) and Alice still got all 6 events | PASS | events 6 |
| P18-30 | broker down: the answer is 503 "ai_service_unavailable" (not 500), and the question stays saved | PASS | status 503 in 768 ms |
| P18-31 | broker down: no orphan job is left behind | PASS |  |
| P18-32 | broker down: the rest of the app works (reading the chat, /health) | PASS |  |
| P18-33 | broker back: both replicas re-attach their workers by themselves | PASS | {"m":0,"c":2} |
| P18-34 | broker back: a new question is queued and answered normally | PASS |  |
| P18-35 | Redis down: the reply is produced and saved (job done) even though no push can reach the browser | PASS |  |
| P18-36 | Redis down: reading the chat returns the reply (this is what the page's 15 s safety net and reconnect refetch rely on) | PASS |  |
| P18-37 | Redis back: pushes work again without a reconnect | PASS |  |
| P18-38 | Chat:AsyncReplies=false: the old behaviour is untouched: 200 with both messages in the same response, no job row, one AI call | PASS |  |
| P18-39 | Chat:AsyncReplies=false: the open-jobs list is just empty and no AI-reply worker runs | PASS |  |
| P18-40 | Chat:AsyncReplies=true without RabbitMq:Host: the API refuses to start and says why | PASS | Unhandled exception. System.InvalidOperationException: Chat:AsyncReplies is on but RabbitMq:Host is not set. Async replies need RabbitMQ: se |

### 15.3 Phase 19 end to end: outbox, dispatcher and subscriber (30/30 passed)

Run with one API replica for the ordering checks, then with two. The subscriber's own queue (`direct.events.log`) and a test queue bound to `#` on the exchange were both read.

| ID | Check | Result | Evidence |
|---|---|---|---|
| P19-01 | one API replica runs (the second is stopped for the ordering tests); RabbitMQ has the direct.events topic exchange and the subscriber queue; the outbox is empty | PASS |  |
| P19-02 | sending a message (recipient offline) writes a "direct.message.sent" outbox row, which is published within about a second | PASS | row #1, 1177 ms after the POST |
| P19-03 | the whole life of two messages writes exactly these events, in this order (repeat reads and repeat deletes write none) | PASS | direct.message.sent > direct.message.delivered > direct.message.read > direct.message.deleted > direct.message.sent > direct.message.delivered > di... |
| P19-04 | RabbitMQ received the same events, in the same order (message id = outbox id, routing key = event type) | PASS | 7 events: sent > delivered > read > deleted > sent > delivered > read |
| P19-05 | the payload has exactly: conversationId, messageIds, senderId (the author), recipientId, status, at | PASS | at,conversationId,messageIds,recipientId,senderId,status |
| P19-06 | the author is Alice and the other person is Bob in every event (also for "read", which Bob caused) | PASS |  |
| P19-07 | NO MESSAGE TEXT anywhere: not in any outbox row, not in any message that reached RabbitMQ | PASS | rows/messages containing the text: 0 |
| P19-08 | the subscriber consumed and logged every event once (ids only in the log line), and both subscriber queues are empty | PASS | 7 log lines for 7 events |
| P19-09 | the dispatched rows carry a dispatch time; none is left waiting | PASS |  |
| P19-10 | if the event cannot be written, the whole request fails (500 in the standard error shape) ... | PASS | status 500 |
| P19-11 | ... and the message was NOT saved either: the change rolled back with its event (no change without an event) | PASS | messages 2 -> 2 |
| P19-12 | with the table back, sending works again and the event is written | PASS |  |
| P19-13 | broker down: all 20 messages are still sent (201) and 20 events wait undispatched in the database | PASS | 201s: 20, waiting events: 20 |
| P19-14 | broker down: the API stays healthy and keeps trying (attempts are counted on the waiting rows) | PASS | max attempts 6 |
| P19-15 | broker back: every waiting event is published by itself, none lost | PASS | 20 events arrived |
| P19-16 | they arrive in the order they were written (ascending ids, no gaps, no repeats) | PASS |  |
| P19-17 | the subscriber handled each of them once (no repeats needed ignoring) | PASS | "seen before" lines so far: 0 |
| P19-18 | 100 messages sent through both replicas at once: all 100 "sent" events reached RabbitMQ (plus their delivered events) | PASS | sent=100, total=151 |
| P19-19 | EACH OUTBOX ROW WAS PUBLISHED EXACTLY ONCE: the two dispatchers never both took the same row | PASS | 151 messages, 151 distinct ids |
| P19-20 | the subscriber (competing across both replicas) saw no repeats, and the queues drained | PASS | drained in 9.6 s |
| P19-21 | the surviving replica finishes the whole backlog (rows the dead one had claimed are taken over after the lease) | PASS | backlog was 90 |
| P19-22 | every backlog event reached RabbitMQ at least once (a repeat is allowed after a crash, loss is not) | PASS | 90 distinct of 90, 0 repeat(s) |
| P19-23 | if any event was published twice, the subscriber noticed and ignored the repeat | PASS | repeats published: 0, repeats ignored: 0 |
| P19-24 | an event that is not valid JSON is moved to the dead-letter queue, not retried forever | PASS |  |
| P19-25 | an event with no message ids is dead-lettered too | PASS |  |
| P19-26 | the same event delivered twice (same message id) is handled once and the repeat is ignored | PASS | handled 1, ignored 1 |
| P19-27 | the subscriber keeps working after all of that: a real message is still logged | PASS |  |
| P19-28 | events published more than 24 h ago are deleted by the dispatcher (exactly those 5); recent ones are kept | PASS | rows 270 -> 265 |
| P19-29 | Outbox:Enabled=false: chat works (201), no event row is written, and no dispatcher runs | PASS |  |
| P19-30 | no RabbitMQ configured: chat works exactly as before and the outbox writes nothing (the app runs with only MySQL) | PASS |  |

### 15.4 Why the stub AI service, and what it proves

The stub counts every call, records the context it received and the highest number of calls in flight, and can delay or fail on command. That is what lets the checks above state facts such as "the AI was called exactly 40 times for 40 jobs" and "never more than 8 at once", which a real LLM cannot show. The real service was used for 15.6.

### 15.5 Real browsers: async replies in the chat page (17/17 passed)

Real Chrome through nginx; the page, hub and API are the real ones. Screenshots of the pending, failed (light and dark) and phone-width states were looked at.

| ID | Check | Result | Evidence |
|---|---|---|---|
| B18-01 | sending: the question appears at once and "Thinking…" shows while the reply is produced in the background | PASS | question shown after 263 ms |
| B18-02 | while a reply is pending the Send button is disabled (no second question queued behind it) | PASS |  |
| B18-03 | the reply appears by itself (no reload), after about the 4 s the AI took, "Thinking…" goes away | PASS | 4618 ms; reply: "stub reply to: What is DNS?" |
| B18-04 | and the composer is usable again | PASS |  |
| B18-05 | when the AI keeps failing, after the retries (about 5 s) the question shows "The assistant couldn't reply." with Send again | PASS | after 5.9 s |
| B18-06 | no "Thinking…" is left behind and the composer is free | PASS |  |
| B18-07 | Send again: the failed note disappears at once and "Thinking…" returns (same question, nothing re-typed) | PASS |  |
| B18-08 | the reply arrives, and the chat still has exactly the two questions (no duplicate from Send again) | PASS |  |
| B18-09 | reloading the page while a reply is pending still shows the question AND "Thinking…" (restored from the server) | PASS |  |
| B18-10 | and the reply still arrives on the reloaded page | PASS |  |
| B18-11 | if the live push is lost (Redis down) the reply is saved anyway and the page picks it up by itself within about 15-20 s, with no reload | PASS | appeared after 16 s |
| B18-12 | and "Thinking…" is cleared | PASS |  |
| B18-13 | switching to another chat while a reply is pending: that chat is clean, and coming back later shows the finished reply (no stuck "Thinking…") | PASS |  |
| B18-14 | the same person with the chat open in two tabs: the reply appears in BOTH tabs | PASS |  |
| B18-15 | accessibility scan (axe-core WCAG 2 A/AA) of the chat with a pending reply, a failed reply, and in dark theme: no serious or critical violations | PASS | none |
| B18-16 | on a phone-width screen (390 px) the failed state does not overflow | PASS |  |
| B18-17 | no uncaught JavaScript errors in either tab | PASS |  |

### 15.6 Browser regression with the real AI service

**With async replies on** (10/10 passed): a real DeepInfra reply arrived as `202` then a SignalR push.

| ID | Check | Result | Evidence |
|---|---|---|---|
| R-01 (async) | register through the form lands on the chat page | PASS |  |
| R-02 (async) | a real AI reply arrives (async: 202, then pushed over SignalR) through the real Python service and DeepInfra | PASS | "pong" after 2.0 s |
| R-03 (async) | the stored job state matches the mode: async leaves one finished job, sync leaves none | PASS | jobs=1 |
| R-04 (async) | forgot-password shows the same confirmation for a registered and an unknown email | PASS |  |
| R-05 (async) | a wrong password shows the inline error | PASS |  |
| R-06 (async) | direct message: Bob receives Alice's message | PASS |  |
| R-07 (async) | Alice's message shows the blue read tick once Bob has it open | PASS |  |
| R-08 (async) | Bob's reply reaches Alice live | PASS |  |
| R-09 (async) | direct-chat events were written (sent, delivered, read) and every one was published by the dispatcher | PASS | 6 events, 0 waiting |
| R-10 (async) | no uncaught JavaScript errors in any window | PASS |  |

**With async replies off, the default** (10/10 passed): the same flow, the old synchronous `200`.

| ID | Check | Result | Evidence |
|---|---|---|---|
| R-01 (sync) | register through the form lands on the chat page | PASS |  |
| R-02 (sync) | a real AI reply arrives (sync: 200 with both messages) through the real Python service and DeepInfra | PASS | "Pong" after 1.7 s |
| R-03 (sync) | the stored job state matches the mode: async leaves one finished job, sync leaves none | PASS | jobs=0 |
| R-04 (sync) | forgot-password shows the same confirmation for a registered and an unknown email | PASS |  |
| R-05 (sync) | a wrong password shows the inline error | PASS |  |
| R-06 (sync) | direct message: Bob receives Alice's message | PASS |  |
| R-07 (sync) | Alice's message shows the blue read tick once Bob has it open | PASS |  |
| R-08 (sync) | Bob's reply reaches Alice live | PASS |  |
| R-09 (sync) | direct-chat events were written (sent, delivered, read) and every one was published by the dispatcher | PASS | 6 events, 0 waiting |
| R-10 (sync) | no uncaught JavaScript errors in any window | PASS |  |

### 15.7 Findings

| # | Severity | Finding | Evidence and cause | Resolution |
|---|---|---|---|---|
| F31 | **Medium (real defect, found by the load test, before release)** | **Each replica ran only one AI call at a time**, whatever the configured prefetch of 4. | `P18-25` failed: 40 jobs, "max concurrent = 2" across two replicas (expected up to 8). RabbitMQ.Client delivers a channel's messages to its consumer one at a time unless the channel is created with a dispatch concurrency; prefetch only limits how many are in flight. | **Fixed:** the worker creates its channel with `consumerDispatchConcurrency` equal to the prefetch (the email worker too). Re-run: exactly 8 concurrent calls (4 per replica), 40 calls for 40 jobs. |
| F32 | **Medium (real defect, found by the two-replica test, before release)** | **Repeated events were only ignored within one replica.** | `P19-26` failed: an event published twice with the same id was handled twice, because the two copies were delivered to the subscribers on different replicas and each kept its own memory. | **Fixed:** de-duplication now uses Redis (`SET NX`, 24 h) when Redis is configured (in memory otherwise, and it lets the event through if Redis is down). Re-run: handled once, repeat ignored. |
| F33 | Info (test setup, not the app) | Harness slips: the stub did not read a chunked request body (.NET sends the AI call that way), SQL with quotes broke the shell, and de-duplication keys in Redis outlived a reset of the test database. | Each time the application behaved correctly; the scripts were fixed (chunked parsing, SQL through stdin, Redis keys cleared with the database). | None needed. |
| F34 | Low (limit, recorded) | The de-duplication keys live 24 h, so if the database were restored to an older state (outbox ids reused), events with a reused id would be ignored for up to 24 h. | Only visible in test environments that reset the database; production ids only grow. | Documented. |
| F35 | Low (by design) | Event order is exact with one replica and best effort with several (two dispatchers can publish neighbouring rows in either order). | Rows are claimed and published in id order per replica. | Documented; each event carries its outbox id and time. |
| F36 | Low (by design) | While a reply is pending the person cannot send a second question in that chat. If Redis is down the live push is lost; the page finds the reply by itself within about 15 s (`B18-11`: 16 s). | Keeps the conversation order simple; the safety-net poll repairs a missed push. | Accepted. |
| F37 | Low | The in-memory test database used by unit tests has no transactions or rollback, so atomicity cannot be proved there. | An InMemory limitation. | Proved on real MySQL instead: `P19-10`/`P19-11` (the request fails and the message is not saved when its event cannot be written) and `P18-27`/`P18-28` (a replica killed mid-work, every job still finished with exactly one reply). |
| F38 | Info (resolves an item of Phase T6) | The migration drift check does not need a database. | `ASPNETCORE_ENVIRONMENT=Production dotnet ef migrations has-pending-model-changes` ran with the host MySQL stopped. Adding a migration works the same way with `--context AppDbContext`. | The T6 item is ticked; only putting it in a script or CI remains. |
| F39 | Info | One Docker safety check blocked a command that removed a container by name inside `bash -c`; it was not worked around (the orphan was removed by `compose --remove-orphans` instead). | | None needed. |

### 15.8 Not covered by this run

- ~~A black-holed network or a RabbitMQ partition~~: closed in section 16 (containers paused; it found and fixed F40).
- ~~More than two replicas~~: closed in section 16 (three). **RabbitMQ clustering** and **Redis Sentinel/Cluster** remain unsupported and untested (F43).
- ~~Real LLM failures and slow replies~~: closed in section 16 (invalid provider key with the real service; a 65 s call).
- ~~Very large backlogs~~: closed in section 16 (3,000 events).
- **The long Phase 12 to 14 browser suites** and a **committed `e2e/` folder** (Phase T6).
- **Per-chat ordering of async replies** when several questions are pending in the same chat: not possible from the page (the composer is locked), so only tested through the API.
- Load, security and penetration testing are out of scope (section 1).

## 16. Gap-closing run for the broker phases (2026-10-23)

Section 15 listed things that were "not covered". This run closes the ones that can be tested here, on the same Docker stack: **a black-holed RabbitMQ and Redis** (the containers are *paused*, so connections stay open and nobody answers, which is what a dropped network looks like, unlike a stopped container that refuses at once), **three API replicas**, **a 3,000-event backlog**, **an AI call slower than the API's 60 s timeout**, and **the real Python service failing with the real provider** (an invalid DeepInfra key). It found a real defect (16.5, F40), which was fixed and re-tested, and then sections 15.2, 15.3 and 15.5 were run again on the final build (all passed: 40/40, 30/30, 17/17). Throwaway accounts only; all test data was deleted afterwards and the stack stopped. The host dev MySQL was not touched.

### 16.1 Automated suites

| Suite | Result |
|---|---|
| .NET (`dotnet test`) | **237 passed, 0 failed** (232 before + 5 new: the broker deadline) |
| Python (`pytest`) | **12 passed, 0 failed** (unchanged) |
| Angular unit (`ng test`) | **188 passed, 0 failed** (unchanged) |
| Migration drift | None (`ASPNETCORE_ENVIRONMENT=Production dotnet ef migrations has-pending-model-changes`) |
| **Total** | **437 passed, 0 failed** |

New unit tests (`BrokerTimeoutTests`): a broker call that never answers becomes `BrokerUnavailableException` after the deadline and is not a cancellation; a caller who cancels still gets an ordinary cancellation; a library `TimeoutException` is also reported as broker unavailable; a fast call runs normally and other errors pass through unchanged; the default deadline is short (1 to 10 s).

### 16.2 Black-holed RabbitMQ and Redis, three replicas, backlog, slow AI (24/24 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| P20-01 | three API replicas (ports 18081, 18082, 18083) are up and healthy behind one MySQL, Redis and RabbitMQ | PASS |  |
| P20-02 | async send with the broker black-holed: a prompt 503 "ai_service_unavailable" (not a 500, not a 20 s wait), the question stays saved, no orphan job | PASS | status 503 after 8282 ms |
| P20-03 | forgot-password with the broker black-holed: the SAME generic 200 for a registered and an unknown email, promptly (no 500 that would reveal accounts) | PASS | known 200 in 8435 ms, unknown 200 in 19 ms |
| P20-04 | direct chat keeps working while the broker is black-holed: 30 messages all 201, each fast (the outbox only writes to MySQL) | PASS | slowest 752 ms |
| P20-05 | their 30 events wait in the database (undispatched) and every replica stays healthy | PASS | waiting events: 30 |
| P20-06 | broker answers again: every waiting event is published by itself (no restart) | PASS |  |
| P20-07 | all three replicas re-attach their reply workers, and a new question is answered normally | PASS | {"m":0,"c":3} |
| P20-08 | Redis black-holed: a direct message is still saved (201) and the list still loads, each within a few seconds (2 s Redis timeouts), never hanging | PASS | send 4614 ms, list 3024 ms |
| P20-09 | Redis black-holed: an async reply is still produced and saved (only the live push is lost) | PASS |  |
| P20-10 | Redis black-holed: /health stays 200 on all three replicas | PASS |  |
| P20-11 | Redis answers again: pushes cross replicas again with no restart and no reconnect | PASS |  |
| P20-12 | 60 questions over three replicas: all answered, the AI called exactly 60 times, never more than 12 at once (prefetch 4 x 3) and more than 8 (all three replicas worked) | PASS | calls 60, max concurrent 12 |
| P20-13 | every person received exactly one AssistantReplied per question (10 each), on whichever replica they were connected to | PASS |  |
| P20-14 | 15 people each open a tab on all three replicas at the same instant: exactly ONE "came online" announcement per person | PASS | counts: 1 |
| P20-15 | and exactly ONE "went offline" announcement per person after all three tabs close at the same instant | PASS | counts: 1 |
| P20-16 | 150 direct messages sent through three replicas: three dispatchers publish every event exactly once (no row published twice, none missing) | PASS | 150 published, 150 distinct, 150 new rows |
| P20-17 | the subscriber, competing across three replicas, saw no repeats | PASS |  |
| P20-18 | one of three replicas is killed in the middle of AI calls: the other two finish all 9 jobs, each with exactly one reply | PASS |  |
| P20-19 | RabbitMQ down: 3,000 messages are all accepted (201) while their events wait in the database | PASS | 201s: 3000 in 16 s, events waiting: 3000 |
| P20-20 | the API stays healthy with that backlog | PASS |  |
| P20-21 | RabbitMQ back: the whole backlog drains by itself, none lost, none published twice | PASS | 3000 events (3000 distinct) in about 85 s |
| P20-22 | the subscriber handled every one of them (log lines carry ids only), and its queues are empty | PASS | 3000 distinct events logged |
| P20-23 | an AI call that outlasts the 60 s timeout counts as a failed attempt: after 3 attempts the person gets AssistantFailed (about 3 minutes) | PASS | after 186 s, attempts 3 |
| P20-24 | with the lease (90 s) longer than the AI timeout (60 s) no other worker steals the job: the AI was called exactly 3 times, once per attempt, and no reply was saved | PASS | AI calls 3 |

### 16.3 The 3,000-event backlog, re-run with the log of all three replicas read (5/5 passed)

The first run failed one check (`P20-22`, 2,122 of 3,000 events logged) because the script read the logs of two of the three replicas only; this run reads all three. It re-ran the whole backlog part.

| ID | Check | Result | Evidence |
|---|---|---|---|
| P20-01 | three API replicas (ports 18081, 18082, 18083) are up and healthy behind one MySQL, Redis and RabbitMQ | PASS |  |
| P20-19 | RabbitMQ down: 3,000 messages are all accepted (201) while their events wait in the database | PASS | 201s: 3000 in 16 s, events waiting: 3000 |
| P20-20 | the API stays healthy with that backlog | PASS |  |
| P20-21 | RabbitMQ back: the whole backlog drains by itself, none lost, none published twice | PASS | 3000 events (3000 distinct) in about 85 s |
| P20-22 | the subscriber handled every one of them (log lines carry ids only), and its queues are empty | PASS | 3000 distinct events logged |

### 16.4 Real Python service and real provider, invalid key, then the right key (7/7 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| P20F-01 | async send answers 202 at once even though the real AI provider will reject the request | PASS | status 202 in 260 ms |
| P20F-02 | the provider's real rejection (401) is retried (5 s, then 20 s) and then reported as AssistantFailed with a safe message | PASS | after 28 s: "The assistant couldn't reply this time." |
| P20F-03 | the job is failed after 3 attempts, no reply was invented, and the job id sits in the dead-letter queue | PASS | {"status":"failed","attempts":3,"hasReply":false} |
| P20F-04 | what is stored for the operator does not contain the API key or any secret | PASS | The AI service returned 502. |
| P20F-05 | with the right key, Send again (retry) gets a REAL reply from the real LLM for the same question, with no duplicate question | PASS | "pong" |
| P20F-06 | default mode with a bad key: the answer is 503 "ai_service_unavailable" (never a 500), the question is saved, and the message tells the person to try again | PASS | status 503 in 1457 ms |
| P20F-07 | with the right key restored the default mode answers normally again (200, real reply) | PASS | status 200 |

The email path was re-checked after the change: `P20-EM` PASS (a password-reset email is still published and picked up by the worker; generic `200`, the first send attempt is logged because SMTP is unconfigured here).

### 16.5 Findings

| # | Severity | Finding | Evidence and cause | Resolution |
|---|---|---|---|---|
| F40 | **Medium (real defect, found by this run, before release)** | **With RabbitMQ black-holed (reachable but not answering) the async send and forgot-password both answered `500` after 20 s.** The forgot-password `500` also broke the "same generic 200 for every email" rule (it would have revealed which emails have accounts). | Probe before the fix: "async ask: status 500 after 20225 ms; forgot password: status 500 after 20084 ms". The client library gave up after its own 20 s and raised a timeout that surfaced as a cancellation; the callers deliberately re-throw cancellations (a cancelled request is not an error), so it escaped as a 500. A stopped container (connection refused) never showed this. | **Fixed:** every broker call made during a request now runs under a short deadline (`RabbitMq:PublishTimeoutSeconds`, default 5) through `RabbitMqConnection.WithTimeoutAsync`, which turns a missed deadline into `BrokerUnavailableException` (an ordinary failure). The library's own waits were shortened too. After the fix: async send gives a prompt `503` with the question kept; forgot-password gives the same generic `200` promptly; the dispatcher and workers recover when the broker answers again. 5 unit tests. |
| F41 | Low (capacity limit, recorded) | The outbox dispatcher publishes about 35 events per second across three replicas (3,000 events drained in about 85 s). | Each event costs a claim save, a confirmed publish and a mark save. A sustained rate above this would build a backlog (it would still drain once the load drops, and chat is not slowed). | Documented. If it ever matters: claim and mark a batch in one statement. |
| F42 | Info (test setup, not the app) | The first backlog run read the logs of two replicas instead of three. | The check read 2,122 of 3,000 log lines; the re-run reads all three and sees 3,000. | None needed. |
| F43 | Limit (design, not a defect) | **RabbitMQ clustering and Redis Sentinel/Cluster are not supported.** Queues are classic, single-node and not replicated, and the Redis connection is a single endpoint. | Making them highly available needs quorum queues, a cluster in compose and Sentinel-aware connection settings: a feature, not a test. | Recorded as an open item in Phase T6; not built. |
| F44 | Info | An AI call that outlasts the API's 60 s timeout is correctly a failed attempt, and a lease longer than that timeout (90 s) keeps other workers from taking the job over. With a shorter lease a slow call would be started twice (still safe: only one reply is kept, but the AI is paid for twice). | `P20-23`/`P20-24`: 3 attempts, 3 AI calls, no reply. | The lease default (90 s) stays above the timeout; documented next to `Chat:ReplyLeaseSeconds`. |

### 16.6 Still not covered

- **RabbitMQ clustering** and **Redis Sentinel/Cluster** (F43): not built, so not tested.
- **The long Phase 12 to 14 browser suites** as a committed `e2e/` folder (Phase T6).
- **A packet-dropping network device** (a firewall that silently discards, not a paused process): pausing the container is the closest thing available here.
- **More than three replicas**, and **backlogs far larger than 3,000** events.
- Load, security and penetration testing are out of scope (section 1).

## 17. Phase 20: the closing end-to-end run (2026-10-23)

Phase 20 is the check of the whole system in one pass, and it also closes the long-standing gap that the end-to-end checks lived in
throw-away scripts: **every suite is now committed under `e2e/` and runs with one command, `./e2e/run.sh`** (see `e2e/README.md`).
The suites drive the **real Docker stack**: real MySQL 8.4, RabbitMQ 3.13, Redis 7, nginx, up to three API replicas behind a load
balancer, real SignalR clients over WebSockets, and real Chrome windows. In the first configuration the Python AI service is replaced by
a stub with the same contract that counts calls and obeys latency and failure commands; the last three suites use the real Python
service and the real LLM provider. The full run below is a single uninterrupted `./e2e/run.sh` on the final build; it empties the test
data it wrote and stops the stack when it ends. The host dev MySQL was not touched.

### 17.1 Automated suites

| Suite | Result |
|---|---|
| .NET (`dotnet test`) | **239 passed, 0 failed** (237 before + 2: the Redis connection settings) |
| Python (`pytest`) | **12 passed, 0 failed** (unchanged) |
| Angular unit (`ng test`) | **188 passed, 0 failed** (unchanged) |
| Migration drift | None (`ASPNETCORE_ENVIRONMENT=Production dotnet ef migrations has-pending-model-changes`) |
| **Total automated** | **439 passed, 0 failed** |

### 17.2 The end-to-end suites (261/261 checks passed)

| Suite | What it covers | Checks | Result |
|---|---|---|---|
| `01-foundation` | Phase 15: broker foundation | 16 | 16/16 |
| `02-email-queue` | Phase 17: password-reset email through RabbitMQ | 30 | 30/30 |
| `03-presence-and-backplane` | Phase 16: Redis backplane and shared presence (and the negative control) | 38 | 38/38 |
| `04-direct-chat-browser` | Phases 9 to 14: the whole user-to-user chat in real browsers across replicas | 19 | 19/19 |
| `05-async-replies` | Phase 18: async AI replies | 40 | 40/40 |
| `06-outbox-events` | Phase 19: direct-chat events through the outbox | 30 | 30/30 |
| `07-black-hole-and-scale` | After 18 and 19: black-holed broker and Redis, three replicas, 3,000-event backlog, slow AI | 24 | 24/24 |
| `08-async-replies-browser` | Phase 18: async replies in the chat page | 17 | 17/17 |
| `09-chaos` | Phase 20: combined and sequential failures with consistency invariants | 20 | 20/20 |
| `10a-real-llm-failure` | Real Python service and provider: invalid key, then the right key | 7 | 7/7 |
| `10b-regression-sync` | Regression with the real AI service, default (synchronous) mode | 10 | 10/10 |
| `10b-regression-async` | Regression with the real AI service, Chat:AsyncReplies on | 10 | 10/10 |
| **Total** | | **261** | **261/261** |

Every check, with the evidence it recorded, is listed below.

### 15.1 `01-foundation`: Phase 15: broker foundation (16/16 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| FND-01 | rabbitmq and redis are running and report healthy | PASS | rabbitmq=healthy redis=healthy |
| FND-02 | Redis, MySQL and the AMQP port of RabbitMQ are NOT published outside Docker (only RabbitMQ's management port is, for the test harness) | PASS | redis: [], mysql: [], rabbitmq: [15672->15673] |
| FND-03 | Redis refuses a client without the password | PASS |  |
| FND-04 | Redis refuses a wrong password | PASS |  |
| FND-05 | Redis answers PONG with the real password | PASS |  |
| FND-06 | RabbitMQ has no default 'guest' login | PASS | user 'guest' - invalid credentials |
| FND-07 | RabbitMQ accepts the generated 'aichat' login | PASS |  |
| FND-08 | RabbitMQ rejects a wrong password for aichat | PASS |  |
| FND-09 | every API replica answers /health 200 (liveness only, no broker check) | PASS |  |
| FND-10 | every replica started its email worker, AI reply worker, outbox dispatcher and subscriber | PASS | start-up lines in the logs |
| FND-11 | the topology exists and is durable: email.send/dead, ai.reply/dead, direct.events.log/dead and their retry queues | PASS | ai.dead, ai.reply, ai.retry.12s, ai.retry.2s, ai.retry.3s, direct.events.dead, direct.events.log, email.dead, email.retry.10s, email.retry.60s, ema... |
| FND-12 | the exchanges are the agreed ones: email (direct), ai (direct), direct.events (topic) | PASS | ai:direct, direct.events:topic, email:direct |
| FND-13 | existing behaviour is unchanged: register then login work with all the brokers running | PASS |  |
| FND-14 | error shape unchanged: an unauthenticated call is 401 with { error: { code, message } } | PASS |  |
| FND-15 | error shape unchanged: an unknown route is 404 with { error: { code, message } } | PASS |  |
| FND-16 | a connected person is recorded in Redis (presence:user:<id>) and SignalR uses its channels under the "aichat" prefix (Phase 16 wiring) | PASS | channels: 12 (e.g. aichatAiChatAssistant.Api.Hubs.DirectHub:internal:ack:2577525d2dd7_9a025b24193c4cb2bf825c1a5c408ad2) |

### 15.2 `02-email-queue`: Phase 17: password-reset email through RabbitMQ (30/30 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| E-01 | same status and body for a registered and an unregistered email | PASS | 200 / 200: If that email is registered, a password reset link has been sent. |
| E-02 | the worker delivered the registered user's email (via the queue) | PASS |  |
| E-03 | no email was produced for the unregistered address | PASS |  |
| E-04 | end to end: the emailed link resets the password (new works, old fails) | PASS | reset 200, new 200, old 401 |
| E-05 | queues are empty afterwards (acknowledged; nothing stuck in retry or dead-letter) | PASS | {"email.dead":{"m":0,"c":0},"email.send":{"m":0,"c":1},"ai.retry.3s":{"m":0,"c":0},"ai.retry.2s":{"m":0,"c":0},"ai.reply":{"m":0,"c":1},"ai.retry.1... |
| E-06 | the worker holds exactly one consumer on email.send | PASS |  |
| E-07 | the request is not slowed or failed by the mail failure: 200 generic answer right away | PASS | 200 in 169 ms |
| E-08 | after 3 failed attempts (waits 2 s and 3 s) the email lands in the dead-letter queue | PASS | dead-lettered after 6.5 s (waits sum to 5 s) |
| E-09 | logs show attempt 1 of 3, attempt 2 of 3 retrying, then giving up on attempt 3 of 3 | PASS |  |
| E-10 | the dead-letter message carries the attempt count and the error, and the other queues are empty | PASS | x-attempt=3 |
| E-11 | the dead-letter message is persistent and holds the original payload | PASS |  |
| E-12 | the retry queues are named after their delays (email.retry.2s / email.retry.3s) | PASS |  |
| E-13 | broker down: the answer is still the same 200 generic message (no 500, no account revealed) | PASS | known 200 in 270 ms, unknown 200 in 45 ms |
| E-14 | broker down: the failure is logged for the operator (by user id, not email) | PASS |  |
| E-15 | broker down: the rest of the app keeps working (login 200) | PASS |  |
| E-16 | broker back: the worker reconnects by itself (a consumer is attached again, no API restart) | PASS | {"m":0,"c":1} |
| E-17 | broker back: a new request is queued and delivered normally | PASS |  |
| E-18 | after the first failure the email waits in email.retry.20s | PASS | {"email.dead":{"m":0,"c":0},"email.retry.2s":{"m":0,"c":0},"email.send":{"m":0,"c":1},"email.retry.3s":{"m":0,"c":0},"email.retry.20s":{"m":1,"c":0... |
| E-19 | broker restarted: the waiting email is still there (durable queue, persistent message) | PASS | {"email.dead":{"m":0,"c":0},"email.retry.2s":{"m":0,"c":0},"email.send":{"m":0,"c":1},"email.retry.3s":{"m":0,"c":0},"email.retry.20s":{"m":1,"c":0... |
| E-20 | API restarted too: the message is still processed to the end (nothing lost, nothing duplicated) | PASS | {"email.dead":{"m":1,"c":0},"email.retry.2s":{"m":0,"c":0},"email.send":{"m":0,"c":1},"email.retry.3s":{"m":0,"c":0},"email.retry.20s":{"m":0,"c":0... |
| E-21 | the stale message is dropped by the worker with a log line, never sent | PASS |  |
| E-22 | and nothing is left in any queue, including the dead-letter queue | PASS | {"email.dead":{"m":0,"c":0},"email.retry.2s":{"m":0,"c":0},"email.retry.30s":{"m":0,"c":0},"email.send":{"m":0,"c":1},"email.retry.3s":{"m":0,"c":0... |
| E-23 | 40 simultaneous requests: all answered 200 with the same message | PASS | slowest 1677 ms |
| E-24 | every one of the 40 emails was delivered exactly once | PASS | 40/40 delivered once each, in 27.5 s |
| E-25 | queues drain to empty | PASS | {"email.dead":{"m":0,"c":0},"email.retry.2s":{"m":0,"c":0},"email.retry.30s":{"m":0,"c":0},"email.send":{"m":0,"c":1},"email.retry.3s":{"m":0,"c":0... |
| E-26 | no broker: the email is still produced (inline) and the answer is the same generic 200 | PASS | 200 in 329 ms |
| E-27 | no broker: no worker starts and the app never tries to connect to RabbitMQ | PASS |  |
| E-28 | the API refuses to start with a clear message naming the missing setting | PASS | Unhandled exception. System.InvalidOperationException: RabbitMq:Host is set but RabbitMq:Username / RabbitMq:Password are not. Set them via user-se... |
| E-29 | a paused broker: forgot-password still gives the same generic 200 (never a 500 that would reveal accounts), within the deadline, not 20 s | PASS | known 200 in 8476 ms, unknown 200 in 45 ms |
| E-30 | and after it answers again the worker is attached and a new reset email is delivered | PASS |  |

### 15.3 `03-presence-and-backplane`: Phase 16: Redis backplane and shared presence (and the negative control) (38/38 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| P-01 | two API replicas are up and healthy (ports 18081 and 18082), sharing one MySQL, Redis and RabbitMQ | PASS |  |
| P-02 | both people are recorded in Redis presence (one key per user) | PASS | presence:user:1, presence:user:2 |
| P-03 | each presence key has an expiry, so an abandoned key cleans itself up | PASS | PTTL 16501 ms |
| P-04 | Alice (replica 1) sees Bob (connected to replica 2) as online in the conversation list | PASS |  |
| P-05 | Bob (replica 2) sees Alice (replica 1) as online | PASS |  |
| P-06 | a message sent through replica 1 is "delivered" at once because Bob is online on replica 2 (shared presence) | PASS | status=delivered |
| P-07 | Bob, connected to replica 2, receives the live MessageReceived push from replica 1 (Redis backplane) | PASS | payload: "hello bob (sent through replica 1)" |
| P-08 | Bob marks it read on replica 2; Alice's read tick arrives live on replica 1 | PASS |  |
| P-09 | Alice's typing signal (replica 1) reaches Bob (replica 2) | PASS |  |
| P-10 | delete through replica 1 is pushed to both people, on both replicas | PASS | delete status 200 |
| P-11 | Carol's first tab (replica 1) makes her come online: Alice is told exactly once | PASS |  |
| P-12 | Carol's second tab on the OTHER replica does not announce her online again | PASS |  |
| P-13 | closing tab 1 (replica 1) does not take her offline: no event, still online per the other replica | PASS |  |
| P-14 | closing the last tab (replica 2) takes her offline: exactly one event, with a last-seen time | PASS |  |
| P-15 | afterwards the list shows Carol offline with the saved last-seen time | PASS | 2026-10-23T17:14:42.136689Z |
| P-16 | exactly ONE "came online" announcement per person, even with two simultaneous first connections on different replicas (atomic Lua) | PASS | counts: 1 over 20 people |
| P-17 | exactly ONE "went offline" announcement per person after both tabs close at the same instant | PASS | counts: 1 over 20 people |
| P-18 | Bob has been idle for 12 s (twice the 6 s mark lifetime) and is still online: the heartbeat refreshes his mark | PASS |  |
| P-19 | Bob's replica is killed: within the mark lifetime (plus one beat) Alice's replica reports him offline, with no manual cleanup | PASS |  |
| P-20 | a message to him now stays at "sent" (one grey tick), not falsely "delivered" | PASS | status=sent |
| P-21 | KNOWN LIMIT (recorded, not a requirement): a crashed replica sends no "went offline" event and saves no last-seen time | PASS |  |
| P-22 | Bob reconnects to the surviving replica: his waiting message becomes "delivered" and Alice is told live | PASS |  |
| P-23 | the killed replica starts again and is healthy | PASS |  |
| P-24 | Redis down: sending a message still works (201, saved) and is not a 500 | PASS | status 201 in 2754 ms, delivery status=sent |
| P-25 | Redis down: the request is slowed by seconds at most, not left hanging (2 s Redis timeouts) | PASS | 2754 ms |
| P-26 | Redis down: the conversation list still loads (presence falls back to this server) | PASS | status 200 in 20 ms |
| P-27 | Redis down: /health stays 200 on both replicas (liveness only, no dependency check) | PASS |  |
| P-28 | Redis down: the message was saved and Bob can read it (REST is unaffected) | PASS |  |
| P-29 | Redis back: live pushes cross replicas again WITHOUT reconnecting Bob or restarting anything | PASS | delivery status=delivered |
| P-30 | Redis loses all its data (FLUSHALL): it held the connections' marks before, so this really wipes them | PASS | 2 presence marks before the flush |
| P-31 | within a heartbeat the replicas re-create their connections' marks and Bob shows online again, with nobody reconnecting | PASS |  |
| P-32 | when everyone has disconnected no presence keys are left in Redis | PASS |  |
| P-33 | without Redis, a message to someone on the OTHER replica is only "sent" (presence is not shared) | PASS | status=sent |
| P-34 | without Redis, the other replica gets NO live push | PASS |  |
| P-35 | without Redis, Alice cannot see that Bob is online (he is on the other replica) | PASS |  |
| P-36 | without Redis, two people on the SAME replica still work (delivered and pushed): the single-replica mode is intact | PASS |  |
| P-37 | WITH the negotiate step, connections through the balancer fail when negotiate and socket land on different replicas (the proof the change is needed) | PASS | 10/30 connected with negotiation |
| P-38 | WITHOUT the negotiate step (what the app does), every connection through the balancer succeeds | PASS | 30/30 connected |

### 15.4 `04-direct-chat-browser`: Phases 9 to 14: the whole user-to-user chat in real browsers across replicas (19/19 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| DC-01 | searching by name finds exactly that person and shows the name only (no email anywhere on the page) | PASS |  |
| DC-02 | ONE grey tick (sent) while the other person is offline | PASS |  |
| DC-03 | Bob signs in: Alice's tick turns TWO grey (delivered) live, she sees him "Online", and he has badge 1 but NO toast (the message was already waiting) | PASS | delivered ticks: 1, status: "Online", Bob's badge: 1, toasts: 0 |
| DC-04 | a live message while Bob is on the chatbot page: a toast with the sender and a preview, and the badge goes to 2 | PASS | toasts: "Alice Rao: m2 are you there? ×", badge: 2 |
| DC-05 | Bob sees "2" unread in the list, opens the chat: Alice's ticks turn BLUE (read) live and his badge and unread pill clear | PASS |  |
| DC-06 | typing: Alice sees "typing…" while Bob types; sending ends it at once and the message arrives | PASS |  |
| DC-07 | a hidden tab: the message counts as unread (badge 1) and is NOT marked read; when the tab is visible again it is read and Alice's tick turns blue | PASS | while hidden: ok=true; after visible: badge 0, blue ticks 3 |
| DC-08 | two tabs of the same person stay in step: a message shows badge 1 in both, and reading it in one clears the badge in the other | PASS | badges: second tab 1, first tab 1; afterwards: second tab 0, first tab 0 |
| DC-09 | delete: Alice deletes her first message; it becomes a placeholder for Alice and, live, for Bob; the text is gone from the database | PASS | Bob sees placeholder: true; Alice sees it: true; rows still holding the text: 0 |
| DC-10 | Bob signs out: Alice sees "Last seen today at …" live, the same after a reload, and a message sent now stays at ONE grey tick | PASS |  |
| DC-11 | Bob signs back in: that message becomes delivered live on Alice's screen | PASS |  |
| DC-12 | a conversation longer than 50 messages: the page loads the newest 50, "Show earlier messages" loads the rest (66 = 66 in the database) | PASS | first page 50, then 66 |
| DC-13 | two people starting the same chat at the same instant (8 pairs): no errors and exactly one conversation per pair | PASS |  |
| DC-14 | privacy: an Admin and an outsider get 404 on someone else's conversation (read, send, mark read, delete) and see no conversation listed | PASS | probes: 404,404,404,404,404,404,404,404 |
| DC-15 | accessibility scan (axe-core WCAG 2 A/AA) of the Messages screen in light and dark: no serious or critical violations | PASS | none |
| DC-16 | on a phone-width screen (390 px) there is no horizontal overflow | PASS |  |
| DC-17 | Alice and Bob are connected to DIFFERENT API replicas behind the same balancer, and everything above worked across them | PASS | Alice -> aichat-api-1, Bob -> aichat-api2-1 |
| DC-18 | Bob's replica is killed: his browser reconnects to a surviving replica on its own and Alice's next message reaches him live | PASS | aichat-api2-1 died; Bob now on aichat-api3-1 |
| DC-19 | no uncaught JavaScript errors in any of the windows | PASS |  |

### 15.5 `05-async-replies`: Phase 18: async AI replies (40/40 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| P18-01 | two API replicas, RabbitMQ, Redis, MySQL and the stub AI service are up; async replies are on (Chat:AsyncReplies=true) | PASS |  |
| P18-02 | POST a message: 202 with replyStatus "pending" and a job id, in well under the 3 s the AI takes (the request does not wait) | PASS | status 202 in 160 ms |
| P18-03 | the question is saved at once (alone), and the chat lists the reply as pending | PASS | messages=1, open jobs=["pending"] |
| P18-04 | Alice's browser (connected to the OTHER replica) receives AssistantReplied with the job, chat, question id and the reply | PASS | after 3312 ms: "stub reply to: What is DNS?" |
| P18-05 | the reply took about as long as the AI did (3 s), proving the work happened in the background | PASS | 3312 ms |
| P18-06 | database: the job is done after 1 attempt and points at the reply; the chat has 2 messages and 1 usage log | PASS | {"status":"done","attempts":1,"hasReply":true} |
| P18-07 | reading the chat returns question then reply, and no open jobs remain | PASS |  |
| P18-08 | the second question sent the first exchange as context (history of 2) and the AI was called exactly once per job | PASS | histories=[0,2], calls=2 |
| P18-09 | the queues are empty afterwards (acknowledged; nothing in retry or dead-letter) | PASS | {"email.dead":{"m":0,"c":0},"email.retry.2s":{"m":0,"c":0},"email.retry.30s":{"m":0,"c":0},"email.send":{"m":0,"c":2},"email.retry.3s":{"m":0,"c":0... |
| P18-10 | one failed AI call is retried by itself: the reply still arrives, attempt 2, no failure shown to anyone | PASS | {"status":"done","attempts":2,"hasReply":true} |
| P18-11 | when every attempt fails (waits of 2 s and 3 s) Alice is told with AssistantFailed, carrying a safe message and no internals | PASS | after 5.2 s: "The assistant couldn't reply this time." |
| P18-12 | database: the job is failed after exactly 3 attempts (the first try plus two retries), with no reply; the AI was called 3 times | PASS | {"status":"failed","attempts":3,"hasReply":false} |
| P18-13 | the dead-letter queue holds that one job, and the message contains only the job id (no question, no answer) | PASS | {"JobId":"c8825967-7d73-499c-b6d3-d576054df6ba"} |
| P18-14 | after a reload the chat still shows that question with a failed reply (it survives the page) | PASS |  |
| P18-15 | Send again (retry): 202, the SAME job is run again and succeeds with no new question; attempts start over at 1 | PASS | {"status":"done","attempts":1,"hasReply":true} |
| P18-16 | retrying a reply that is already delivered is 409; someone else retrying it is 404 | PASS |  |
| P18-17 | DUPLICATE DELIVERY: the broker delivers an already-finished job again: no second AI call, no second reply, no second usage log | PASS |  |
| P18-18 | an unreadable message is moved to the dead-letter queue, and the worker keeps going | PASS |  |
| P18-19 | a message for a job that does not exist is dropped quietly (not retried forever, not dead-lettered) | PASS |  |
| P18-20 | the worker is still healthy after those: a normal question gets its reply | PASS |  |
| P18-21 | another person cannot see this chat's jobs (404) | PASS |  |
| P18-22 | all 40 requests were accepted (202) and all 40 replies were produced | PASS | accepted 40, done 45 in total |
| P18-23 | every person received exactly one AssistantReplied per question (5 each), none missing, none doubled | PASS |  |
| P18-24 | the AI was called exactly 40 times: one call per job even with two competing workers | PASS | calls=40 |
| P18-25 | backpressure: never more than 8 AI calls at once (prefetch 4 per replica x 2), and more than 4 (both replicas worked) | PASS | max concurrent = 8 |
| P18-26 | database: 40 more done jobs, each pointing at its own reply; replies = jobs; no job done twice | PASS |  |
| P18-27 | killing one replica mid-work loses nothing: all 6 jobs still finish (the survivor takes over after the lease) | PASS | 23 s after the kill |
| P18-28 | each job has exactly ONE reply (no duplicates) even though some were started twice | PASS | attempts: 2,1,2,1,2,1 |
| P18-29 | at least one job was taken over (attempts > 1) and Alice still got all 6 events | PASS | events 6 |
| P18-30 | broker down: the answer is 503 "ai_service_unavailable" (not 500), and the question stays saved | PASS | status 503 in 646 ms |
| P18-31 | broker down: no orphan job is left behind | PASS |  |
| P18-32 | broker down: the rest of the app works (reading the chat, /health) | PASS |  |
| P18-33 | broker back: both replicas re-attach their workers by themselves | PASS | {"m":0,"c":2} |
| P18-34 | broker back: a new question is queued and answered normally | PASS |  |
| P18-35 | Redis down: the reply is produced and saved (job done) even though no push can reach the browser | PASS |  |
| P18-36 | Redis down: reading the chat returns the reply (this is what the page's 15 s safety net and reconnect refetch rely on) | PASS |  |
| P18-37 | Redis back: pushes work again without a reconnect | PASS |  |
| P18-38 | Chat:AsyncReplies=false: the old behaviour is untouched: 200 with both messages in the same response, no job row, one AI call | PASS |  |
| P18-39 | Chat:AsyncReplies=false: the open-jobs list is just empty and no AI-reply worker runs | PASS |  |
| P18-40 | Chat:AsyncReplies=true without RabbitMq:Host: the API refuses to start and says why | PASS | Unhandled exception. System.InvalidOperationException: Chat:AsyncReplies is on but RabbitMq:Host is not set. Async replies need RabbitMQ: se |

### 15.6 `06-outbox-events`: Phase 19: direct-chat events through the outbox (30/30 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| P19-01 | one API replica runs (the second is stopped for the ordering tests); RabbitMQ has the direct.events topic exchange and the subscriber queue; the outbox is empty | PASS |  |
| P19-02 | sending a message (recipient offline) writes a "direct.message.sent" outbox row, which is published within about a second | PASS | row #1, 1077 ms after the POST |
| P19-03 | the whole life of two messages writes exactly these events, in this order (repeat reads and repeat deletes write none) | PASS | direct.message.sent > direct.message.delivered > direct.message.read > direct.message.deleted > direct.message.sent > direct.message.delivered > di... |
| P19-04 | RabbitMQ received the same events, in the same order (message id = outbox id, routing key = event type) | PASS | 7 events: sent > delivered > read > deleted > sent > delivered > read |
| P19-05 | the payload has exactly: conversationId, messageIds, senderId (the author), recipientId, status, at | PASS | at,conversationId,messageIds,recipientId,senderId,status |
| P19-06 | the author is Alice and the other person is Bob in every event (also for "read", which Bob caused) | PASS |  |
| P19-07 | NO MESSAGE TEXT anywhere: not in any outbox row, not in any message that reached RabbitMQ | PASS | rows/messages containing the text: 0 |
| P19-08 | the subscriber consumed and logged every event once (ids only in the log line), and both subscriber queues are empty | PASS | 7 log lines for 7 events |
| P19-09 | the dispatched rows carry a dispatch time; none is left waiting | PASS |  |
| P19-10 | if the event cannot be written, the whole request fails (500 in the standard error shape) ... | PASS | status 500 |
| P19-11 | ... and the message was NOT saved either: the change rolled back with its event (no change without an event) | PASS | messages 2 -> 2 |
| P19-12 | with the table back, sending works again and the event is written | PASS |  |
| P19-13 | broker down: all 20 messages are still sent (201) and 20 events wait undispatched in the database | PASS | 201s: 20, waiting events: 20 |
| P19-14 | broker down: the API stays healthy and keeps trying (attempts are counted on the waiting rows) | PASS | max attempts 3 |
| P19-15 | broker back: every waiting event is published by itself, none lost | PASS | 20 events arrived |
| P19-16 | they arrive in the order they were written (ascending ids, no gaps, no repeats) | PASS |  |
| P19-17 | the subscriber handled each of them once (no repeats needed ignoring) | PASS | "seen before" lines so far: 0 |
| P19-18 | 100 messages sent through both replicas at once: all 100 "sent" events reached RabbitMQ (plus their delivered events) | PASS | sent=100, total=101 |
| P19-19 | EACH OUTBOX ROW WAS PUBLISHED EXACTLY ONCE: the two dispatchers never both took the same row | PASS | 101 messages, 101 distinct ids |
| P19-20 | the subscriber (competing across both replicas) saw no repeats, and the queues drained | PASS | drained in 9.1 s |
| P19-21 | the surviving replica finishes the whole backlog (rows the dead one had claimed are taken over after the lease) | PASS | backlog was 90 |
| P19-22 | every backlog event reached RabbitMQ at least once (a repeat is allowed after a crash, loss is not) | PASS | 90 distinct of 90, 0 repeat(s) |
| P19-23 | if any event was published twice, the subscriber noticed and ignored the repeat | PASS | repeats published: 0, repeats ignored: 0 |
| P19-24 | an event that is not valid JSON is moved to the dead-letter queue, not retried forever | PASS |  |
| P19-25 | an event with no message ids is dead-lettered too | PASS |  |
| P19-26 | the same event delivered twice (same message id) is handled once and the repeat is ignored | PASS | handled 1, ignored 1 |
| P19-27 | the subscriber keeps working after all of that: a real message is still logged | PASS |  |
| P19-28 | events published more than 24 h ago are deleted by the dispatcher (exactly those 5); recent ones are kept | PASS | rows 220 -> 215 |
| P19-29 | Outbox:Enabled=false: chat works (201), no event row is written, and no dispatcher runs | PASS |  |
| P19-30 | no RabbitMQ configured: chat works exactly as before and the outbox writes nothing (the app runs with only MySQL) | PASS |  |

### 15.7 `07-black-hole-and-scale`: After 18 and 19: black-holed broker and Redis, three replicas, 3,000-event backlog, slow AI (24/24 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| P20-01 | three API replicas (ports 18081, 18082, 18083) are up and healthy behind one MySQL, Redis and RabbitMQ | PASS |  |
| P20-02 | async send with the broker black-holed: a prompt 503 "ai_service_unavailable" (not a 500, not a 20 s wait), the question stays saved, no orphan job | PASS | status 503 after 8237 ms |
| P20-03 | forgot-password with the broker black-holed: the SAME generic 200 for a registered and an unknown email, promptly (no 500 that would reveal accounts) | PASS | known 200 in 8449 ms, unknown 200 in 17 ms |
| P20-04 | direct chat keeps working while the broker is black-holed: 30 messages all 201, each fast (the outbox only writes to MySQL) | PASS | slowest 791 ms |
| P20-05 | their 30 events wait in the database (undispatched) and every replica stays healthy | PASS | waiting events: 30 |
| P20-06 | broker answers again: every waiting event is published by itself (no restart) | PASS |  |
| P20-07 | all three replicas re-attach their reply workers, and a new question is answered normally | PASS | {"m":0,"c":3} |
| P20-08 | Redis black-holed: a direct message is still saved (201) and the list still loads, each within a few seconds (2 s Redis timeouts), never hanging | PASS | send 4606 ms, list 2305 ms |
| P20-09 | Redis black-holed: an async reply is still produced and saved (only the live push is lost) | PASS |  |
| P20-10 | Redis black-holed: /health stays 200 on all three replicas | PASS |  |
| P20-11 | Redis answers again: pushes cross replicas again with no restart and no reconnect, within 30 s of it answering (the keep-alive ping notices a silent connection in seconds, not the library's default minute) | PASS | first cross-replica push 2.1 s after Redis was unpaused |
| P20-12 | 60 questions over three replicas: all answered, the AI called exactly 60 times, never more than 12 at once (prefetch 4 x 3) and more than 8 (all three replicas worked) | PASS | calls 60, max concurrent 12 |
| P20-13 | every person received exactly one AssistantReplied per question (10 each), on whichever replica they were connected to | PASS |  |
| P20-14 | 15 people each open a tab on all three replicas at the same instant: exactly ONE "came online" announcement per person | PASS | counts: 1 |
| P20-15 | and exactly ONE "went offline" announcement per person after all three tabs close at the same instant | PASS | counts: 1 |
| P20-16 | 150 direct messages sent through three replicas: three dispatchers publish every event exactly once (no row published twice, none missing) | PASS | 150 published, 150 distinct, 150 new rows |
| P20-17 | the subscriber, competing across three replicas, saw no repeats | PASS |  |
| P20-18 | one of three replicas is killed in the middle of AI calls: the other two finish all 9 jobs, each with exactly one reply | PASS |  |
| P20-19 | RabbitMQ down: 3,000 messages are all accepted (201) while their events wait in the database | PASS | 201s: 3000 in 14 s, events waiting: 3000 |
| P20-20 | the API stays healthy with that backlog | PASS |  |
| P20-21 | RabbitMQ back: the whole backlog drains by itself, none lost, none published twice | PASS | 3000 events (3000 distinct) in about 80 s |
| P20-22 | the subscriber handled every one of them (log lines carry ids only), and its queues are empty | PASS | 3182 distinct events logged |
| P20-23 | an AI call that outlasts the 60 s timeout counts as a failed attempt: after 3 attempts the person gets AssistantFailed (about 3 minutes) | PASS | after 186 s, attempts 3 |
| P20-24 | with the lease (90 s) longer than the AI timeout (60 s) no other worker steals the job: the AI was called exactly 3 times, once per attempt, and no reply was saved | PASS | AI calls 3 |

### 15.8 `08-async-replies-browser`: Phase 18: async replies in the chat page (17/17 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| B18-01 | sending: the question appears at once and "Thinking…" shows while the reply is produced in the background | PASS | question shown after 519 ms |
| B18-02 | while a reply is pending the Send button is disabled (no second question queued behind it) | PASS |  |
| B18-03 | the reply appears by itself (no reload), after about the 4 s the AI took, "Thinking…" goes away | PASS | 5636 ms; reply: "stub reply to: What is DNS?" |
| B18-04 | and the composer is usable again | PASS |  |
| B18-05 | when the AI keeps failing, after the retries (about 5 s) the question shows "The assistant couldn't reply." with Send again | PASS | after 5.9 s |
| B18-06 | no "Thinking…" is left behind and the composer is free | PASS |  |
| B18-07 | Send again: the failed note disappears at once and "Thinking…" returns (same question, nothing re-typed) | PASS |  |
| B18-08 | the reply arrives, and the chat still has exactly the two questions (no duplicate from Send again) | PASS |  |
| B18-09 | reloading the page while a reply is pending still shows the question AND "Thinking…" (restored from the server) | PASS |  |
| B18-10 | and the reply still arrives on the reloaded page | PASS |  |
| B18-11 | if the live push is lost (Redis down) the reply is saved anyway and the page picks it up by itself within about 15-20 s, with no reload | PASS | appeared after 16 s |
| B18-12 | and "Thinking…" is cleared | PASS |  |
| B18-13 | switching to another chat while a reply is pending: that chat is clean, and coming back later shows the finished reply (no stuck "Thinking…") | PASS |  |
| B18-14 | the same person with the chat open in two tabs: the reply appears in BOTH tabs | PASS |  |
| B18-15 | accessibility scan (axe-core WCAG 2 A/AA) of the chat with a pending reply, a failed reply, and in dark theme: no serious or critical violations | PASS | none |
| B18-16 | on a phone-width screen (390 px) the failed state does not overflow | PASS |  |
| B18-17 | no uncaught JavaScript errors in either tab | PASS |  |

### 15.9 `09-chaos`: Phase 20: combined and sequential failures with consistency invariants (20/20 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| X-01 | three replicas, RabbitMQ, Redis and MySQL are up, every reply worker is attached, and the balancer answers | PASS |  |
| X-02 | RabbitMQ stopped while the AI was answering: the worker still saves the reply (job done in the database) although it cannot acknowledge the message | PASS |  |
| X-03 | RabbitMQ back: the unacknowledged message is delivered again and does nothing: still exactly ONE AI call and ONE reply | PASS | AI calls 1 |
| X-04 | Alice received the reply push exactly once, and the queues are empty | PASS |  |
| X-05 | both brokers down: a new async question is refused with a prompt 503 (question kept), direct chat still works (10 of 10 saved), every replica stays healthy | PASS | ask 503 in 72 ms; sends 201201201201201201201201201201 |
| X-06 | the reply that was in flight was still produced and saved while both were down | PASS |  |
| X-07 | both back: the 10 chat events waiting in the database are all published, reply workers re-attach, and the invariants hold | PASS | after C2: duplicate replies 0, done-without-reply 0, reply-but-not-done 0, jobs still open 0, events waiting 0, ai.dead 0, direct.events.dead 0 |
| X-08 | both back: a new question is answered and the live push works again | PASS |  |
| X-09 | rolling restart under load: every ACCEPTED question (81) still gets its reply, none lost | PASS | failed requests during the restarts: 0 (none) |
| X-10 | and each accepted job has exactly one reply (no duplicates), even though workers were killed mid-call | PASS | AI calls 83 for 81 jobs |
| X-11 | every accepted direct message (81) was saved, and every one has a published "sent" event | PASS |  |
| X-12 | after the rolling restart all invariants hold (no stuck jobs, no waiting events, empty dead-letter queues) | PASS | after C3: duplicate replies 0, done-without-reply 0, reply-but-not-done 0, jobs still open 0, events waiting 0, ai.dead 0, direct.events.dead 0 |
| X-13 | MySQL down: requests fail with the standard error shape (not a hang, not an HTML page) within a bounded time, and /health stays 200 | PASS | ask 500 internal_error in 20 ms; list 500 in 31 ms |
| X-14 | MySQL back: the job that was in flight is finished by itself, with exactly one reply | PASS | {"status":"done","attempts":2,"hasReply":true} |
| X-15 | MySQL back: chat works, the outbox dispatcher is still alive and publishes the new event, and the invariants hold | PASS | after C4: duplicate replies 0, done-without-reply 0, reply-but-not-done 0, jobs still open 0, events waiting 0, ai.dead 0, direct.events.dead 0 |
| X-16 | everything restarted at once: all 6 in-flight questions are answered, each exactly once | PASS |  |
| X-17 | and all invariants hold afterwards (nothing stuck, nothing waiting, nothing dead-lettered) | PASS | after C5: duplicate replies 0, done-without-reply 0, reply-but-not-done 0, jobs still open 0, events waiting 0, ai.dead 0, direct.events.dead 0 |
| X-18 | presence and live pushes work again after the big restart (a message reaches Bob over SignalR) | PASS |  |
| X-19 | FINAL CONSISTENCY after every failure above: no duplicate replies, no done job without a reply, no stuck job, no waiting event, empty dead-letter queues | PASS | final: duplicate replies 0, done-without-reply 0, reply-but-not-done 0, jobs still open 0, events waiting 0, ai.dead 0, direct.events.dead 0 |
| X-20 | FINAL: every direct message that exists has its "sent" event (the outbox never lost one, whatever failed) | PASS | messages without a sent event: 0 |

### 15.10 `10a-real-llm-failure`: Real Python service and provider: invalid key, then the right key (7/7 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| P20F-01 | async send answers 202 at once even though the real AI provider will reject the request | PASS | status 202 in 249 ms |
| P20F-02 | the provider's real rejection (401) is retried (5 s, then 20 s) and then reported as AssistantFailed with a safe message | PASS | after 28 s: "The assistant couldn't reply this time." |
| P20F-03 | the job is failed after 3 attempts, no reply was invented, and the job id sits in the dead-letter queue | PASS | {"status":"failed","attempts":3,"hasReply":false} |
| P20F-04 | what is stored for the operator does not contain the API key or any secret | PASS | The AI service returned 502. |
| P20F-05 | with the right key, Send again (retry) gets a REAL reply from the real LLM for the same question, with no duplicate question | PASS | "Pong" |
| P20F-06 | default mode with a bad key: the answer is 503 "ai_service_unavailable" (never a 500), the question is saved, and the message tells the person to try again | PASS | status 503 in 1184 ms |
| P20F-07 | with the right key restored the default mode answers normally again (200, real reply) | PASS | status 200 |

### 15.11 `10b-regression-sync`: Regression with the real AI service, default (synchronous) mode (10/10 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| R-01 (sync) | register through the form lands on the chat page | PASS |  |
| R-02 (sync) | a real AI reply arrives (sync: 200 with both messages) through the real Python service and DeepInfra | PASS | "pong" after 1.7 s |
| R-03 (sync) | the stored job state matches the mode: async leaves one finished job, sync leaves none | PASS | jobs=0 |
| R-04 (sync) | forgot-password shows the same confirmation for a registered and an unknown email | PASS |  |
| R-05 (sync) | a wrong password shows the inline error | PASS |  |
| R-06 (sync) | direct message: Bob receives Alice's message | PASS |  |
| R-07 (sync) | Alice's message shows the blue read tick once Bob has it open | PASS |  |
| R-08 (sync) | Bob's reply reaches Alice live | PASS |  |
| R-09 (sync) | direct-chat events were written (sent, delivered, read) and every one was published by the dispatcher | PASS | 6 events, 0 waiting |
| R-10 (sync) | no uncaught JavaScript errors in any window | PASS |  |

### 15.12 `10b-regression-async`: Regression with the real AI service, Chat:AsyncReplies on (10/10 passed)

| ID | Check | Result | Evidence |
|---|---|---|---|
| R-01 (async) | register through the form lands on the chat page | PASS |  |
| R-02 (async) | a real AI reply arrives (async: 202, then pushed over SignalR) through the real Python service and DeepInfra | PASS | "pong" after 1.6 s |
| R-03 (async) | the stored job state matches the mode: async leaves one finished job, sync leaves none | PASS | jobs=1 |
| R-04 (async) | forgot-password shows the same confirmation for a registered and an unknown email | PASS |  |
| R-05 (async) | a wrong password shows the inline error | PASS |  |
| R-06 (async) | direct message: Bob receives Alice's message | PASS |  |
| R-07 (async) | Alice's message shows the blue read tick once Bob has it open | PASS |  |
| R-08 (async) | Bob's reply reaches Alice live | PASS |  |
| R-09 (async) | direct-chat events were written (sent, delivered, read) and every one was published by the dispatcher | PASS | 6 events, 0 waiting |
| R-10 (async) | no uncaught JavaScript errors in any window | PASS |  |

### 17.15 Findings

| # | Severity | Finding | Evidence and cause | Resolution |
|---|---|---|---|---|
| F45 | **Medium (real defect, found by this run, before release)** | **After a Redis that had gone silent (paused or partitioned) answered again, cross-replica SignalR pushes could stay dead for about a minute.** | `P20-11` failed in one run and passed in another: the black-holed Redis connection was only noticed by the library's default keep-alive of about 60 s, and the backplane's subscription does not reconnect until it notices. | **Fixed:** the Redis connection sends a keep-alive ping every 4 s (`RealtimeExtensions.BuildRedisOptions`), so a silent connection is noticed in seconds. `P20-11` now measures the time from Redis answering to the first cross-replica push and requires 30 s or less; 2 unit tests pin the settings. |
| F46 | Info (recorded) | While **MySQL is down** the API answers `500` with the standard `{ error: { code: "internal_error" } }` body within about 100 ms, not a hang and not a `503`. It recovers by itself, the in-flight reply job finishes with exactly one reply, and the outbox dispatcher keeps working (`X-13` to `X-15`). | A database outage is reported like any unexpected server error. | Left as is: the shape and timing are right; a `503` for database outages could be added later if clients need to tell the two apart. |
| F47 | Info (a good result worth recording) | **Rolling restart of all three replicas under continuous load lost nothing and failed no request** (79 accepted questions, 79 direct messages, 0 failed requests; the AI was called 81 times for 79 jobs, the two extra being calls cut short by a restart and repeated safely). | `X-09` to `X-12`: the balancer moves a request to another replica when one is restarting, and the job lease, claim token and outbox make every restart harmless. | None needed. |
| F48 | Info (test setup, not the app) | The harness itself needed hardening, and now has it: a suite that crashed part-way used to be reported as passed (now every suite writes a "finished" marker and the report shows `CRASH`); `docker compose up` leaves a container "Created" when a dependency is briefly unhealthy (now retried); the balancer will not start if a replica's container is stopped (now every replica is started first); the unread badge holds a hidden "unread" word for screen readers (now only the number is read); headless Chrome treats every window as visible (hidden tabs are simulated); Redis de-duplication keys outlive a database reset (cleared with it). | Found while writing the suites. | Fixed in `e2e/lib.mjs` and the suites. |
| F49 | Resolved (from F22/F30) | **The long Phase 12 to 14 browser suites that were lost are rebuilt and committed**, together with the Phase 15 to 19 suites, as `e2e/` with a single runner. | Suite `04-direct-chat-browser` repeats the whole direct-chat feature in two real browsers across replicas (19 checks), including the hidden tab, two tabs, delete, last seen, paging, races, privacy, accessibility, phone width and a replica dying. | Phase T6's main item is done. |

### 17.16 Not covered

- **RabbitMQ clustering and Redis Sentinel/Cluster**: not supported by the design (single nodes, classic queues), so not tested; recorded in Phase T6.
- **A firewall that silently drops packets**: a paused container (open connections, no answers) is the closest thing available here.
- **More than three replicas**, **backlogs far beyond 3,000 events**, **sustained load or soak testing**.
- **Real SMTP delivery** (no credentials here), **Firefox, Safari and real phones** (Chrome only; phone width is a resized window).
- **Real LLM failure modes other than a rejected key** (rate limits, very slow replies are simulated with the stub).
- Load, security and penetration testing are out of scope (section 1).

## 18. Phase T2: .NET gap tests (2026-10-24)

Closes the .NET gaps listed in section 6. The new tests need no database, broker, mail server or LLM: the pipeline tests start the real `Program.cs` in memory through `WebApplicationFactory<Program>` (test-only package `Microsoft.AspNetCore.Mvc.Testing` 8.0.11, `public partial class Program;` added to `Program.cs`), with the database replaced by EF InMemory, and the AI client, the email queue and the AI-reply queue replaced by mocks (`TestSupport/ApiFactory.cs`). Every token in them is a real JWT made by the real `TokenService` and checked by the real JWT middleware. The async-reply fixture sets `Chat:AsyncReplies` and a RabbitMQ host so start-up validation passes, then removes the broker queue and every background worker.

### 18.1 Automated suites

| Suite | Result |
|---|---|
| .NET (`dotnet test`) | **344 passed, 0 failed** (239 before + 105 new) |
| Python (`pytest`) | **12 passed, 0 failed** (unchanged) |
| Angular unit (`ng test`) | **188 passed, 0 failed** (unchanged) |
| Migration drift | None |
| **Total automated** | **544 passed, 0 failed** |

The end-to-end suites in `e2e/` were not re-run: the only application changes are the cancellation fix below (one exception filter) and the `Program` declaration, neither on a path those suites exercise.

### 18.2 The new tests

| Class | Tests | What it checks |
|---|---|---|
| `AdminControllerTests` | 9 | ping; page size capped at 100 and floored at 1; newest first, page below 1 becomes 1, last partial page; empty table; `from`/`to` inclusive, each alone, `from` = `to`; `from` > `to` is `400 validation_error`; joined user name, email, session title |
| `TokenServiceTests` | 8 | `sub`/`email`/`role` (several roles); unique `jti`; 60-minute default and configured expiry (and the token's own `exp` agrees); issuer/audience defaults and overrides; valid with its key, invalid with another; tampered payload rejected; missing `Jwt:Key` fails with a message naming it |
| `PythonAiClientTests` | 12 | POST `/generate`; snake_case body; `X-Internal-Key`; empty history is `[]`; reply and `tokens_used` mapped; `401`/`422`/`500`/`502`/`503` become `PythonServiceUnavailableException`; connection failure keeps its cause; own timeout; `null` body; **a caller cancellation stays a cancellation** |
| `ValidatorTests` | 28 | register (blank/over-long name, bad emails, 256 limit, password 8 minimum), login, forgot-password, reset-password, send-message (8000 limit, blank, whitespace-only), create-session (200), direct message (8000, blank) |
| `ExceptionHandlingMiddlewareTests` | 4 | `500 internal_error`; no exception text or stack frames in the body; success untouched; half-written response discarded |
| `PipelineTests` | 21 | `401` (no token, garbage, bad signature, another key); `403` for a user on an admin route; admin `200` and page cap; `404` for unknown routes; unhandled exception `500` with nothing leaked; `/health`; malformed JSON `400`; send `503` keeps the question; send `200`; other user's session `404`; forgot-password gives one identical answer for unknown, known and broken-queue emails; hub negotiate: anonymous `401`, `access_token` accepted, garbage rejected, ignored outside `/hubs`; an Admin gets `404` on someone else's direct conversation (read and send); user search never returns emails |
| `AsyncReplyPipelineTests` | 9 | send is `202 pending` and queues the job without calling Python; broker refusal is `503` with the question kept and no orphan job; jobs list shows pending/failed only; other user's chat `404`; no token `401`; retry of a failed job `202` and re-queued; retry of a delivered job `409`; retry of another user's or unknown job `404`; retry with the broker down `503` and the job stays failed |
| `StartupConfigurationTests` | 10 | `Chat:AsyncReplies` without a broker host, a host without credentials (missing or blank), and an unknown `Email:Provider` all fail at start-up naming the setting; provider picks the sender; no broker means inline email, no workers and a no-op outbox; no Redis means in-memory presence |
| `ChatControllerTests` (added) | 4 | only the last 20 prior messages are sent, oldest first, new question not in history; short history sent whole with lower-case roles; other chats' messages ignored; whitespace-only content is `400` and nothing is saved |

The existing `SmtpEmailSenderTests` already cover the SMTP fail-fast rule (missing, empty and whitespace username and password).

### 18.3 Findings

| ID | Status | Finding | Resolution |
|---|---|---|---|
| T2-F1 | Fixed | **A caller cancellation was reported as an AI outage.** `PythonAiClient` caught `TaskCanceledException` to recognise its own 60 s timeout, but a cancelled request (the browser went away) throws the same type, so it was logged as "Could not reach the Python AI service" and rethrown as `PythonServiceUnavailableException`; its own comment claimed the opposite. Found by `ACallerCancellation_IsNotDisguisedAsAnAiOutage`. | The catch now requires `!cancellationToken.IsCancellationRequested`; the cancellation passes through, and the test pins it. |
| T2-F2 | Info | Tests that share a `WebApplicationFactory` also share its mocks; one test's `ThrowsAsync` leaked into the next (seen as a wrong `503`). | The fixture classes reset the mocks in their constructors. |
| T2-F3 | Info | Program.cs reads the connection string, `Jwt:Key` and the Python key while it builds, before a test host can add configuration, so the factory sets them as environment variables once. | Recorded in `ApiFactory`. |

### 18.4 Not covered by this run

- The Docker end-to-end suites (see 18.1).
- `/health/ready`, rate limiting, lockout, security headers, refresh tokens and the other features of Phases 21 to 28 do not exist yet; their cases stay in section 7.
- A real MySQL failure through the pipeline (LIM-01) is still only recorded by the Docker chaos suite; the in-memory pipeline tests prove the `500` shape, not the MySQL error path.
