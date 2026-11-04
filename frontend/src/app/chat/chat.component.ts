import { HttpErrorResponse } from '@angular/common/http';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { AfterViewChecked, Component, DestroyRef, ElementRef, Injector, afterNextRender, computed, effect, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Observable, Subject, Subscription, debounceTime, distinctUntilChanged, firstValueFrom, forkJoin, map, switchMap } from 'rxjs';

import { AuthService } from '../core/services/auth.service';
import { ChatService, STREAM_REPLIES } from '../core/services/chat.service';
import { DirectHubService } from '../core/services/direct-hub.service';
import { ThemeService } from '../core/services/theme.service';
import { MessageResponse, ReplyJob, ReplyStreamEvent, SessionResponse, isAccepted } from '../core/models/chat.models';
import { extractErrorMessage } from '../core/utils/http-error';
import { groupSessionsByRecency } from '../core/utils/session-groups';
import { MarkdownComponent } from '../shared/markdown/markdown.component';
import { TopNavComponent } from '../shared/top-nav/top-nav.component';

/** A message as rendered in the thread - `pending`/`failed` only ever exist client-side, for the
 * optimistic-render + retry UI; they never come from or go to the server. */
interface DisplayMessage extends MessageResponse {
  pending?: boolean;
  failed?: boolean;
  /** The answer is still being written (client-side only; its id is temporary until the server's saved copy arrives). */
  streaming?: boolean;
}

