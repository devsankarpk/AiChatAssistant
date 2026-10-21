import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Subscription, firstValueFrom } from 'rxjs';

import {
  ConversationResponse,
  DirectMessageResponse,
  DirectMessageStatus,
  MessageDeletedEvent,
  MessagesReadEvent,
  PeerPresenceEvent,
  PeerTypingEvent,
  StatusChangedEvent,
} from '../models/direct.models';
import { extractErrorMessage } from '../utils/http-error';
import { AuthService } from './auth.service';
import { DirectChatService } from './direct-chat.service';
import { DirectHubService } from './direct-hub.service';
import { ToastService } from './toast.service';

const PAGE_SIZE = 50;
const PREVIEW_LENGTH = 80;
/** How long "typing…" stays up after the last typing signal (the sender repeats it every few seconds). */
const TYPING_SHOWN_MS = 4000;
/** The most often we tell the other person that we are typing. */
const TYPING_SEND_INTERVAL_MS = 3000;

/** A tick state only ever moves forward: a late "delivered" must not undo "read". */
const STATUS_RANK: Record<DirectMessageStatus, number> = { sent: 0, delivered: 1, read: 2 };

export function previewOf(text: string): string {
  const flat = text.replace(/\s+/g, ' ').trim();
  return flat.length > PREVIEW_LENGTH ? flat.slice(0, PREVIEW_LENGTH - 1) + '…' : flat;
}

/**
 * Everything the direct-chat UI shows: the conversation list, the loaded threads, the unread total
 * (for the Messages tab badge) and which conversation is open. It is app-wide (started and stopped by
 * AppComponent with the sign-in state) so the badge and toasts work on every page, not just Messages.
 *
 * The database is the source of truth. Live events from the hub update this state instantly; anything
 * missed (a dropped connection, a tab in the background) is repaired by refetching on reconnect and
 * when the tab becomes visible again.
 */
@Injectable({ providedIn: 'root' })
export class DirectStateService {
  private readonly chat = inject(DirectChatService);
  private readonly hub = inject(DirectHubService);
  private readonly auth = inject(AuthService);
  private readonly toast = inject(ToastService);
  private readonly document = inject(DOCUMENT);

  private readonly conversationsSignal = signal<ConversationResponse[]>([]);
  private readonly threadsSignal = signal<Record<string, DirectMessageResponse[]>>({});
  private readonly hasMoreSignal = signal<Record<string, boolean>>({});
  private readonly openIdSignal = signal<string | null>(null);
  private readonly typingSignal = signal<Record<string, true>>({});

  /** In display order: most recently active first. */
  readonly conversations = this.conversationsSignal.asReadonly();
  readonly unreadTotal = computed(() => this.conversationsSignal().reduce((sum, c) => sum + c.unreadCount, 0));
  readonly openConversationId = this.openIdSignal.asReadonly();
  readonly loadingConversations = signal(false);
  readonly loadError = signal<string | null>(null);
  readonly myId = computed(() => this.auth.user()?.id ?? 0);

  private started = false;
  private subscriptions: Subscription[] = [];
  private readonly typingTimers = new Map<string, ReturnType<typeof setTimeout>>();
  private readonly lastTypingSentAt = new Map<string, number>();

  /** Starts the live connection and loads the list. Safe to call repeatedly. */
  start(): void {
    if (this.started) {
      return;
    }
    this.started = true;
    this.subscriptions = [
      this.hub.messageReceived$.subscribe((m) => void this.onMessageReceived(m)),
      this.hub.statusChanged$.subscribe((e) => this.onStatusChanged(e)),
      this.hub.messagesRead$.subscribe((e) => this.onMessagesRead(e)),
      this.hub.reconnected$.subscribe(() => void this.resync()),
      this.hub.messageDeleted$.subscribe((e) => this.onMessageDeleted(e)),
      this.hub.peerTyping$.subscribe((e) => this.onPeerTyping(e)),
      this.hub.peerPresence$.subscribe((e) => this.onPeerPresence(e)),
    ];
    this.document.addEventListener('visibilitychange', this.onVisibilityChange);
    void this.hub.start();
    void this.refreshConversations();
  }

