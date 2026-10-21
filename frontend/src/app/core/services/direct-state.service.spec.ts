import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';

import {
  ConversationResponse,
  DirectMessageResponse,
  DirectMessageStatus,
  MessagesReadEvent,
  StatusChangedEvent,
} from '../models/direct.models';
import { AuthService } from './auth.service';
import { DirectChatService } from './direct-chat.service';
import { DirectHubService } from './direct-hub.service';
import { DirectStateService, previewOf } from './direct-state.service';
import { ToastService } from './toast.service';

const ME = 1;
const flush = () => new Promise<void>((resolve) => setTimeout(resolve));

function msg(id: number, conversationId: string, senderId: number, over: Partial<DirectMessageResponse> = {}): DirectMessageResponse {
  return {
    id,
    conversationId,
    senderId,
    content: `message ${id}`,
    createdAt: `2026-10-21T10:${String(id).padStart(2, '0')}:00Z`,
    status: 'sent',
    deleted: false,
    ...over,
  };
}

function conv(id: string, peerName: string, unread = 0, last: DirectMessageResponse | null = null): ConversationResponse {
  return { id, peer: { id: 100 + id.length, name: peerName }, lastMessage: last, unreadCount: unread, peerOnline: false, peerLastSeenAt: null };
}

describe('previewOf', () => {
  it('flattens whitespace and trims', () => {
    expect(previewOf('  hello \n\n  world  ')).toBe('hello world');
  });

  it('cuts long text at 80 characters with an ellipsis', () => {
    const out = previewOf('x'.repeat(200));
    expect(out.length).toBe(80);
    expect(out.endsWith('…')).toBeTrue();
  });
});

