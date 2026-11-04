import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, Subject } from 'rxjs';

import { environment } from '../../environments/environment';
import { MessageResponse, SessionResponse } from '../core/models/chat.models';
import { AuthService } from '../core/services/auth.service';
import { STREAM_REPLIES } from '../core/services/chat.service';
import { DirectHubService } from '../core/services/direct-hub.service';
import { ChatComponent } from './chat.component';

const API = `${environment.apiBaseUrl}/chat`;
const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
const session = (id: string, title: string, createdAt = '2026-10-24T09:00:00Z'): SessionResponse => ({ id, title, createdAt });
const msg = (id: number, role: 'user' | 'assistant' = 'user'): MessageResponse => ({ id, role, content: `m${id}`, createdAt: '2026-10-24T10:00:00Z' });
/** `count` chats, newest first, like the server sends them: c{count} ... c1, one minute apart. */
const manySessions = (count: number): SessionResponse[] =>
  Array.from({ length: count }, (_, i) => session(`s${count - i}`, `c${count - i}`, new Date(Date.UTC(2026, 9, 24, 8, count - i)).toISOString()));
/** Messages with ids from..to (ascending). */
const range = (from: number, to: number): MessageResponse[] => Array.from({ length: to - from + 1 }, (_, i) => msg(from + i));

