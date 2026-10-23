import { HttpErrorResponse } from '@angular/common/http';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { AfterViewChecked, Component, DestroyRef, ElementRef, computed, effect, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { distinctUntilChanged, firstValueFrom, forkJoin, map } from 'rxjs';

import { AuthService } from '../core/services/auth.service';
import { ChatService } from '../core/services/chat.service';
import { DirectHubService } from '../core/services/direct-hub.service';
import { ThemeService } from '../core/services/theme.service';
import { MessageResponse, ReplyJob, SessionResponse, isAccepted } from '../core/models/chat.models';
import { extractErrorMessage } from '../core/utils/http-error';
import { groupSessionsByRecency } from '../core/utils/session-groups';
import { MarkdownComponent } from '../shared/markdown/markdown.component';
import { TopNavComponent } from '../shared/top-nav/top-nav.component';

/** A message as rendered in the thread - `pending`/`failed` only ever exist client-side, for the
 * optimistic-render + retry UI; they never come from or go to the server. */
interface DisplayMessage extends MessageResponse {
  pending?: boolean;
  failed?: boolean;
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

  readonly sessions = signal<SessionResponse[]>([]);
  readonly sessionsLoading = signal(true);
  readonly sessionsError = signal<string | null>(null);

  readonly messages = signal<DisplayMessage[]>([]);
  readonly messagesLoading = signal(false);

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
  readonly sendError = signal<string | null>(null);
  private lastFailedContent: string | null = null;

  private scrolledForMessageCount = -1;

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
        this.sessionsLoading.set(false);
      },
      error: (err: HttpErrorResponse) => {
        this.sessionsLoading.set(false);
        this.sessionsError.set(extractErrorMessage(err));
      },
    });

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

  // The backend renames a "New chat" from its first message; pick that up without a full reload.
  private refreshTitleIfUntitled(sessionId: string): void {
    if (this.sessions().find((s) => s.id === sessionId)?.title !== 'New chat') {
      return;
    }
    this.chat.getSessions().subscribe({
      next: (list) => this.sessions.set(list),
      error: () => {
        // Cosmetic only - the title shows up on the next page load anyway.
      },
    });
  }

  private loadMessages(sessionId: string): void {
    const token = ++this.messagesToken;
    this.messagesLoading.set(true);
    this.sendError.set(null);
    this.chat.getMessages(sessionId).subscribe({
      next: (msgs) => {
        if (token !== this.messagesToken) {
          return; // superseded by a newer load or an in-progress send - discard this response
        }
        this.messages.set(msgs);
        this.messagesLoading.set(false);
        this.loadReplyJobs(sessionId);
      },
      error: (err: HttpErrorResponse) => {
        if (token !== this.messagesToken) {
          return;
        }
        this.messagesLoading.set(false);
        this.messages.set([]);
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
          // Keep an optimistic message that is still being sent; take everything the server knows.
          const local = list.filter((m) => m.pending || m.failed);
          return [...msgs, ...local];
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
