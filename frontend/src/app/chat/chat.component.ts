import { HttpErrorResponse } from '@angular/common/http';
import { toSignal } from '@angular/core/rxjs-interop';
import { AfterViewChecked, Component, ElementRef, effect, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { distinctUntilChanged, firstValueFrom, map } from 'rxjs';

import { AuthService } from '../core/services/auth.service';
import { ChatService } from '../core/services/chat.service';
import { MessageResponse, SessionResponse } from '../core/models/chat.models';
import { extractErrorMessage } from '../core/utils/http-error';

/** A message as rendered in the thread - `pending`/`failed` only ever exist client-side, for the
 * optimistic-render + retry UI; they never come from or go to the server. */
interface DisplayMessage extends MessageResponse {
  pending?: boolean;
  failed?: boolean;
}

@Component({
  selector: 'app-chat',
  imports: [FormsModule, RouterLink],
  templateUrl: './chat.component.html',
  styleUrl: './chat.component.scss',
})
export class ChatComponent implements AfterViewChecked {
  private readonly chat = inject(ChatService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly scrollAnchor = viewChild<ElementRef<HTMLElement>>('scrollAnchor');

  readonly auth = inject(AuthService);

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
    return this.router.navigate([], { relativeTo: this.route, queryParams: { session: id } });
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
    if (!content || this.sending()) {
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
        this.messages.update((list) => [
          ...list.filter((m) => m.id !== tempId),
          res.userMessage,
          res.assistantMessage,
        ]);
        this.sending.set(false);
      },
      error: (err: HttpErrorResponse) => {
        this.sending.set(false);
        this.messages.update((list) =>
          list.map((m) => (m.id === tempId ? { ...m, pending: false, failed: true } : m)),
        );
        this.lastFailedContent = content;
        this.sendError.set(extractErrorMessage(err));
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
}
