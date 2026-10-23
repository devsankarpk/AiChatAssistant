# Testing

The test plan and test-case catalogue for the whole application. It is the single place to answer "what is tested, how, and what is not yet".

**Status today:** 321 automated tests (141 .NET, 12 Python, 168 Angular), all passing. There is no committed browser E2E suite and no CI; end-to-end checks are a manual checklist (section 8). The most recent runs, with results and findings, are in **section 13** (Phases 15 and 17, latest), **section 12** (Phase 14), **section 11** (Phase 13), **section 10** (Phase 12) and **section 9** (Phase 11). Test gaps are listed in section 6 and are closed phase by phase (T2 to T5, see `README.md`). Cases for planned features (chat, brokers, hardening) are listed in section 7 and filled in as each phase is built.

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
# .NET (141 tests)
cd backend-dotnet && dotnet test
dotnet test --filter "FullyQualifiedName~ChatControllerTests"          # one class

# Python (12 tests)
cd ai-service-python && source .venv/bin/activate && pytest
pytest tests/test_generate.py::test_returns_reply                      # one test

# Angular (168 tests)
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

## 6. Coverage gaps and the phases that close them

| Phase | Gap | New tests |
|---|---|---|
| T2 | .NET: admin usage, token service, Python client, validators, exception middleware, `401`/`403`/`404`/`/health` pipeline behaviour, SMTP fail-fast, history window | `AdminControllerTests`, `TokenServiceTests`, `PythonAiClientTests`, `ValidatorTests`, `ExceptionHandlingMiddlewareTests`, `ErrorShapeIntegrationTests` (adds the test-only package `Microsoft.AspNetCore.Mvc.Testing`) |
| T3 | Python: malformed bodies (`422`) and invalid history role | Added to `ai-service-python/tests/` |
| T4 | Angular: guards, `jwt.ts`, `extractErrorMessage`, `ChatService`, `AdminService`, `AppTitleStrategy`, chat send/retry/`503`/title flows, usage paging, login/register error mapping | New specs next to their sources |
| T5 | Close-out | Statuses in this file changed from *Planned* to real test names; before/after counts; mutation spot-check; full run |

## 7. Planned test cases for features not built yet

These rows are filled in (and their test names recorded) when the feature phase is built. Phases are built in the order given by the Roadmap in `README.md` (next up: T2), not in number order; rows marked *because Phase N is built first* are obligations that a later phase picks up. Source plans in `README.md`: "User-to-user chat (Phases 9-14, done)", "Message brokers (Phases 15-20)" and "Hardening and product gaps (Phases 21-28)".

| ID | Scenario | Expected result | Layer | Status |
|---|---|---|---|---|
| BRK-01 | Presence and push across two API replicas | Event reaches a user connected to the other replica | Manual | Planned (Phase 16) |
| BRK-02 | Forgot password publishes an email job only for existing users | Same generic response either way; retries; dead-letter after the last failure | .NET | Built in Phase 17, see 5.14 (`QUE-*`) |
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
| ACC-06 | Delete account for a user who has direct conversations | Their conversations and messages are removed or anonymised | .NET | Planned (Phase 22). The foreign keys already cascade (verified on real MySQL in Phase 10); this row covers the delete-account feature itself |
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
- **More than two people / more than one API replica:** one replica by design until Phase 16.
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
- **More than one API replica:** each replica would run its own worker on the same queue (safe), but the in-memory presence still limits the API to one replica until Phase 16.
- **A black-holed network** (packets dropped rather than refused): the 5 s connection timeout is configured but was not exercised.
- **The long Phase 12 to 14 browser suites** (see F22) and the **migration drift check** (see 13.1).
- Load, security and penetration testing are out of scope (section 1).