  /** Signing out: drop the connection and forget everything about the previous user. */
  stop(): void {
    if (!this.started) {
      return;
    }
    this.started = false;
    this.subscriptions.forEach((s) => s.unsubscribe());
    this.subscriptions = [];
    this.document.removeEventListener('visibilitychange', this.onVisibilityChange);
    void this.hub.stop();
    this.conversationsSignal.set([]);
    this.threadsSignal.set({});
    this.hasMoreSignal.set({});
    this.openIdSignal.set(null);
    this.loadError.set(null);
    this.typingTimers.forEach((timer) => clearTimeout(timer));
    this.typingTimers.clear();
    this.lastTypingSentAt.clear();
    this.typingSignal.set({});
  }

  /** True while the other person is typing in this conversation (cleared after a few quiet seconds). */
  isTyping(conversationId: string | null): boolean {
    return conversationId ? !!this.typingSignal()[conversationId] : false;
  }

  /** Call as the draft changes: tells the other person, at most once every few seconds. */
  notifyTyping(conversationId: string): void {
    const now = Date.now();
    if (now - (this.lastTypingSentAt.get(conversationId) ?? 0) < TYPING_SEND_INTERVAL_MS) {
      return;
    }
    this.lastTypingSentAt.set(conversationId, now);
    void this.hub.sendTyping(conversationId);
  }

  /** Delete one of your own messages for everyone; the thread and the list preview show the placeholder. */
  async deleteMessage(conversationId: string, messageId: number): Promise<void> {
    await firstValueFrom(this.chat.deleteMessage(conversationId, messageId));
    this.applyDeleted(conversationId, messageId);
  }

  thread(conversationId: string | null): DirectMessageResponse[] {
    return conversationId ? (this.threadsSignal()[conversationId] ?? []) : [];
  }

  hasMore(conversationId: string | null): boolean {
    return conversationId ? (this.hasMoreSignal()[conversationId] ?? false) : false;
  }

  async refreshConversations(): Promise<void> {
    this.loadingConversations.set(true);
    try {
      this.conversationsSignal.set(await firstValueFrom(this.chat.listConversations()));
      this.loadError.set(null);
    } catch (err) {
      this.loadError.set(err instanceof HttpErrorResponse ? extractErrorMessage(err) : 'Could not load your messages.');
    } finally {
      this.loadingConversations.set(false);
    }
  }

  /** The Messages page calls this when a conversation is selected (and with null when it is left). */
  async openConversation(conversationId: string | null): Promise<void> {
    this.openIdSignal.set(conversationId);
    if (!conversationId) {
      return;
    }
    await this.loadThread(conversationId);
    await this.markReadIfVisible();
  }

  async loadThread(conversationId: string): Promise<void> {
    const fetched = await firstValueFrom(this.chat.getMessages(conversationId, undefined, PAGE_SIZE));
    this.threadsSignal.update((all) => ({ ...all, [conversationId]: mergeById(all[conversationId] ?? [], fetched) }));
    this.hasMoreSignal.update((all) => ({ ...all, [conversationId]: fetched.length >= PAGE_SIZE }));
  }

  /** The "show earlier messages" button: prepend the page before the oldest message we have. */
  async loadOlder(conversationId: string): Promise<void> {
    const current = this.thread(conversationId);
    if (current.length === 0) {
      return;
    }
    const older = await firstValueFrom(this.chat.getMessages(conversationId, current[0].id, PAGE_SIZE));
    this.threadsSignal.update((all) => ({ ...all, [conversationId]: mergeById(older, all[conversationId] ?? []) }));
    this.hasMoreSignal.update((all) => ({ ...all, [conversationId]: older.length >= PAGE_SIZE }));
  }

