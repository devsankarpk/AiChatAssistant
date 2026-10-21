import { HttpErrorResponse } from '@angular/common/http';
import { Component, ElementRef, OnDestroy, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { catchError, debounceTime, distinctUntilChanged, map, of, switchMap } from 'rxjs';

import { ConversationResponse, UserSearchResult } from '../core/models/direct.models';
import { DirectChatService } from '../core/services/direct-chat.service';
import { DirectStateService, previewOf } from '../core/services/direct-state.service';
import { extractErrorMessage } from '../core/utils/http-error';
import { TicksComponent } from '../shared/ticks/ticks.component';
import { TopNavComponent } from '../shared/top-nav/top-nav.component';

/**
 * Direct chat between users. A conversation list on the left (with a name search to start a new one),
 * the selected thread on the right. The selection lives in the URL (`?c=<id>`), like the AI chat's
 * `?session=`, so one component instance is reused as you switch. On a narrow screen only one pane shows
 * at a time. Everything live comes from DirectStateService; this component only renders and sends.
 */
@Component({
  selector: 'app-messages',
  imports: [FormsModule, TopNavComponent, TicksComponent],
  templateUrl: './messages.component.html',
  styleUrl: './messages.component.scss',
})
export class MessagesComponent implements OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly chat = inject(DirectChatService);
  readonly state = inject(DirectStateService);
  private readonly anchor = viewChild<ElementRef<HTMLElement>>('anchor');

  readonly selectedId = toSignal(this.route.queryParamMap.pipe(map((p) => p.get('c')), distinctUntilChanged()), {
    initialValue: null as string | null,
  });
  readonly selected = computed(() => this.state.conversations().find((c) => c.id === this.selectedId()) ?? null);
  readonly thread = computed(() => this.state.thread(this.selectedId()));
  readonly hasMore = computed(() => this.state.hasMore(this.selectedId()));
  readonly me = this.state.myId;

  readonly draft = signal('');
  readonly sending = signal(false);
  readonly sendError = signal<string | null>(null);
  /** The message whose "Delete for everyone?" step is showing (one at a time). */
  readonly confirmingDeleteId = signal<number | null>(null);

  readonly searchTerm = signal('');
  /** The people matching `term`; kept with its term so "no one found" is not flashed while a search is pending. */
  readonly searchResult = toSignal(
    toObservable(this.searchTerm).pipe(
      debounceTime(250),
      map((t) => t.trim()),
      distinctUntilChanged(),
      switchMap((term) =>
        term.length < 2
          ? of({ term, people: [] as UserSearchResult[] })
          : this.chat.searchUsers(term).pipe(
              map((people) => ({ term, people })),
              catchError(() => of({ term, people: [] as UserSearchResult[] })),
            ),
      ),
    ),
    { initialValue: { term: '', people: [] as UserSearchResult[] } },
  );
  readonly searching = computed(() => this.searchTerm().trim().length >= 2);
  readonly noResults = computed(
    () => this.searching() && this.searchResult().term === this.searchTerm().trim() && this.searchResult().people.length === 0,
  );

  constructor() {
    effect(() => {
      const id = this.selectedId();
      untracked(() => {
        this.sendError.set(null);
        this.confirmingDeleteId.set(null);
        this.state.openConversation(id).catch(() => {
          // A conversation id that no longer exists (or is not ours): leave the dead link.
          if (id) {
            void this.router.navigate([], { queryParams: { c: null }, replaceUrl: true });
          }
        });
      });
    });

    effect(() => {
      this.thread(); // scroll to the newest message whenever the thread changes
      const el = this.anchor()?.nativeElement;
      if (el && typeof el.scrollIntoView === 'function') {
        setTimeout(() => el.scrollIntoView({ block: 'end' }));
      }
    });
  }

  ngOnDestroy(): void {
    void this.state.openConversation(null);
  }

  open(conversationId: string): void {
    void this.router.navigate([], { queryParams: { c: conversationId } });
  }

  back(): void {
    void this.router.navigate([], { queryParams: { c: null } });
  }

  async startChat(person: UserSearchResult): Promise<void> {
    try {
      const conversation = await this.state.startConversation(person.id);
      this.searchTerm.set('');
      this.open(conversation.id);
    } catch (err) {
      this.sendError.set(err instanceof HttpErrorResponse ? extractErrorMessage(err) : 'Could not start the chat.');
    }
  }

  async send(): Promise<void> {
    const id = this.selectedId();
    const text = this.draft().trim();
    if (!id || !text || this.sending()) {
      return;
    }
    this.sending.set(true);
    this.sendError.set(null);
    try {
      await this.state.send(id, text);
      this.draft.set('');
    } catch (err) {
      // Keep the draft so nothing typed is lost.
      this.sendError.set(err instanceof HttpErrorResponse ? extractErrorMessage(err) : "This message didn't send.");
    } finally {
      this.sending.set(false);
    }
  }

  /** Typing in the box tells the other person (the state service sends it at most every few seconds). */
  onDraftChange(value: string): void {
    this.draft.set(value);
    const id = this.selectedId();
    if (id && value.trim()) {
      this.state.notifyTyping(id);
    }
  }

  askDelete(messageId: number): void {
    this.confirmingDeleteId.set(messageId);
  }

  cancelDelete(): void {
    this.confirmingDeleteId.set(null);
  }

  async confirmDelete(messageId: number): Promise<void> {
    const id = this.selectedId();
    if (!id) {
      return;
    }
    try {
      await this.state.deleteMessage(id, messageId);
    } catch (err) {
      this.sendError.set(err instanceof HttpErrorResponse ? extractErrorMessage(err) : "Couldn't delete that message.");
    } finally {
      this.confirmingDeleteId.set(null);
    }
  }

  onDraftKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      void this.send();
    }
  }

  async loadOlder(): Promise<void> {
    const id = this.selectedId();
    if (id) {
      await this.state.loadOlder(id);
    }
  }

  initial(name: string): string {
    return name.trim().charAt(0).toUpperCase();
  }

  /** The line under the name in the thread header: typing, online, or when they were last seen. */
  presenceText(conversation: ConversationResponse): string {
    if (this.state.isTyping(conversation.id)) {
      return 'typing…';
    }
    if (conversation.peerOnline) {
      return 'Online';
    }
    return conversation.peerLastSeenAt ? this.lastSeenText(conversation.peerLastSeenAt) : '';
  }

  lastSeenText(iso: string): string {
    const date = new Date(iso);
    const time = date.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
    const today = new Date();
    if (date.toDateString() === today.toDateString()) {
      return `Last seen today at ${time}`;
    }
    const yesterday = new Date(today);
    yesterday.setDate(today.getDate() - 1);
    if (date.toDateString() === yesterday.toDateString()) {
      return `Last seen yesterday at ${time}`;
    }
    return `Last seen ${date.toLocaleDateString([], { month: 'short', day: 'numeric' })}`;
  }

  /** "You: hi", "Priya: hi", or a note for an empty or deleted last message. */
  preview(conversation: ConversationResponse): string {
    const last = conversation.lastMessage;
    if (!last) {
      return 'No messages yet';
    }
    const text = last.deleted ? 'This message was deleted' : previewOf(last.content);
    return last.senderId === this.me() ? `You: ${text}` : text;
  }

  /** Time for today, short date otherwise. */
  listTime(iso: string): string {
    const date = new Date(iso);
    const sameDay = date.toDateString() === new Date().toDateString();
    return sameDay
      ? date.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })
      : date.toLocaleDateString([], { month: 'short', day: 'numeric' });
  }

  messageTime(iso: string): string {
    return new Date(iso).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
  }
}
