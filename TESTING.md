# Testing

The test plan and test-case catalogue for the whole application. It is the single place to answer "what is tested, how, and what is not yet".

**Status today:** 163 automated tests (95 .NET, 12 Python, 56 Angular), all passing. There is no committed browser E2E suite and no CI; end-to-end checks are a manual checklist (section 8). The most recent full-application run, with results and findings, is in **section 9**. Test gaps are listed in section 6 and are closed phase by phase (T2 to T5, see `README.md`). Cases for planned features (chat, brokers, hardening) are listed in section 7 and filled in as each phase is built.

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
# .NET (95 tests)
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
| OPS-01 | `docker compose --env-file docker.env up --build -d` on a clean machine | All five services start; `api` and `web` report healthy | Manual |
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
| MSG-01 | Ticks component | One grey tick = sent, two grey = delivered, two blue = read, deleted shows none; accessible labels | Angular | Planned (Phase 12) |
| MSG-02 | Unread badge and toast | Badge updates live; toast shows sender and preview only when that conversation isn't open and visible | Angular | Planned (Phase 12) |
| MSG-03 | Typing indicator | Sent about every 3 s, clears after 4 s; rejected for non-participants | .NET + Angular | Planned (Phase 13) |
| MSG-04 | Delete own message | Sender only; content cleared on the server; both sides see "This message was deleted" | .NET + Angular | Planned (Phase 13) |
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