  /** Sends over REST. The reply (status "sent", or "delivered" if they are online) is added to the thread. */
  async send(conversationId: string, content: string): Promise<DirectMessageResponse> {
    const sent = await firstValueFrom(this.chat.sendMessage(conversationId, content));
    this.lastTypingSentAt.delete(conversationId); // the next thing typed should be announced at once
    this.appendToThread(sent);
    this.updateConversation(conversationId, (c) => ({ ...c, lastMessage: sent }), true);
    return sent;
  }

  /** Start (or reopen) a chat with someone found by name search. The conversation is added at the top. */
  async startConversation(userId: number): Promise<ConversationResponse> {
    const conversation = await firstValueFrom(this.chat.startConversation(userId));
    this.conversationsSignal.update((list) => (list.some((c) => c.id === conversation.id) ? list : [conversation, ...list]));
    return conversation;
  }

  /** Marks the open conversation read, but only when the tab is actually visible to the person. */
  async markReadIfVisible(): Promise<void> {
    const id = this.openIdSignal();
    if (!id || !this.isVisible()) {
      return;
    }
    const conversation = this.find(id);
    if (!conversation || conversation.unreadCount === 0) {
      return;
    }
    this.setUnread(id, 0); // optimistic: the badge clears at once
    try {
      await firstValueFrom(this.chat.markRead(id));
    } catch {
      // The next refresh restores the true count if the server did not record it.
    }
  }

  // ---- live events --------------------------------------------------------------------------

  private async onMessageReceived(message: DirectMessageResponse): Promise<void> {
    if (message.senderId === this.myId() || this.threadHas(message.conversationId, message.id)) {
      return;
    }

    this.clearTyping(message.conversationId); // they sent it, so they are no longer "typing"

    let conversation = this.find(message.conversationId);
    if (conversation?.lastMessage?.id === message.id) {
      return; // the same message delivered twice (its thread may not be loaded, so threadHas could not catch it)
    }
    if (!conversation) {
      await this.refreshConversations(); // someone started a chat with us: we have not seen it yet
      conversation = this.find(message.conversationId);
      if (!conversation) {
        return;
      }
    }

    this.appendToThread(message);
    // After a refresh the server's list may already include this message in its unread count.
    const alreadyCounted = conversation.lastMessage?.id === message.id;
    this.updateConversation(
      message.conversationId,
      (c) => ({ ...c, lastMessage: message, unreadCount: alreadyCounted ? c.unreadCount : c.unreadCount + 1 }),
      true,
    );

    if (this.openIdSignal() === message.conversationId && this.isVisible()) {
      void this.markReadIfVisible(); // they are looking at it: read at once, no toast
    } else {
      this.toast.show(`${conversation.peer.name}: ${previewOf(message.content)}`, 'info');
    }
  }

  private onStatusChanged(event: StatusChangedEvent): void {
    const ids = new Set(event.messageIds);
    const upgrade = (m: DirectMessageResponse): DirectMessageResponse =>
      ids.has(m.id) && STATUS_RANK[event.status] > STATUS_RANK[m.status] ? { ...m, status: event.status } : m;

    this.threadsSignal.update((all) =>
      all[event.conversationId] ? { ...all, [event.conversationId]: all[event.conversationId].map(upgrade) } : all,
    );
    this.updateConversation(event.conversationId, (c) => (c.lastMessage ? { ...c, lastMessage: upgrade(c.lastMessage) } : c), false);
  }

  private onMessagesRead(event: MessagesReadEvent): void {
    this.setUnread(event.conversationId, 0); // read in another tab of ours
  }

  private onPeerTyping(event: PeerTypingEvent): void {
    if (event.userId === this.myId()) {
      return;
    }
    this.typingSignal.update((all) => ({ ...all, [event.conversationId]: true }));
    const existing = this.typingTimers.get(event.conversationId);
    if (existing) {
      clearTimeout(existing);
    }
    this.typingTimers.set(
      event.conversationId,
      setTimeout(() => this.clearTyping(event.conversationId), TYPING_SHOWN_MS),
    );
  }

