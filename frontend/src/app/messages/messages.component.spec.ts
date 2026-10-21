import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, Subject, of, throwError } from 'rxjs';

import { ConversationResponse, DirectMessageResponse } from '../core/models/direct.models';
import { AuthService } from '../core/services/auth.service';
import { DirectChatService } from '../core/services/direct-chat.service';
import { DirectHubService } from '../core/services/direct-hub.service';
import { MessagesComponent } from './messages.component';

const ME = 1;

function msg(id: number, sender: number, over: Partial<DirectMessageResponse> = {}): DirectMessageResponse {
  return { id, conversationId: 'c1', senderId: sender, content: `text ${id}`, createdAt: '2026-10-21T10:15:00Z', status: 'sent', deleted: false, ...over };
}

function conv(id: string, name: string, unread = 0, last: DirectMessageResponse | null = null): ConversationResponse {
  return { id, peer: { id: 50 + id.length, name }, lastMessage: last, unreadCount: unread, peerOnline: false, peerLastSeenAt: null };
}

describe('MessagesComponent', () => {
  let fixture: ComponentFixture<MessagesComponent>;
  let el: HTMLElement;
  let chat: jasmine.SpyObj<DirectChatService>;
  let router: Router;
  let query: BehaviorSubject<ReturnType<typeof convertToParamMap>>;

  async function setup(conversations: ConversationResponse[], thread: DirectMessageResponse[] = [], selected: string | null = null) {
    chat = jasmine.createSpyObj<DirectChatService>('DirectChatService', [
      'listConversations', 'getMessages', 'sendMessage', 'markRead', 'startConversation', 'searchUsers',
    ]);
    chat.listConversations.and.returnValue(of(conversations));
    chat.getMessages.and.returnValue(of(thread));
    chat.markRead.and.returnValue(of({ conversationId: 'c1', readCount: 0, upToMessageId: null }));
    chat.searchUsers.and.returnValue(of([]));
    query = new BehaviorSubject(convertToParamMap(selected ? { c: selected } : {}));

    await TestBed.configureTestingModule({
      imports: [MessagesComponent],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { queryParamMap: query.asObservable() } },
        { provide: DirectChatService, useValue: chat },
        {
          provide: DirectHubService,
          useValue: {
            start: () => Promise.resolve(), stop: () => Promise.resolve(),
            messageReceived$: new Subject(), statusChanged$: new Subject(), messagesRead$: new Subject(), reconnected$: new Subject(),
          },
        },
        { provide: AuthService, useValue: { user: signal({ id: ME, name: 'Me', email: 'me@example.test', roles: ['User'] }) } },
      ],
    }).compileComponents();

    router = TestBed.inject(Router);
    spyOn(router, 'navigate').and.resolveTo(true);
    fixture = TestBed.createComponent(MessagesComponent);
    el = fixture.nativeElement;
    // The page is opened after the app has started the state (AppComponent does this in the real app).
    await fixture.componentInstance.state.refreshConversations();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const rows = () => Array.from(el.querySelectorAll<HTMLElement>('.conversation'));
  const bubbles = () => Array.from(el.querySelectorAll<HTMLElement>('.bubble-row'));

  // ---- list --------------------------------------------------------------------------------

  it('lists conversations with the peer name, a preview, and an unread badge', async () => {
    await setup([conv('c1', 'Alice Rao', 3, msg(5, 50, { content: 'see you at 5' })), conv('c2', 'Bob Menon')]);

    expect(rows().length).toBe(2);
    expect(rows()[0].querySelector('.conv-name')?.textContent).toBe('Alice Rao');
    expect(rows()[0].querySelector('.conv-preview')?.textContent).toBe('see you at 5');
    expect(rows()[0].querySelector('.unread')?.textContent?.trim()).toBe('3');
    expect(rows()[0].querySelector('.unread')?.getAttribute('aria-label')).toBe('3 unread');
    expect(rows()[1].querySelector('.unread')).toBeNull();
    expect(rows()[1].querySelector('.conv-preview')?.textContent).toBe('No messages yet');
  });

  it('prefixes your own last message with "You:" and shows its ticks, but shows no ticks for theirs', async () => {
    await setup([
      conv('c1', 'Alice Rao', 0, msg(5, ME, { content: 'sure', status: 'read' })),
      conv('c2', 'Bob Menon', 1, msg(6, 77, { content: 'hey' })),
    ]);

    expect(rows()[0].querySelector('.conv-preview')?.textContent).toBe('You: sure');
    expect(rows()[0].querySelector('app-ticks svg')?.getAttribute('data-status')).toBe('read');
    expect(rows()[1].querySelector('app-ticks')).toBeNull();
  });

  it('caps a huge unread count at 99+', async () => {
    await setup([conv('c1', 'Alice Rao', 250)]);

    expect(rows()[0].querySelector('.unread')?.textContent?.trim()).toBe('99+');
  });

  it('says what to do when there are no conversations yet', async () => {
    await setup([]);

    expect(el.textContent).toContain('No conversations yet');
  });

  it('opens a conversation by putting its id in the URL', async () => {
    await setup([conv('c1', 'Alice Rao')]);

    rows()[0].click();

    expect(router.navigate).toHaveBeenCalledWith([], { queryParams: { c: 'c1' } });
  });

  // ---- thread and ticks --------------------------------------------------------------------

  it('shows the selected thread, with ticks only on messages you sent, in the right state', async () => {
    await setup(
      [conv('c1', 'Alice Rao')],
      [msg(1, 50, { content: 'hi' }), msg(2, ME, { content: 'one', status: 'sent' }), msg(3, ME, { content: 'two', status: 'delivered' }), msg(4, ME, { content: 'three', status: 'read' })],
      'c1',
    );

    expect(el.querySelector('.thread-title')?.textContent).toBe('Alice Rao');
    expect(bubbles().length).toBe(4);
    expect(bubbles()[0].classList.contains('mine')).toBeFalse();
    expect(bubbles()[0].querySelector('app-ticks')).toBeNull(); // their message: no ticks
    expect(bubbles().slice(1).every((b) => b.classList.contains('mine'))).toBeTrue();
    expect(bubbles().slice(1).map((b) => b.querySelector('app-ticks svg')?.getAttribute('data-status'))).toEqual(['sent', 'delivered', 'read']);
    expect(el.querySelector('.messages-layout')?.classList.contains('thread-open')).toBeTrue();
  });

  it('renders message text as plain text, never as HTML', async () => {
    await setup([conv('c1', 'Alice Rao')], [msg(1, 50, { content: '<img src=x onerror=alert(1)> <b>bold</b>' })], 'c1');

    expect(el.querySelector('.bubble .text')?.textContent).toBe('<img src=x onerror=alert(1)> <b>bold</b>');
    expect(el.querySelector('.bubble img')).toBeNull();
    expect(el.querySelector('.bubble b')).toBeNull();
  });

  it('shows a deleted message as a placeholder with no text and no ticks', async () => {
    await setup([conv('c1', 'Alice Rao')], [msg(1, ME, { content: '', deleted: true, status: 'read' })], 'c1');

    expect(bubbles()[0].textContent).toContain('This message was deleted');
    expect(bubbles()[0].querySelector('app-ticks')).toBeNull();
  });

  it('invites you to say hello in an empty thread', async () => {
    await setup([conv('c1', 'Alice Rao')], [], 'c1');

    expect(el.textContent).toContain('Say hello to Alice Rao.');
  });

  it('shows a prompt instead of a thread when nothing is selected', async () => {
    await setup([conv('c1', 'Alice Rao')]);

    expect(el.querySelector('.empty-thread')?.textContent).toContain('Pick a conversation');
    expect(el.querySelector('.messages-layout')?.classList.contains('thread-open')).toBeFalse();
  });

  it('opening the thread marks unread messages read', async () => {
    await setup([conv('c1', 'Alice Rao', 2)], [msg(1, 50), msg(2, 50)], 'c1');

    expect(chat.markRead).toHaveBeenCalledWith('c1');
    expect(rows()[0].querySelector('.unread')).toBeNull();
  });

  it('the Chats button goes back to the list by clearing the selection', async () => {
    await setup([conv('c1', 'Alice Rao')], [], 'c1');

    (el.querySelector('.back') as HTMLButtonElement).click();

    expect(router.navigate).toHaveBeenCalledWith([], { queryParams: { c: null } });
  });

  it('leaves the page cleanly when the conversation id in the URL does not exist', async () => {
    chat = undefined as never;
    await setup([conv('c1', 'Alice Rao')]);
    chat.getMessages.and.returnValue(throwError(() => new Error('404')));

    query.next(convertToParamMap({ c: 'gone' }));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(router.navigate).toHaveBeenCalledWith([], { queryParams: { c: null }, replaceUrl: true });
  });

  // ---- sending -----------------------------------------------------------------------------

  it('sends the trimmed draft, clears the box, and shows the new message with a single grey tick', async () => {
    await setup([conv('c1', 'Alice Rao')], [], 'c1');
    chat.sendMessage.and.returnValue(of(msg(9, ME, { content: 'on my way', status: 'sent' })));
    const box = el.querySelector('#direct-composer') as HTMLTextAreaElement;

    box.value = '  on my way  ';
    box.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (el.querySelector('.composer button[type=submit]') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(chat.sendMessage).toHaveBeenCalledOnceWith('c1', 'on my way');
    expect(fixture.componentInstance.draft()).toBe('');
    expect(bubbles().length).toBe(1);
    expect(bubbles()[0].querySelector('app-ticks svg')?.getAttribute('data-status')).toBe('sent');
  });

  it('does not send an empty or whitespace-only draft, and the Send button is disabled', async () => {
    await setup([conv('c1', 'Alice Rao')], [], 'c1');
    fixture.componentInstance.draft.set('   ');
    fixture.detectChanges();

    await fixture.componentInstance.send();

    expect(chat.sendMessage).not.toHaveBeenCalled();
    expect((el.querySelector('.composer button[type=submit]') as HTMLButtonElement).disabled).toBeTrue();
  });

  it('keeps what you typed and shows an error when sending fails', async () => {
    await setup([conv('c1', 'Alice Rao')], [], 'c1');
    chat.sendMessage.and.returnValue(throwError(() => new Error('network')));
    fixture.componentInstance.draft.set('important words');

    await fixture.componentInstance.send();
    fixture.detectChanges();

    expect(fixture.componentInstance.draft()).toBe('important words');
    expect(el.querySelector('.form-error')?.textContent).toContain("This message didn't send.");
    expect(fixture.componentInstance.sending()).toBeFalse();
  });

  it('Enter sends, Shift+Enter does not', async () => {
    await setup([conv('c1', 'Alice Rao')], [], 'c1');
    chat.sendMessage.and.returnValue(of(msg(9, ME)));
    fixture.componentInstance.draft.set('hello');

    const shiftEnter = new KeyboardEvent('keydown', { key: 'Enter', shiftKey: true, cancelable: true });
    fixture.componentInstance.onDraftKeydown(shiftEnter);
    expect(shiftEnter.defaultPrevented).toBeFalse();
    expect(chat.sendMessage).not.toHaveBeenCalled();

    const enter = new KeyboardEvent('keydown', { key: 'Enter', cancelable: true });
    fixture.componentInstance.onDraftKeydown(enter);
    expect(enter.defaultPrevented).toBeTrue();
    expect(chat.sendMessage).toHaveBeenCalledOnceWith('c1', 'hello');
  });

  // ---- people search -----------------------------------------------------------------------
  // The search box feeds an RxJS pipeline (debounce 250 ms) through an Angular effect, so these tests
  // change the signal, run change detection so the effect fires, then wait out the debounce for real.

  const typeSearch = async (term: string) => {
    fixture.componentInstance.searchTerm.set(term);
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 320));
    fixture.detectChanges();
  };

  it('does not search for fewer than two characters', async () => {
    await setup([conv('c1', 'Alice Rao')]);

    await typeSearch('a');

    expect(chat.searchUsers).not.toHaveBeenCalled();
    expect(el.querySelector('.results')).toBeNull();
  });

  it('searches once after typing pauses and lists name-only results', async () => {
    await setup([conv('c1', 'Alice Rao')]);
    chat.searchUsers.and.returnValue(of([{ id: 9, name: 'Dana Shah' }]));

    fixture.componentInstance.searchTerm.set('d');
    fixture.componentInstance.searchTerm.set('da');
    await typeSearch('dan'); // three keystrokes inside one debounce window

    expect(chat.searchUsers).toHaveBeenCalledOnceWith('dan');
    expect(el.querySelector('.person-name')?.textContent).toBe('Dana Shah');
  });

  it('says so when no one matches', async () => {
    await setup([conv('c1', 'Alice Rao')]);
    chat.searchUsers.and.returnValue(of([]));

    await typeSearch('zzz');

    expect(el.querySelector('.results')?.textContent).toContain('No one found with that name.');
  });

  it('clicking a search result starts the chat, clears the search and opens it', async () => {
    await setup([conv('c1', 'Alice Rao')]);
    chat.searchUsers.and.returnValue(of([{ id: 9, name: 'Dana Shah' }]));
    chat.startConversation.and.returnValue(of(conv('new1', 'Dana Shah')));
    await typeSearch('dana');

    (el.querySelector('.person') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(chat.startConversation).toHaveBeenCalledOnceWith(9);
    expect(fixture.componentInstance.searchTerm()).toBe('');
    expect(router.navigate).toHaveBeenCalledWith([], { queryParams: { c: 'new1' } });
  });
});