describe('DirectStateService', () => {
  let state: DirectStateService;
  let toast: ToastService;
  let chat: jasmine.SpyObj<DirectChatService>;
  let visibility: 'visible' | 'hidden';

  // The hub is replaced by plain subjects so tests can fire "live" events by hand.
  const messageReceived$ = new Subject<DirectMessageResponse>();
  const statusChanged$ = new Subject<StatusChangedEvent>();
  const messagesRead$ = new Subject<MessagesReadEvent>();
  const reconnected$ = new Subject<void>();
  let hub: { start: jasmine.Spy; stop: jasmine.Spy } & Record<string, unknown>;

  async function startWith(conversations: ConversationResponse[]): Promise<void> {
    chat.listConversations.and.returnValue(of(conversations));
    state.start();
    await flush();
  }

  beforeEach(() => {
    visibility = 'visible';
    chat = jasmine.createSpyObj<DirectChatService>('DirectChatService', [
      'listConversations', 'getMessages', 'sendMessage', 'markRead', 'startConversation', 'searchUsers',
    ]);
    chat.getMessages.and.returnValue(of([]));
    chat.markRead.and.returnValue(of({ conversationId: '', readCount: 1, upToMessageId: 1 }));
    hub = { start: jasmine.createSpy('start').and.resolveTo(), stop: jasmine.createSpy('stop').and.resolveTo(), messageReceived$, statusChanged$, messagesRead$, reconnected$ };

    TestBed.configureTestingModule({
      providers: [
        { provide: DirectChatService, useValue: chat },
        { provide: DirectHubService, useValue: hub },
        { provide: AuthService, useValue: { user: signal({ id: ME, name: 'Me', email: 'me@example.test', roles: ['User'] }) } },
      ],
    });
    state = TestBed.inject(DirectStateService);
    toast = TestBed.inject(ToastService);
    spyOnProperty(document, 'visibilityState', 'get').and.callFake(() => visibility);
  });

  afterEach(() => state.stop());

  // ---- start / list / unread total ---------------------------------------------------------

  it('connects the hub and loads the conversation list on start, and sums the unread total', async () => {
    await startWith([conv('a', 'Alice', 2), conv('b', 'Bob', 3), conv('c', 'Carol', 0)]);

    expect(hub.start).toHaveBeenCalledTimes(1);
    expect(state.conversations().map((c) => c.id)).toEqual(['a', 'b', 'c']);
    expect(state.unreadTotal()).toBe(5);
  });

  it('start() twice does not connect twice', async () => {
    await startWith([]);
    state.start();

    expect(hub.start).toHaveBeenCalledTimes(1);
  });

  it('shows a readable error when the list cannot be loaded', async () => {
    chat.listConversations.and.returnValue(throwError(() => new Error('offline')));
    state.start();
    await flush();

    expect(state.loadError()).toBe('Could not load your messages.');
    expect(state.conversations()).toEqual([]);
  });

  // ---- a message arrives -------------------------------------------------------------------

  it('counts a message for a conversation you are not looking at as unread, moves it to the top and shows a toast', async () => {
    await startWith([conv('a', 'Alice'), conv('b', 'Bob')]);

    messageReceived$.next(msg(7, 'b', 200, { content: 'are you free tonight?' }));
    await flush();

    expect(state.conversations().map((c) => c.id)).toEqual(['b', 'a']);
    expect(state.conversations()[0].unreadCount).toBe(1);
    expect(state.conversations()[0].lastMessage?.id).toBe(7);
    expect(state.unreadTotal()).toBe(1);
    expect(toast.toasts().map((t) => t.message)).toEqual(['Bob: are you free tonight?']);
    expect(toast.toasts()[0].kind).toBe('info');
    expect(chat.markRead).not.toHaveBeenCalled();
  });

  it('marks a message read at once, with no toast and no unread, when that conversation is open and visible', async () => {
    await startWith([conv('a', 'Alice')]);
    await state.openConversation('a');
    chat.markRead.calls.reset();

    messageReceived$.next(msg(8, 'a', 200));
    await flush();

    expect(state.thread('a').map((m) => m.id)).toEqual([8]);
    expect(state.conversations()[0].unreadCount).toBe(0);
    expect(chat.markRead).toHaveBeenCalledOnceWith('a');
    expect(toast.toasts()).toEqual([]);
  });

  it('still counts it as unread and toasts when the conversation is open but the tab is hidden', async () => {
    await startWith([conv('a', 'Alice')]);
    await state.openConversation('a');
    chat.markRead.calls.reset();
    visibility = 'hidden';

    messageReceived$.next(msg(9, 'a', 200, { content: 'ping' }));
    await flush();

    expect(state.conversations()[0].unreadCount).toBe(1);
    expect(chat.markRead).not.toHaveBeenCalled();
    expect(toast.toasts().map((t) => t.message)).toEqual(['Alice: ping']);
  });

  it('marks the open conversation read when the tab becomes visible again, and refetches what it missed', async () => {
    await startWith([conv('a', 'Alice')]);
    await state.openConversation('a');
    visibility = 'hidden';
    messageReceived$.next(msg(9, 'a', 200));
    await flush();
    chat.markRead.calls.reset();
    chat.listConversations.and.returnValue(of([conv('a', 'Alice', 1, msg(9, 'a', 200))]));
    chat.getMessages.and.returnValue(of([msg(9, 'a', 200)]));

    visibility = 'visible';
    document.dispatchEvent(new Event('visibilitychange'));
    await flush();

    expect(chat.getMessages).toHaveBeenCalled();
    expect(chat.markRead).toHaveBeenCalledOnceWith('a');
    expect(state.conversations()[0].unreadCount).toBe(0);
  });

  it('ignores a duplicate delivery of the same message', async () => {
    await startWith([conv('a', 'Alice')]);

    messageReceived$.next(msg(7, 'a', 200));
    await flush();
    messageReceived$.next(msg(7, 'a', 200));
    await flush();

    expect(state.conversations()[0].unreadCount).toBe(1);
    expect(toast.toasts().length).toBe(1);
  });

  it('ignores an event for a message you sent yourself', async () => {
    await startWith([conv('a', 'Alice')]);

    messageReceived$.next(msg(7, 'a', ME));
    await flush();

    expect(state.unreadTotal()).toBe(0);
    expect(toast.toasts()).toEqual([]);
  });

  it('reloads the list for a conversation it has not seen, without counting the message twice', async () => {
    await startWith([conv('a', 'Alice')]);
    const incoming = msg(11, 'new', 300, { content: 'hi, it is Dana' });
    // The server list already includes the new conversation with this message counted as unread.
    chat.listConversations.and.returnValue(of([conv('new', 'Dana', 1, incoming), conv('a', 'Alice')]));

    messageReceived$.next(incoming);
    await flush();

    expect(state.conversations().map((c) => c.id)).toEqual(['new', 'a']);
    expect(state.conversations()[0].unreadCount).toBe(1); // not 2
    expect(toast.toasts().map((t) => t.message)).toEqual(['Dana: hi, it is Dana']);
  });

  // ---- ticks: status events ----------------------------------------------------------------

  it('upgrades the ticks on your messages from the status events, in the thread and the list preview', async () => {
    const mine = msg(5, 'a', ME);
    chat.getMessages.and.returnValue(of([mine]));
    await startWith([conv('a', 'Alice', 0, mine)]);
    await state.openConversation('a');

    statusChanged$.next({ conversationId: 'a', messageIds: [5], status: 'delivered' });
    expect(state.thread('a')[0].status).toBe('delivered');
    expect(state.conversations()[0].lastMessage?.status).toBe('delivered');

    statusChanged$.next({ conversationId: 'a', messageIds: [5], status: 'read' });
    expect(state.thread('a')[0].status).toBe('read');
    expect(state.conversations()[0].lastMessage?.status).toBe('read');
  });

  it('never moves a tick backwards: a late "delivered" does not undo "read"', async () => {
    const read = msg(5, 'a', ME, { status: 'read' as DirectMessageStatus });
    chat.getMessages.and.returnValue(of([read]));
    await startWith([conv('a', 'Alice', 0, read)]);
    await state.openConversation('a');

    statusChanged$.next({ conversationId: 'a', messageIds: [5], status: 'delivered' });

    expect(state.thread('a')[0].status).toBe('read');
  });

  it('only touches the messages named in the event', async () => {
    chat.getMessages.and.returnValue(of([msg(1, 'a', ME), msg(2, 'a', ME), msg(3, 'a', ME)]));
    await startWith([conv('a', 'Alice')]);
    await state.openConversation('a');

    statusChanged$.next({ conversationId: 'a', messageIds: [1, 3], status: 'read' });

    expect(state.thread('a').map((m) => m.status)).toEqual(['read', 'sent', 'read']);
  });

  it('clears the unread count when told you read the conversation in another tab', async () => {
    await startWith([conv('a', 'Alice', 4)]);

    messagesRead$.next({ conversationId: 'a', upToMessageId: 99 });

    expect(state.unreadTotal()).toBe(0);
  });

  // ---- opening, sending, starting ----------------------------------------------------------

  it('opening a conversation loads its thread and marks it read when there is something unread and the tab is visible', async () => {
    chat.getMessages.and.returnValue(of([msg(1, 'a', 200), msg(2, 'a', 200)]));
    await startWith([conv('a', 'Alice', 2)]);

    await state.openConversation('a');

    expect(state.thread('a').map((m) => m.id)).toEqual([1, 2]);
    expect(chat.markRead).toHaveBeenCalledOnceWith('a');
    expect(state.unreadTotal()).toBe(0);
  });

  it('does not mark anything read while the tab is hidden', async () => {
    visibility = 'hidden';
    await startWith([conv('a', 'Alice', 2)]);

    await state.openConversation('a');

    expect(chat.markRead).not.toHaveBeenCalled();
    expect(state.unreadTotal()).toBe(2);
  });

  it('does not call the server when nothing is unread', async () => {
    await startWith([conv('a', 'Alice', 0)]);

    await state.openConversation('a');

    expect(chat.markRead).not.toHaveBeenCalled();
  });

  it('sending adds the saved message (with its real status) to the thread and moves the conversation to the top', async () => {
    chat.getMessages.and.returnValue(of([]));
    await startWith([conv('a', 'Alice'), conv('b', 'Bob')]);
    await state.openConversation('b');
    chat.sendMessage.and.returnValue(of(msg(20, 'b', ME, { content: 'hello Bob', status: 'delivered' })));

    const sent = await state.send('b', 'hello Bob');

    expect(chat.sendMessage).toHaveBeenCalledOnceWith('b', 'hello Bob');
    expect(sent.status).toBe('delivered');
    expect(state.thread('b').map((m) => m.id)).toEqual([20]);
    expect(state.conversations()[0].id).toBe('b');
    expect(state.conversations()[0].lastMessage?.content).toBe('hello Bob');
  });

  it('a failed send leaves the thread untouched and rejects so the page can keep the draft', async () => {
    await startWith([conv('a', 'Alice')]);
    await state.openConversation('a');
    chat.sendMessage.and.returnValue(throwError(() => new Error('boom')));

    await expectAsync(state.send('a', 'hi')).toBeRejected();

    expect(state.thread('a')).toEqual([]);
  });

  it('starting a chat adds the new conversation at the top, and reopening an existing one adds nothing', async () => {
    await startWith([conv('a', 'Alice')]);
    chat.startConversation.and.returnValue(of(conv('n', 'Newcomer')));
    await state.startConversation(5);
    expect(state.conversations().map((c) => c.id)).toEqual(['n', 'a']);

    chat.startConversation.and.returnValue(of(conv('a', 'Alice')));
    await state.startConversation(6);
    expect(state.conversations().map((c) => c.id)).toEqual(['n', 'a']);
  });

  // ---- history paging ----------------------------------------------------------------------

  it('knows there may be earlier messages when a full page came back, and prepends them without duplicates', async () => {
    const page = Array.from({ length: 50 }, (_, i) => msg(100 + i, 'a', 200));
    chat.getMessages.and.returnValue(of(page));
    await startWith([conv('a', 'Alice')]);
    await state.openConversation('a');
    expect(state.hasMore('a')).toBeTrue();

    chat.getMessages.and.returnValue(of([msg(98, 'a', 200), msg(99, 'a', 200), msg(100, 'a', 200)]));
    await state.loadOlder('a');

    expect(chat.getMessages).toHaveBeenCalledWith('a', 100, 50);
    expect(state.thread('a').map((m) => m.id).slice(0, 4)).toEqual([98, 99, 100, 101]);
    expect(state.thread('a').length).toBe(52); // 50 + 2 new, the duplicate 100 merged
    expect(state.hasMore('a')).toBeFalse(); // a short page: nothing earlier
  });

  // ---- recovery and sign-out ---------------------------------------------------------------

  it('refetches the list and the open thread after the connection comes back', async () => {
    await startWith([conv('a', 'Alice')]);
    await state.openConversation('a');
    chat.listConversations.calls.reset();
    chat.getMessages.calls.reset();
    chat.getMessages.and.returnValue(of([msg(30, 'a', 200)]));

    reconnected$.next();
    await flush();

    expect(chat.listConversations).toHaveBeenCalledTimes(1);
    expect(chat.getMessages).toHaveBeenCalledTimes(1);
    expect(state.thread('a').map((m) => m.id)).toEqual([30]);
  });

  it('forgets everything, stops the hub and ignores later events once stopped (sign-out)', async () => {
    await startWith([conv('a', 'Alice', 3)]);
    await state.openConversation('a');

    state.stop();
    messageReceived$.next(msg(40, 'a', 200));
    await flush();

    expect(hub.stop).toHaveBeenCalledTimes(1);
    expect(state.conversations()).toEqual([]);
    expect(state.unreadTotal()).toBe(0);
    expect(state.openConversationId()).toBeNull();
    expect(state.thread('a')).toEqual([]);
    expect(toast.toasts()).toEqual([]);
  });
});