  private clearTyping(conversationId: string): void {
    const timer = this.typingTimers.get(conversationId);
    if (timer) {
      clearTimeout(timer);
      this.typingTimers.delete(conversationId);
    }
    this.typingSignal.update((all) => {
      if (!all[conversationId]) {
        return all;
      }
      const { [conversationId]: _removed, ...rest } = all;
      return rest;
    });
  }

  private onPeerPresence(event: PeerPresenceEvent): void {
    this.conversationsSignal.update((list) =>
      list.map((c) =>
        c.peer.id === event.userId
          ? { ...c, peerOnline: event.online, peerLastSeenAt: event.lastSeenAt ?? c.peerLastSeenAt }
          : c,
      ),
    );
    if (!event.online) {
      // Someone who went offline is not typing any more.
      this.conversationsSignal().filter((c) => c.peer.id === event.userId).forEach((c) => this.clearTyping(c.id));
    }
  }

  private onMessageDeleted(event: MessageDeletedEvent): void {
    const wasMine = this.thread(event.conversationId).find((m) => m.id === event.messageId)?.senderId === this.myId();
    this.applyDeleted(event.conversationId, event.messageId);
    if (!wasMine) {
      void this.refreshConversations(); // an unread message that was deleted no longer counts as unread
    }
  }

  private applyDeleted(conversationId: string, messageId: number): void {
    const wipe = (m: DirectMessageResponse): DirectMessageResponse => (m.id === messageId ? { ...m, content: '', deleted: true } : m);
    this.threadsSignal.update((all) => (all[conversationId] ? { ...all, [conversationId]: all[conversationId].map(wipe) } : all));
    this.updateConversation(conversationId, (c) => (c.lastMessage ? { ...c, lastMessage: wipe(c.lastMessage) } : c), false);
  }

  /** After a reconnect or coming back to the tab, events may have been missed: refetch what is on screen. */
  private async resync(): Promise<void> {
    await this.refreshConversations();
    const open = this.openIdSignal();
    if (open) {
      await this.loadThread(open);
      await this.markReadIfVisible();
    }
  }

  private readonly onVisibilityChange = (): void => {
    if (this.isVisible()) {
      void this.resync();
    }
  };

  // ---- helpers ------------------------------------------------------------------------------

  private isVisible(): boolean {
    return this.document.visibilityState === 'visible';
  }

  private find(conversationId: string): ConversationResponse | undefined {
    return this.conversationsSignal().find((c) => c.id === conversationId);
  }

  private threadHas(conversationId: string, messageId: number): boolean {
    return this.thread(conversationId).some((m) => m.id === messageId);
  }

  /** Only adds to a thread we have loaded; an unloaded one is fetched whole when it is opened. */
  private appendToThread(message: DirectMessageResponse): void {
    this.threadsSignal.update((all) => {
      const existing = all[message.conversationId];
      if (!existing || existing.some((m) => m.id === message.id)) {
        return all;
      }
      return { ...all, [message.conversationId]: [...existing, message] };
    });
  }

  private setUnread(conversationId: string, count: number): void {
    this.updateConversation(conversationId, (c) => ({ ...c, unreadCount: count }), false);
  }

  private updateConversation(
    conversationId: string,
    change: (c: ConversationResponse) => ConversationResponse,
    moveToTop: boolean,
  ): void {
    this.conversationsSignal.update((list) => {
      const index = list.findIndex((c) => c.id === conversationId);
      if (index < 0) {
        return list;
      }
      const updated = change(list[index]);
      if (!moveToTop) {
        return list.map((c, i) => (i === index ? updated : c));
      }
      return [updated, ...list.filter((_, i) => i !== index)];
    });
  }
}

/** Union of two lists of messages by id (the second wins on a clash), oldest first. */
function mergeById(first: DirectMessageResponse[], second: DirectMessageResponse[]): DirectMessageResponse[] {
  const byId = new Map<number, DirectMessageResponse>();
  for (const m of first) byId.set(m.id, m);
  for (const m of second) byId.set(m.id, m);
  return [...byId.values()].sort((a, b) => a.id - b.id);
}