@Component({
  selector: 'app-chat',
  imports: [FormsModule, RouterLink, MarkdownComponent, TopNavComponent],
  templateUrl: './chat.component.html',
  styleUrl: './chat.component.scss',
})
export class ChatComponent implements AfterViewChecked {
  private readonly chat = inject(ChatService);
  private readonly hub = inject(DirectHubService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly scrollAnchor = viewChild<ElementRef<HTMLElement>>('scrollAnchor');
  private readonly composerInput = viewChild<ElementRef<HTMLTextAreaElement>>('composerInput');
  private readonly messageList = viewChild<ElementRef<HTMLElement>>('messageList');
  private readonly renameInput = viewChild<ElementRef<HTMLInputElement>>('renameInput');
  private readonly cancelDeleteButton = viewChild<ElementRef<HTMLButtonElement>>('cancelDeleteButton');
  private readonly injector = inject(Injector);

  readonly auth = inject(AuthService);
  readonly theme = inject(ThemeService);

  // distinctUntilChanged matters here, not just style: ActivatedRoute.queryParamMap can emit a
  // new ParamMap *object* for the same underlying value more than once per navigation, and
  // toSignal() treats every emission as a change regardless of value equality. Without this, the
  // effect below re-fires an extra loadMessages() GET for a session whose id didn't actually
  // change, racing it against whatever dispatchSend() is doing for that same navigation.
  readonly selectedSessionId = toSignal(
    this.route.queryParamMap.pipe(
      map((params) => params.get('session')),
      distinctUntilChanged(),
    ),
    { initialValue: null },
  );

  /** How many items the server sends per page when no `limit` is asked for (it caps a page at 100). A full page means there may be more. */
  static readonly PAGE_SIZE = 50;
  private static readonly SEARCH_DEBOUNCE_MS = 250;
  /** How close to the bottom of the chat list (or the top of the thread) counts as "reached the end", in pixels. */
  private static readonly SCROLL_EDGE_PX = 80;

  readonly sessions = signal<SessionResponse[]>([]);
  readonly sessionsLoading = signal(true);
  readonly sessionsError = signal<string | null>(null);
  readonly hasMoreSessions = signal(false);
  readonly loadingMoreSessions = signal(false);

  /** What the person has typed in the search box, and the text the list on screen was actually searched with. */
  readonly searchText = signal('');
  readonly activeSearch = signal('');
  private readonly searchTerms = new Subject<string>();

  /** Inline rename of one chat in the sidebar. */
  readonly renamingId = signal<string | null>(null);
  readonly renameDraft = signal('');
  readonly renameSaving = signal(false);

  /** The chat the delete confirmation is open for. */
  readonly deleteTarget = signal<SessionResponse | null>(null);
  readonly deleting = signal(false);

  readonly messages = signal<DisplayMessage[]>([]);
  readonly messagesLoading = signal(false);
  readonly hasMoreMessages = signal(false);
  readonly loadingOlder = signal(false);

  /**
   * Replies being produced in the background (the server's async mode), by job id. The page shows "Thinking…" while
   * one exists for the open chat, and clears it on the `AssistantReplied` / `AssistantFailed` event. Because the reply
   * is also saved, a missed event is repaired by refetching (on reconnect, on opening the chat, and on a timer below).
   */
  readonly pendingJobs = signal<Record<string, { sessionId: string; userMessageId: number }>>({});

  /** Questions whose reply failed, by question id -> job id. These show "Send again" under the question. */
  readonly failedJobs = signal<Record<number, string>>({});

  /** True while a background reply is being produced for the open chat. */
  readonly replyPending = computed(() => {
    const open = this.selectedSessionId();
    return !!open && Object.values(this.pendingJobs()).some((j) => j.sessionId === open);
  });

  /** Safety net: how often to look for a reply whose push never arrived. */
  private static readonly REPLY_POLL_MS = 15_000;
  private replyPoll: ReturnType<typeof setInterval> | undefined;

  readonly sessionGroups = computed(() => groupSessionsByRecency(this.sessions()));

  /** Ideas on the empty screen. Clicking one fills the composer; the person still presses send. */
  readonly starters = [
    'Explain how DNS works in plain language',
    'Rewrite this to sound friendlier: ',
    'Plan a week of simple vegetarian dinners',
  ];

  readonly copiedId = signal<number | null>(null);
  private copiedTimer: ReturnType<typeof setTimeout> | undefined;

  readonly sidebarOpen = signal(false);

  readonly selectedTitle = computed(
    () => this.sessions().find((s) => s.id === this.selectedSessionId())?.title ?? 'New chat',
  );

  readonly draft = signal('');
  readonly sending = signal(false);

  private readonly streamReplies = inject(STREAM_REPLIES);
  private streamSub: Subscription | undefined;
  private nextStreamId = -1_000_000;

  /** True while an answer is arriving word by word: the composer shows Stop instead of Send. */
  readonly streamInFlight = signal(false);

  /** True once the first words of a streamed answer are on screen ("Thinking…" is then replaced by the words). */
  readonly streamedWords = computed(() => this.messages().some((m) => m.streaming));

  /** The last message in the open chat, which decides where Regenerate (or "try again") is offered. */
  private readonly lastMessage = computed(() => this.messages().at(-1));

  /** Regenerate sits under the newest answer; it is offered when nothing else is going on. */
  canRegenerate(message: DisplayMessage): boolean {
    return (
      this.streamReplies &&
      !this.sending() &&
      !this.replyPending() &&
      !message.streaming &&
      message.id === this.lastMessage()?.id
    );
  }

  /** A question the assistant never answered (the reply failed or was stopped before a word), offered "Try again". */
  isUnanswered(message: DisplayMessage): boolean {
    return (
      this.streamReplies &&
      message.role === 'user' &&
      !message.pending &&
      !message.failed &&
      message.id === this.lastMessage()?.id &&
      !this.sending() &&
      !this.replyPending() &&
      !this.failedJobs()[message.id]
    );
  }
  readonly sendError = signal<string | null>(null);
  private lastFailedContent: string | null = null;

  private scrolledForMessageCount = -1;
  private scrolledForStreamLength = 0;

  // Only ever superseded by a newer loadMessages() call (not by a send - see dispatchSend),
  // so a slow GET can never clobber a more recent GET's result.
  private messagesToken = 0;

  // The id of a session this component just created itself (via newChat() or send()'s
  // auto-create). We already know that session has zero messages - no need to round-trip a GET
  // for it, and more importantly: NOT skipping it would race that GET against dispatchSend's own
  // optimistic-then-real update for the same session, which can land first and get overwritten
  // by the (now-stale) empty GET response. Consumed by the effect below on first sight.
  private skipNextLoadFor: string | null = null;

  constructor() {
    this.chat.getSessions().subscribe({
      next: (list) => {
        this.sessions.set(list);
        this.hasMoreSessions.set(list.length >= ChatComponent.PAGE_SIZE);
        this.sessionsLoading.set(false);
      },
      error: (err: HttpErrorResponse) => {
        this.sessionsLoading.set(false);
        this.sessionsError.set(extractErrorMessage(err));
      },
    });

    // Searching asks the server (titles of every chat, not just the ones loaded so far). switchMap drops a slow answer to an older term.
    this.searchTerms
      .pipe(
        map((term) => term.trim()),
        debounceTime(ChatComponent.SEARCH_DEBOUNCE_MS),
        distinctUntilChanged(),
        switchMap((term) => {
          this.activeSearch.set(term);
          this.sessionsError.set(null);
          return this.chat.getSessions({ search: term });
        }),
        takeUntilDestroyed(),
      )
      .subscribe({
        next: (list) => {
          this.sessions.set(list);
          this.hasMoreSessions.set(list.length >= ChatComponent.PAGE_SIZE);
        },
        error: (err: HttpErrorResponse) => this.sessionsError.set(extractErrorMessage(err)),
      });

    effect(() => {
      const input = this.renameInput()?.nativeElement;
      if (input) {
        // ngModel writes the value a tick after the input exists; focusing and selecting sooner would select nothing.
        setTimeout(() => {
          input.focus();
          input.select();
        });
      }
    });
    effect(() => this.cancelDeleteButton()?.nativeElement.focus());

    inject(DestroyRef).onDestroy(() => clearInterval(this.replyPoll));

    // Async replies (Phase 18) arrive over the hub; a reconnect may have missed one, so refetch what is open.
    this.hub.assistantReplied$.pipe(takeUntilDestroyed()).subscribe((event) => {
      this.dropPending(event.jobId);
      this.clearFailed(event.userMessageId);
      if (event.sessionId === this.selectedSessionId()) {
        this.messages.update((list) =>
          list.some((m) => m.id === event.assistantMessage.id) ? list : [...list, event.assistantMessage],
        );
      }
    });
    this.hub.assistantFailed$.pipe(takeUntilDestroyed()).subscribe((event) => {
      this.dropPending(event.jobId);
      this.failedJobs.update((jobs) => ({ ...jobs, [event.userMessageId]: event.jobId }));
    });
    this.hub.reconnected$.pipe(takeUntilDestroyed()).subscribe(() => {
      const open = this.selectedSessionId();
      if (open) {
        this.resyncReplies(open);
      }
    });
    effect(() => {
      // Poll only while a reply is outstanding, and stop the moment none is.
      if (this.replyPending() && !this.replyPoll) {
        this.replyPoll = setInterval(() => {
          const open = this.selectedSessionId();
          if (open) {
            this.resyncReplies(open);
          }
        }, ChatComponent.REPLY_POLL_MS);
      } else if (!this.replyPending() && this.replyPoll) {
        clearInterval(this.replyPoll);
        this.replyPoll = undefined;
      }
    });

    effect(() => {
      const id = this.selectedSessionId();
      if (!id) {
        this.messages.set([]);
        this.hasMoreMessages.set(false);
      } else if (id === this.skipNextLoadFor) {
        // Caller (newChat()/send()) already cleared `messages` synchronously, before ever
        // navigating - deliberately NOT touched here. Angular's effect scheduling gives no
        // guarantee of running before router.navigate()'s promise resolves, so by the time this
        // fires, dispatchSend() may already be mid-flight (or done) for this exact session;
        // writing to `messages` here could clobber it depending on exactly when that turns out
        // to be. Skipping the GET is still correct either way - the session really is empty.
        this.skipNextLoadFor = null;
      } else {
        this.loadMessages(id);
      }
    });
  }

  ngAfterViewChecked(): void {
    if (this.messages().length !== this.scrolledForMessageCount) {
      this.scrolledForMessageCount = this.messages().length;
      this.scrollAnchor()?.nativeElement.scrollIntoView({ block: 'end' });
      return;
    }
    // Words arriving in the last message: follow them, unless the reader scrolled up to look at something earlier.
    const streamed = this.messages().find((m) => m.streaming)?.content.length ?? 0;
    if (streamed !== this.scrolledForStreamLength) {
      this.scrolledForStreamLength = streamed;
      const list = this.messageList()?.nativeElement;
      if (list && list.scrollHeight - list.scrollTop - list.clientHeight < 160) {
        this.scrollAnchor()?.nativeElement.scrollIntoView({ block: 'end' });
      }
    }
  }

  newChat(): void {
    this.sessionsError.set(null);
    this.chat.createSession().subscribe({
      next: (session) => {
        this.sessions.update((list) => [session, ...list]);
        this.messages.set([]);
        this.skipNextLoadFor = session.id;
        this.selectSession(session.id);
      },
      error: (err: HttpErrorResponse) => this.sessionsError.set(extractErrorMessage(err)),
    });
  }

  onSearchInput(text: string): void {
    this.searchText.set(text);
    this.searchTerms.next(text);
  }

  clearSearch(): void {
    this.onSearchInput('');
  }

  onSessionListScroll(event: Event): void {
    const el = event.target as HTMLElement;
    if (el.scrollHeight - el.scrollTop - el.clientHeight < ChatComponent.SCROLL_EDGE_PX) {
      this.loadMoreSessions();
    }
  }

  /** The next page of chats after the last one shown, for the current search. */
  loadMoreSessions(): void {
    const last = this.sessions().at(-1);
    if (!last || !this.hasMoreSessions() || this.loadingMoreSessions()) {
      return;
    }
    const search = this.activeSearch();
    this.loadingMoreSessions.set(true);
    this.chat.getSessions({ search, before: last.createdAt }).subscribe({
      next: (page) => {
        this.loadingMoreSessions.set(false);
        if (search !== this.activeSearch()) {
          return; // the search changed while this was loading
        }
        this.sessions.update((list) => [...list, ...page.filter((p) => !list.some((s) => s.id === p.id))]);
        this.hasMoreSessions.set(page.length >= ChatComponent.PAGE_SIZE);
      },
      error: (err: HttpErrorResponse) => {
        this.loadingMoreSessions.set(false);
        this.sessionsError.set(extractErrorMessage(err));
      },
    });
  }

  startRename(session: SessionResponse): void {
    this.renameDraft.set(session.title);
    this.renamingId.set(session.id);
  }

  cancelRename(): void {
    this.renamingId.set(null);
  }

  onRenameKeydown(event: KeyboardEvent, session: SessionResponse): void {
    if (event.key === 'Enter') {
      event.preventDefault();
      this.commitRename(session);
    } else if (event.key === 'Escape') {
      event.preventDefault();
      this.cancelRename();
    }
  }

  commitRename(session: SessionResponse): void {
    const title = this.renameDraft().trim();
    if (this.renameSaving()) {
      return;
    }
    if (!title || title === session.title) {
      this.cancelRename();
      return;
    }
    this.renameSaving.set(true);
    this.sessionsError.set(null);
    this.chat.renameSession(session.id, title).subscribe({
      next: (updated) => {
        this.renameSaving.set(false);
        this.renamingId.set(null);
        this.sessions.update((list) => list.map((s) => (s.id === updated.id ? { ...s, title: updated.title } : s)));
      },
      error: (err: HttpErrorResponse) => {
        this.renameSaving.set(false);
        this.renamingId.set(null);
        this.sessionsError.set(extractErrorMessage(err));
      },
    });
  }

  askDelete(session: SessionResponse): void {
    this.deleteTarget.set(session);
  }

  cancelDelete(): void {
    if (!this.deleting()) {
      this.deleteTarget.set(null);
    }
  }

  confirmDelete(): void {
    const target = this.deleteTarget();
    if (!target || this.deleting()) {
      return;
    }
    this.deleting.set(true);
    const finish = (): void => {
      this.deleting.set(false);
      this.deleteTarget.set(null);
      this.sessions.update((list) => list.filter((s) => s.id !== target.id));
      this.pendingJobs.update((jobs) => Object.fromEntries(Object.entries(jobs).filter(([, j]) => j.sessionId !== target.id)));
      if (this.selectedSessionId() === target.id) {
        this.messages.set([]);
        this.messagesToken++; // a load still in flight for the deleted chat must not bring it back
        this.router.navigate([], { relativeTo: this.route, queryParams: { session: null } });
      }
    };
    this.chat.deleteSession(target.id).subscribe({
      next: finish,
      error: (err: HttpErrorResponse) => {
        if (err.status === 404) {
          finish(); // already gone (deleted in another tab): same end state
          return;
        }
        this.deleting.set(false);
        this.deleteTarget.set(null);
        this.sessionsError.set(extractErrorMessage(err));
      },
    });
  }

  onMessageListScroll(event: Event): void {
    if ((event.target as HTMLElement).scrollTop < ChatComponent.SCROLL_EDGE_PX) {
      this.loadOlderMessages();
    }
  }

  /** Fetches the page of messages above the oldest one on screen and puts it in front, keeping what the person is reading in place. */
  loadOlderMessages(): void {
    const sessionId = this.selectedSessionId();
    const oldest = this.messages().find((m) => m.id > 0);
    if (!sessionId || !oldest || !this.hasMoreMessages() || this.loadingOlder()) {
      return;
    }
    this.loadingOlder.set(true);
    this.chat.getMessages(sessionId, { before: oldest.id }).subscribe({
      next: (page) => {
        this.loadingOlder.set(false);
        if (sessionId !== this.selectedSessionId()) {
          return; // the person moved to another chat meanwhile
        }
        this.hasMoreMessages.set(page.length >= ChatComponent.PAGE_SIZE);
        const el = this.messageList()?.nativeElement;
        const heightBefore = el?.scrollHeight ?? 0;
        const topBefore = el?.scrollTop ?? 0;
        this.messages.update((list) => [...page.filter((p) => !list.some((m) => m.id === p.id)), ...list]);
        // Prepending is not "a new message": do not jump to the bottom, and keep the same text under the eyes.
        this.scrolledForMessageCount = this.messages().length;
        afterNextRender(
          () => {
            if (el) {
              el.scrollTop = topBefore + (el.scrollHeight - heightBefore);
            }
          },
          { injector: this.injector },
        );
      },
      error: (err: HttpErrorResponse) => {
        this.loadingOlder.set(false);
        this.sendError.set(extractErrorMessage(err));
      },
    });
  }

  selectSession(id: string): Promise<boolean> {
    this.sidebarOpen.set(false);
    return this.router.navigate([], { relativeTo: this.route, queryParams: { session: id } });
  }

  useStarter(text: string): void {
    this.draft.set(text);
    this.composerInput()?.nativeElement.focus();
  }

  async copy(message: DisplayMessage): Promise<void> {
    try {
      await navigator.clipboard.writeText(message.content);
    } catch {
      return; // Clipboard blocked (insecure context or denied) - the text stays selectable by hand.
    }
    this.copiedId.set(message.id);
    clearTimeout(this.copiedTimer);
    this.copiedTimer = setTimeout(() => this.copiedId.set(null), 1800);
  }

  onDraftKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      void this.send();
    }
  }

  logout(): void {
    this.auth.logout();
    this.router.navigateByUrl('/login');
  }

  async send(): Promise<void> {
    const content = this.draft().trim();
    if (!content || this.sending() || this.replyPending()) {
      return;
    }

    let sessionId = this.selectedSessionId();
    if (!sessionId) {
      try {
        const session = await firstValueFrom(this.chat.createSession());
        this.sessions.update((list) => [session, ...list]);
        sessionId = session.id;
        this.messages.set([]);
        this.skipNextLoadFor = sessionId;
        await this.selectSession(sessionId);
      } catch (err) {
        this.sendError.set(extractErrorMessage(err as HttpErrorResponse));
        return;
      }
    }

    this.draft.set('');
    this.dispatchSend(sessionId, content);
  }

  /** "Send again" under a question whose reply failed: ask the server for another go. No duplicate question is created. */
  retryReply(jobId: string, userMessageId: number): void {
    const sessionId = this.selectedSessionId();
    if (!sessionId) {
      return;
    }
    this.sendError.set(null);
    this.chat.retryReply(jobId).subscribe({
      next: () => {
        this.clearFailed(userMessageId);
        this.pendingJobs.update((jobs) => ({ ...jobs, [jobId]: { sessionId, userMessageId } }));
      },
      error: (err: HttpErrorResponse) => this.sendError.set(extractErrorMessage(err)),
    });
  }

  retry(): void {
    const sessionId = this.selectedSessionId();
    if (!this.lastFailedContent || !sessionId) {
      return;
    }
    this.messages.update((list) => list.filter((m) => !m.failed));
    this.dispatchSend(sessionId, this.lastFailedContent);
  }

  private dispatchSend(sessionId: string, content: string): void {
    // From this point, this send owns the message list for `sessionId` - any loadMessages() GET
    // for it still in flight (the route-change effect can fire more than once per navigation)
    // must not be allowed to land afterward and overwrite what we're about to render.
    this.messagesToken++;

    const tempId = -Date.now();
    this.messages.update((list) => [
      ...list,
      { id: tempId, role: 'user', content, createdAt: new Date().toISOString(), pending: true },
    ]);
    this.sending.set(true);
    this.sendError.set(null);

    if (this.streamReplies) {
      this.runStream(sessionId, this.chat.streamMessage(sessionId, content), { tempId, content });
      return;
    }

    this.chat.sendMessage(sessionId, content).subscribe({
      next: (res) => {
        if (isAccepted(res)) {
          // Async mode: the question is saved and the reply will arrive on the hub. Keep "Thinking…" until it does.
          this.messages.update((list) => [...list.filter((m) => m.id !== tempId), res.userMessage]);
          this.pendingJobs.update((jobs) => ({ ...jobs, [res.jobId]: { sessionId, userMessageId: res.userMessage.id } }));
        } else {
          this.messages.update((list) => [
            ...list.filter((m) => m.id !== tempId),
            res.userMessage,
            res.assistantMessage,
          ]);
        }
        this.sending.set(false);
        this.refreshTitleIfUntitled(sessionId);
      },
      error: (err: HttpErrorResponse) => {
        this.sending.set(false);
        this.messages.update((list) =>
          list.map((m) => (m.id === tempId ? { ...m, pending: false, failed: true } : m)),
        );
        this.lastFailedContent = content;
        this.sendError.set(extractErrorMessage(err));
        // The server titles the chat as soon as the message is saved, even if the reply failed.
        this.refreshTitleIfUntitled(sessionId);
      },
    });
  }

  /** Stop: closes the connection. The server keeps the words written so far as the answer. */
  stop(): void {
    if (!this.streamSub) {
      return;
    }
    const sessionId = this.selectedSessionId();
    this.streamSub.unsubscribe();
    this.streamSub = undefined;
    this.messages.update((list) => list.map((m) => ({ ...m, streaming: false, pending: m.id < 0 && m.role === 'user' ? false : m.pending })));
    this.streamInFlight.set(false);
    this.sending.set(false);
    if (sessionId) {
      this.refreshTitleIfUntitled(sessionId);
    }
  }

  /** Another answer to the last question (replaces the newest answer, or answers a question that has none). */
  regenerate(): void {
    const sessionId = this.selectedSessionId();
    if (!sessionId || this.sending() || this.replyPending()) {
      return;
    }
    this.messagesToken++; // a thread load still in flight must not overwrite what is about to arrive
    this.sending.set(true);
    this.sendError.set(null);
    this.runStream(sessionId, this.chat.regenerate(sessionId), { regenerating: true });
  }

  private runStream(
    sessionId: string,
    source: Observable<ReplyStreamEvent>,
    opts: { tempId?: number; content?: string; regenerating?: boolean },
  ): void {
    let questionSaved = false;
    const streamId = this.nextStreamId--;
    this.streamInFlight.set(true);

    const end = () => {
      this.streamSub = undefined;
      this.streamInFlight.set(false);
      this.sending.set(false);
    };

    this.streamSub = source.subscribe({
      next: (event) => {
        if (event.type === 'user') {
          questionSaved = true;
          this.messages.update((list) => list.map((m) => (m.id === opts.tempId ? event.message : m)));
        } else if (event.type === 'delta') {
          this.messages.update((list) => {
            const at = list.findIndex((m) => m.streaming);
            if (at >= 0) {
              return list.map((m, i) => (i === at ? { ...m, content: m.content + event.text } : m));
            }
            // First word. A regenerated answer replaces the old one only now, so a failure to start keeps the old one.
            const kept = opts.regenerating && list.at(-1)?.role === 'assistant' ? list.slice(0, -1) : list;
            return [...kept, { id: streamId, role: 'assistant', content: event.text, createdAt: new Date().toISOString(), streaming: true }];
          });
        } else {
          this.messages.update((list) => {
            const hadWords = list.some((m) => m.streaming);
            const kept = hadWords
              ? list.filter((m) => !m.streaming)
              : opts.regenerating && list.at(-1)?.role === 'assistant'
                ? list.slice(0, -1)
                : list;
            return [...kept, event.message];
          });
          end();
          this.refreshTitleIfUntitled(sessionId);
        }
      },
      error: (err: HttpErrorResponse) => {
        const hadWords = this.messages().some((m) => m.streaming);
        end();
        const message = extractErrorMessage(err);
        if (hadWords || questionSaved) {
          // The server saved no answer for a broken stream: show what it really holds (the question, and for a
          // regenerate the old answer) instead of the half-written text.
          this.loadMessages(sessionId);
        }
        if (opts.tempId !== undefined && !questionSaved) {
          // Never reached the server's save: the old "didn't send" state, with Send again.
          this.messages.update((list) => list.map((m) => (m.id === opts.tempId ? { ...m, pending: false, failed: true } : m)));
          this.lastFailedContent = opts.content ?? null;
        }
        this.sendError.set(message);
        this.refreshTitleIfUntitled(sessionId);
      },
    });
  }

  // The backend renames a "New chat" from its first message; pick that up without a full reload.
  private refreshTitleIfUntitled(sessionId: string): void {
    if (this.sessions().find((s) => s.id === sessionId)?.title !== 'New chat') {
      return;
    }
    this.chat.getSessions().subscribe({
      next: (list) => {
        // Take only the new title: replacing the whole list would drop pages already loaded and ignore an active search.
        const fresh = list.find((s) => s.id === sessionId);
        if (fresh) {
          this.sessions.update((all) => all.map((s) => (s.id === sessionId ? { ...s, title: fresh.title } : s)));
        }
      },
      error: () => {
        // Cosmetic only - the title shows up on the next page load anyway.
      },
    });
  }

  private loadMessages(sessionId: string): void {
    const token = ++this.messagesToken;
    this.hasMoreMessages.set(false);
    this.messagesLoading.set(true);
    this.sendError.set(null);
    this.chat.getMessages(sessionId).subscribe({
      next: (msgs) => {
        if (token !== this.messagesToken) {
          return; // superseded by a newer load or an in-progress send - discard this response
        }
        this.messages.set(msgs);
        this.hasMoreMessages.set(msgs.length >= ChatComponent.PAGE_SIZE);
        this.messagesLoading.set(false);
        this.loadReplyJobs(sessionId);
      },
      error: (err: HttpErrorResponse) => {
        if (token !== this.messagesToken) {
          return;
        }
        this.messagesLoading.set(false);
        this.messages.set([]);
        this.hasMoreMessages.set(false);
        this.sessionsError.set(extractErrorMessage(err));
      },
    });
  }

  /** Show "Thinking…" or a failed state for replies that are not delivered yet (also after a page reload). */
  private loadReplyJobs(sessionId: string): void {
    this.chat.getOpenReplyJobs(sessionId).subscribe({
      next: (jobs) => this.applyReplyJobs(sessionId, jobs),
      error: () => {
        // Cosmetic: without it the page just does not show a pending state for an old question.
      },
    });
  }

  private applyReplyJobs(sessionId: string, jobs: ReplyJob[]): void {
    this.pendingJobs.update((current) => {
      const others = Object.fromEntries(Object.entries(current).filter(([, j]) => j.sessionId !== sessionId));
      const mine = Object.fromEntries(
        jobs.filter((j) => j.status === 'pending').map((j) => [j.jobId, { sessionId, userMessageId: j.userMessageId }]),
      );
      return { ...others, ...mine };
    });
    this.failedJobs.set(Object.fromEntries(jobs.filter((j) => j.status === 'failed').map((j) => [j.userMessageId, j.jobId])));
  }

  /** Refetch messages and open jobs together, to repair a missed push. */
  private resyncReplies(sessionId: string): void {
    forkJoin([this.chat.getMessages(sessionId), this.chat.getOpenReplyJobs(sessionId)]).subscribe({
      next: ([msgs, jobs]) => {
        if (sessionId !== this.selectedSessionId()) {
          return;
        }
        this.messages.update((list) => {
          // Take everything the server knows for the newest page, keep older pages already scrolled in, and keep an optimistic message still being sent.
          const local = list.filter((m) => m.pending || m.failed);
          const newestPageStart = msgs[0]?.id;
          const older = newestPageStart === undefined ? [] : list.filter((m) => !m.pending && !m.failed && m.id > 0 && m.id < newestPageStart);
          return [...older, ...msgs, ...local];
        });
        this.applyReplyJobs(sessionId, jobs);
      },
      error: () => {
        // Try again at the next poll or reconnect.
      },
    });
  }

  private dropPending(jobId: string): void {
    this.pendingJobs.update((jobs) => {
      const { [jobId]: _removed, ...rest } = jobs;
      return rest;
    });
  }

  private clearFailed(userMessageId: number): void {
    this.failedJobs.update((jobs) => {
      const { [userMessageId]: _removed, ...rest } = jobs;
      return rest;
    });
  }
}