/** Phase 24: renaming, deleting, searching and paging of chats and messages, with the API answered by hand. */
describe('ChatComponent chat management', () => {
  let fixture: ComponentFixture<ChatComponent>;
  let component: ChatComponent;
  let http: HttpTestingController;
  let router: Router;
  let query: BehaviorSubject<ReturnType<typeof convertToParamMap>>;
  const auth = { logout: jasmine.createSpy('logout'), hasRole: () => false, user: () => ({ id: 1, name: 'Ada Lovelace', email: 'ada@example.com', roles: ['User'] }) };
  const hub = { assistantReplied$: new Subject(), assistantFailed$: new Subject(), reconnected$: new Subject() };

  const root = () => fixture.nativeElement as HTMLElement;
  const el = <T extends Element>(selector: string) => root().querySelector<T>(selector);
  const all = <T extends Element>(selector: string) => Array.from(root().querySelectorAll<T>(selector));
  const text = () => root().textContent ?? '';
  const titles = () => component.sessions().map((s) => s.title);

  async function setup(sessions: SessionResponse[] = [session('s1', 'Trip plans'), session('s2', 'Budget')]): Promise<void> {
    query = new BehaviorSubject(convertToParamMap({}));
    await TestBed.configureTestingModule({
      imports: [ChatComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: STREAM_REPLIES, useValue: false }, // these specs cover the one-shot request; streaming has its own spec
        { provide: ActivatedRoute, useValue: { queryParamMap: query } },
        { provide: DirectHubService, useValue: hub },
        { provide: AuthService, useValue: auth },
      ],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    spyOn(router, 'navigate').and.resolveTo(true);
    fixture = TestBed.createComponent(ChatComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    http.expectOne(`${API}/sessions`).flush(sessions);
    fixture.detectChanges();
  }

  async function open(id: string, messages: MessageResponse[] = []): Promise<void> {
    query.next(convertToParamMap({ session: id }));
    fixture.detectChanges();
    http.expectOne(`${API}/sessions/${id}/messages`).flush(messages);
    http.expectOne(`${API}/sessions/${id}/jobs`).flush([]);
    fixture.detectChanges();
  }

  afterEach(() => http.verify());

  describe('the chat list pages', () => {
    it('knows there may be more after a full first page, and none after a short one', async () => {
      await setup(manySessions(50));
      expect(component.hasMoreSessions()).toBeTrue();
      expect(el('button.more')).not.toBeNull();
      TestBed.resetTestingModule();

      await setup(manySessions(49));
      expect(component.hasMoreSessions()).toBeFalse();
      expect(el('button.more')).toBeNull();
    });

    it('asks for the page after the last chat shown, appends it, and stops on a short page', async () => {
      const first = manySessions(50);
      await setup(first);

      component.loadMoreSessions();
      const req = http.expectOne((r) => r.url === `${API}/sessions` && r.params.get('before') === first[49].createdAt);
      expect(req.request.params.has('search')).toBeFalse();
      req.flush([session('old1', 'Older one', '2026-10-01T00:00:00Z')]);

      expect(component.sessions().length).toBe(51);
      expect(component.sessions().at(-1)?.title).toBe('Older one');
      expect(component.hasMoreSessions()).toBeFalse();
      component.loadMoreSessions(); // nothing more: no request (http.verify in afterEach)
    });

    it('does not ask twice while a page is already loading, and never adds a chat twice', async () => {
      const first = manySessions(50);
      await setup(first);

      component.loadMoreSessions();
      component.loadMoreSessions();
      const req = http.expectOne((r) => r.params.has('before'));
      req.flush([first[49], session('old1', 'Older one')]); // the boundary chat comes back again

      expect(component.sessions().filter((s) => s.id === first[49].id).length).toBe(1);
    });

    it('loads more when the list is scrolled near its bottom, not before', async () => {
      await setup(manySessions(50));
      const list = { scrollHeight: 2000, scrollTop: 100, clientHeight: 500 } as HTMLElement;

      component.onSessionListScroll({ target: list } as unknown as Event);
      http.expectNone((r) => r.params.has('before'));

      list.scrollTop = 1450;
      component.onSessionListScroll({ target: list } as unknown as Event);
      http.expectOne((r) => r.params.has('before')).flush([]);
    });

    it('offers a "Show more chats" button as a keyboard alternative to scrolling', async () => {
      await setup(manySessions(50));
      el<HTMLButtonElement>('button.more')!.click();
      http.expectOne((r) => r.params.has('before')).flush([]);
      fixture.detectChanges();
      expect(el('button.more')).toBeNull();
    });

    it('shows an error and lets the person try again when a page fails', async () => {
      await setup(manySessions(50));
      component.loadMoreSessions();
      http.expectOne((r) => r.params.has('before')).flush({ error: { code: 'x', message: 'Boom' } }, { status: 500, statusText: 'Server Error' });
      fixture.detectChanges();

      expect(component.loadingMoreSessions()).toBeFalse();
      expect(component.sessionsError()).toBeTruthy();
      expect(component.hasMoreSessions()).toBeTrue();
    });
  });

  describe('searching', () => {
    beforeEach(() => setup());

    it('asks the server after a short pause, once, however fast the person types', async () => {
      component.onSearchInput('b');
      component.onSearchInput('bu');
      component.onSearchInput('bud');
      http.expectNone((r) => r.params.has('search'));
      await wait(320);

      http.expectOne((r) => r.params.get('search') === 'bud').flush([session('s2', 'Budget')]);
      expect(titles()).toEqual(['Budget']);
    });

    it('ignores surrounding spaces and does not repeat the same search', async () => {
      component.onSearchInput('  bud ');
      await wait(320);
      http.expectOne((r) => r.params.get('search') === 'bud').flush([session('s2', 'Budget')]);

      component.onSearchInput('bud');
      await wait(320);
      http.expectNone((r) => r.url === `${API}/sessions`);
    });

    it('restores the normal list when the box is cleared', async () => {
      component.onSearchInput('bud');
      await wait(320);
      http.expectOne((r) => r.params.get('search') === 'bud').flush([session('s2', 'Budget')]);

      component.clearSearch();
      await wait(320);
      const req = http.expectOne((r) => r.url === `${API}/sessions`);
      expect(req.request.params.keys()).toEqual([]);
      req.flush([session('s1', 'Trip plans'), session('s2', 'Budget')]);
      expect(titles()).toEqual(['Trip plans', 'Budget']);
      expect(component.activeSearch()).toBe('');
    });

    it('says when nothing matches and offers to clear the search', async () => {
      component.onSearchInput('zzz');
      await wait(320);
      http.expectOne((r) => r.params.get('search') === 'zzz').flush([]);
      fixture.detectChanges();

      expect(text()).toContain('No chats match “zzz”');
      el<HTMLButtonElement>('.empty-hint .link-button')!.click();
      expect(component.searchText()).toBe('');
    });

    it('keeps the search term when it loads further pages', async () => {
      component.onSearchInput('trip');
      await wait(320);
      http.expectOne((r) => r.params.get('search') === 'trip').flush(manySessions(50));

      component.loadMoreSessions();
      const req = http.expectOne((r) => r.params.has('before'));
      expect(req.request.params.get('search')).toBe('trip');
      req.flush([]);
    });

    it('throws away a page that arrives after the search changed', async () => {
      component.onSearchInput('trip');
      await wait(320);
      http.expectOne((r) => r.params.get('search') === 'trip').flush(manySessions(50));
      component.loadMoreSessions();
      const late = http.expectOne((r) => r.params.has('before'));

      component.onSearchInput('bud');
      await wait(320);
      http.expectOne((r) => r.params.get('search') === 'bud').flush([session('s2', 'Budget')]);
      late.flush([session('late', 'Late arrival')]);

      expect(titles()).toEqual(['Budget']);
    });
  });

  describe('renaming', () => {
    beforeEach(async () => {
      await setup();
      await open('s1');
    });

    it('opens an input holding the current title', async () => {
      el<HTMLButtonElement>('.session-row .rename')!.click();
      fixture.detectChanges();
      await fixture.whenStable(); // ngModel writes the value a tick after rendering
      const input = el<HTMLInputElement>('.rename-input')!;
      expect(input.value).toBe('Trip plans');
      await wait(10); // the box takes focus and selects the old title, so typing replaces it
      expect(document.activeElement).toBe(input);
      expect([input.selectionStart, input.selectionEnd]).toEqual([0, 'Trip plans'.length]);
    });

    it('saves on Enter, trimmed, and the list and the page header both show it', () => {
      component.startRename(component.sessions()[0]);
      component.renameDraft.set('  Lisbon trip ');
      component.onRenameKeydown(new KeyboardEvent('keydown', { key: 'Enter' }), component.sessions()[0]);

      const req = http.expectOne(`${API}/sessions/s1`);
      expect(req.request.method).toBe('PATCH');
      expect(req.request.body).toEqual({ title: 'Lisbon trip' });
      req.flush(session('s1', 'Lisbon trip'));
      fixture.detectChanges();

      expect(titles()).toEqual(['Lisbon trip', 'Budget']);
      expect(component.selectedTitle()).toBe('Lisbon trip');
      expect(component.renamingId()).toBeNull();
    });

    it('makes no request on Escape, for an unchanged title, or for an empty one', () => {
      const first = component.sessions()[0];
      component.startRename(first);
      component.renameDraft.set('Something else');
      component.onRenameKeydown(new KeyboardEvent('keydown', { key: 'Escape' }), first);
      expect(component.renamingId()).toBeNull();

      component.startRename(first);
      component.commitRename(first); // unchanged
      expect(component.renamingId()).toBeNull();

      component.startRename(first);
      component.renameDraft.set('   ');
      component.commitRename(first);
      expect(component.renamingId()).toBeNull();
      expect(titles()).toEqual(['Trip plans', 'Budget']);
    });

    it('cancels when the box loses focus', () => {
      component.startRename(component.sessions()[0]);
      fixture.detectChanges();
      el<HTMLInputElement>('.rename-input')!.dispatchEvent(new Event('blur'));
      expect(component.renamingId()).toBeNull();
    });

    it('shows the server error and keeps the old title when the rename fails', () => {
      const first = component.sessions()[0];
      component.startRename(first);
      component.renameDraft.set('New name');
      component.commitRename(first);
      http.expectOne(`${API}/sessions/s1`).flush({ error: { code: 'validation_failed', message: 'Give the chat a name.' } }, { status: 400, statusText: 'Bad Request' });
      fixture.detectChanges();

      expect(titles()[0]).toBe('Trip plans');
      expect(component.sessionsError()).toBe('Give the chat a name.');
      expect(component.renameSaving()).toBeFalse();
    });

    it('does not send a second request while the first is still saving', () => {
      const first = component.sessions()[0];
      component.startRename(first);
      component.renameDraft.set('New name');
      component.commitRename(first);
      component.commitRename(first);
      http.expectOne(`${API}/sessions/s1`).flush(session('s1', 'New name'));
    });
  });

  describe('deleting', () => {
    beforeEach(async () => {
      await setup();
      await open('s1', [msg(1), msg(2, 'assistant')]);
    });

    it('asks first, naming the chat, and Cancel changes nothing', () => {
      el<HTMLButtonElement>('.session-row .delete')!.click();
      fixture.detectChanges();

      expect(el('[role=alertdialog]')).not.toBeNull();
      expect(el('#delete-text')!.textContent).toContain('Trip plans');

      el<HTMLButtonElement>('.dialog-actions .btn-quiet')!.click();
      fixture.detectChanges();
      expect(el('[role=alertdialog]')).toBeNull();
      expect(titles()).toEqual(['Trip plans', 'Budget']);
    });

    it('closes on Escape and when the backdrop is clicked', () => {
      component.askDelete(component.sessions()[1]);
      fixture.detectChanges();
      el('.dialog')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
      fixture.detectChanges();
      expect(component.deleteTarget()).toBeNull();

      component.askDelete(component.sessions()[1]);
      fixture.detectChanges();
      el<HTMLElement>('.dialog-backdrop')!.click();
      expect(component.deleteTarget()).toBeNull();
    });

    it('puts the keyboard focus on Cancel, the safe choice, when the dialog opens', () => {
      component.askDelete(component.sessions()[0]);
      fixture.detectChanges();
      expect(document.activeElement).toBe(el('.dialog-actions .btn-quiet'));
    });

    it('deletes another chat without touching the open one', () => {
      component.askDelete(component.sessions()[1]);
      component.confirmDelete();
      const req = http.expectOne(`${API}/sessions/s2`);
      expect(req.request.method).toBe('DELETE');
      req.flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(titles()).toEqual(['Trip plans']);
      expect(component.messages().length).toBe(2);
      expect(router.navigate).not.toHaveBeenCalled();
      expect(el('[role=alertdialog]')).toBeNull();
    });

    it('leaves the chat that is open: empties the thread and clears the address', () => {
      component.askDelete(component.sessions()[0]);
      component.confirmDelete();
      http.expectOne(`${API}/sessions/s1`).flush(null, { status: 204, statusText: 'No Content' });

      expect(component.sessions().map((s) => s.id)).toEqual(['s2']);
      expect(component.messages()).toEqual([]);
      expect(router.navigate).toHaveBeenCalledWith([], jasmine.objectContaining({ queryParams: { session: null } }));
    });

    it('treats "already gone" (404, deleted in another tab) as deleted', () => {
      component.askDelete(component.sessions()[1]);
      component.confirmDelete();
      http.expectOne(`${API}/sessions/s2`).flush({ error: { code: 'session_not_found', message: 'Chat session not found.' } }, { status: 404, statusText: 'Not Found' });

      expect(titles()).toEqual(['Trip plans']);
      expect(component.sessionsError()).toBeNull();
    });

    it('keeps the chat and shows the error when the server fails', () => {
      component.askDelete(component.sessions()[1]);
      component.confirmDelete();
      http.expectOne(`${API}/sessions/s2`).flush({ error: { code: 'internal_error', message: 'Something went wrong.' } }, { status: 500, statusText: 'Server Error' });
      fixture.detectChanges();

      expect(titles()).toEqual(['Trip plans', 'Budget']);
      expect(component.sessionsError()).toBe('Something went wrong.');
      expect(component.deleting()).toBeFalse();
      expect(el('[role=alertdialog]')).toBeNull();
    });

    it('cannot be confirmed twice, and cannot be cancelled while it is running', () => {
      component.askDelete(component.sessions()[1]);
      component.confirmDelete();
      component.confirmDelete();
      component.cancelDelete();
      expect(component.deleteTarget()).not.toBeNull();
      http.expectOne(`${API}/sessions/s2`).flush(null, { status: 204, statusText: 'No Content' });
    });

    it('forgets a reply that was still being produced for the deleted chat', () => {
      component.pendingJobs.set({ j1: { sessionId: 's1', userMessageId: 1 }, j2: { sessionId: 's2', userMessageId: 9 } });
      component.askDelete(component.sessions()[0]);
      component.confirmDelete();
      http.expectOne(`${API}/sessions/s1`).flush(null, { status: 204, statusText: 'No Content' });

      expect(Object.keys(component.pendingJobs())).toEqual(['j2']);
    });
  });

  describe('the thread pages', () => {
    it('offers earlier messages only after a full page', async () => {
      await setup();
      await open('s1', range(51, 100));
      expect(component.hasMoreMessages()).toBeTrue();
      expect(el('button.older')).not.toBeNull();

      query.next(convertToParamMap({ session: 's2' }));
      fixture.detectChanges();
      http.expectOne(`${API}/sessions/s2/messages`).flush([msg(201), msg(202)]);
      http.expectOne(`${API}/sessions/s2/jobs`).flush([]);
      fixture.detectChanges();
      expect(component.hasMoreMessages()).toBeFalse();
      expect(el('button.older')).toBeNull();
    });

    it('asks for the page above the oldest message and puts it in front, in order, without repeats', async () => {
      await setup();
      await open('s1', range(51, 100));

      component.loadOlderMessages();
      const req = http.expectOne((r) => r.url === `${API}/sessions/s1/messages` && r.params.get('before') === '51');
      req.flush([...range(1, 50), msg(51)]); // the boundary message comes back too
      fixture.detectChanges();

      expect(component.messages().map((m) => m.id)).toEqual(Array.from({ length: 100 }, (_, i) => i + 1));
      expect(component.hasMoreMessages()).toBeTrue(); // 51 items came back, a full page
    });

    it('stops offering earlier messages when a short page comes back', async () => {
      await setup();
      await open('s1', range(51, 100));
      component.loadOlderMessages();
      http.expectOne((r) => r.params.get('before') === '51').flush(range(40, 50));
      fixture.detectChanges();
      expect(component.hasMoreMessages()).toBeFalse();
      expect(el('button.older')).toBeNull();
    });

    it('is not fooled by a message still being sent: the cursor is the oldest saved message', async () => {
      await setup();
      await open('s1', range(51, 100));
      component.messages.update((list) => [{ id: -5, role: 'user', content: 'sending', createdAt: '', pending: true }, ...list]);

      component.loadOlderMessages();
      http.expectOne((r) => r.params.get('before') === '51').flush([]);
    });

    it('does not ask twice at once, nor when there is nothing more', async () => {
      await setup();
      await open('s1', range(51, 100));
      component.loadOlderMessages();
      component.loadOlderMessages();
      http.expectOne((r) => r.params.get('before') === '51').flush([msg(50)]);
      component.loadOlderMessages(); // short page: nothing more, no request
    });

    it('loads when the thread is scrolled to the top, not when it is not', async () => {
      await setup();
      await open('s1', range(51, 100));
      component.onMessageListScroll({ target: { scrollTop: 400 } } as unknown as Event);
      http.expectNone((r) => r.params.has('before'));
      component.onMessageListScroll({ target: { scrollTop: 10 } } as unknown as Event);
      http.expectOne((r) => r.params.get('before') === '51').flush([]);
    });

    it('drops a page that arrives after the person opened another chat', async () => {
      await setup();
      await open('s1', range(51, 100));
      component.loadOlderMessages();
      const late = http.expectOne((r) => r.params.get('before') === '51');

      query.next(convertToParamMap({ session: 's2' }));
      fixture.detectChanges();
      http.expectOne(`${API}/sessions/s2/messages`).flush([msg(201)]);
      http.expectOne(`${API}/sessions/s2/jobs`).flush([]);
      late.flush(range(1, 50));

      expect(component.messages().map((m) => m.id)).toEqual([201]);
    });

    it('shows an error and allows another try when the older page fails', async () => {
      await setup();
      await open('s1', range(51, 100));
      component.loadOlderMessages();
      http.expectOne((r) => r.params.get('before') === '51').flush({ error: { code: 'x', message: 'Boom' } }, { status: 500, statusText: 'Server Error' });

      expect(component.loadingOlder()).toBeFalse();
      expect(component.sendError()).toBeTruthy();
      expect(component.hasMoreMessages()).toBeTrue();
    });

    it('keeps the older pages already scrolled in when a missed reply is repaired by a refetch', async () => {
      await setup();
      await open('s1', range(51, 100));
      component.loadOlderMessages();
      http.expectOne((r) => r.params.get('before') === '51').flush(range(1, 50));

      hub.reconnected$.next(undefined);
      http.expectOne(`${API}/sessions/s1/messages`).flush([...range(51, 100), msg(101, 'assistant')]);
      http.expectOne(`${API}/sessions/s1/jobs`).flush([]);

      expect(component.messages().length).toBe(101);
      expect(component.messages()[0].id).toBe(1);
      expect(component.messages().at(-1)?.id).toBe(101);
    });
  });

  describe('a new chat\'s first message', () => {
    it('updates just that chat\'s title and keeps the pages and search already on screen', async () => {
      const list = [...manySessions(50)];
      list[0] = session('s50', 'New chat', list[0].createdAt);
      await setup(list);
      await open('s50');
      component.draft.set('Plan my trip');
      void component.send();
      http.expectOne(`${API}/sessions/s50/messages`).flush({ userMessage: msg(1), assistantMessage: msg(2, 'assistant') });
      http.expectOne(`${API}/sessions`).flush([session('s50', 'Plan my trip', list[0].createdAt)]);

      expect(component.sessions().length).toBe(50);
      expect(component.sessions()[0].title).toBe('Plan my trip');
    });
  });
});
