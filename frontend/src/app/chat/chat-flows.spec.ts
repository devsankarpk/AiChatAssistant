import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, Subject } from 'rxjs';

import { environment } from '../../environments/environment';
import { MessageResponse } from '../core/models/chat.models';
import { AuthService } from '../core/services/auth.service';
import { DirectHubService } from '../core/services/direct-hub.service';
import { ChatComponent } from './chat.component';

const API = `${environment.apiBaseUrl}/chat`;
const userMsg = (id: number, content: string): MessageResponse => ({ id, role: 'user', content, createdAt: '2026-10-24T10:00:00Z' });
const botMsg = (id: number, content: string): MessageResponse => ({ id, role: 'assistant', content, createdAt: '2026-10-24T10:00:02Z' });
const session = (id: string, title: string) => ({ id, title, createdAt: '2026-10-24T09:00:00Z' });

/** The synchronous send flow (async replies off) and the sidebar, with the API answered by hand. */
describe('ChatComponent flows', () => {
  let fixture: ComponentFixture<ChatComponent>;
  let component: ChatComponent;
  let http: HttpTestingController;
  let query: BehaviorSubject<ReturnType<typeof convertToParamMap>>;
  const auth = {
    logout: jasmine.createSpy('logout'),
    hasRole: () => false,
    user: () => ({ id: 1, name: 'Ada Lovelace', email: 'ada@example.com', roles: ['User'] }),
  };
  const hub = { assistantReplied$: new Subject(), assistantFailed$: new Subject(), reconnected$: new Subject() };

  const el = <T extends Element>(selector: string) => (fixture.nativeElement as HTMLElement).querySelector<T>(selector);
  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';

  async function setup(sessions: ReturnType<typeof session>[] = [session('s1', 'New chat')]): Promise<void> {
    query = new BehaviorSubject(convertToParamMap({}));
    auth.logout.calls.reset();
    await TestBed.configureTestingModule({
      imports: [ChatComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { queryParamMap: query } },
        { provide: DirectHubService, useValue: hub },
        { provide: AuthService, useValue: auth },
      ],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ChatComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    http.expectOne(`${API}/sessions`).flush(sessions);
  }

  async function open(id: string, messages: MessageResponse[] = []): Promise<void> {
    query.next(convertToParamMap({ session: id }));
    fixture.detectChanges();
    http.expectOne(`${API}/sessions/${id}/messages`).flush(messages);
    http.expectOne(`${API}/sessions/${id}/jobs`).flush([]);
    fixture.detectChanges();
  }

  async function send(content: string): Promise<void> {
    component.draft.set(content);
    await component.send();
  }

  afterEach(() => http.verify());

  describe('sending a message', () => {
    beforeEach(async () => {
      await setup();
      await open('s1');
    });

    it('shows the question straight away, marked as sending', async () => {
      await send('hello');
      fixture.detectChanges();

      expect(component.sending()).toBeTrue();
      expect(component.messages().map((m) => [m.content, !!m.pending])).toEqual([['hello', true]]);
      http.expectOne(`${API}/sessions/s1/messages`).flush({ userMessage: userMsg(1, 'hello'), assistantMessage: botMsg(2, 'hi') });
      http.expectOne(`${API}/sessions`).flush([session('s1', 'hello')]);
    });

    it('on success replaces the temporary question and shows the reply, with the composer ready again', async () => {
      await send('hello');
      const req = http.expectOne(`${API}/sessions/s1/messages`);
      expect(req.request.body).toEqual({ content: 'hello' });
      req.flush({ userMessage: userMsg(1, 'hello'), assistantMessage: botMsg(2, 'hi there') });
      http.expectOne(`${API}/sessions`).flush([session('s1', 'hello')]);
      fixture.detectChanges();

      expect(component.sending()).toBeFalse();
      expect(component.sendError()).toBeNull();
      expect(component.draft()).toBe('');
      expect(component.messages().map((m) => m.id)).toEqual([1, 2]);
      expect(component.messages().some((m) => m.pending || m.failed)).toBeFalse();
      expect(text()).toContain('hi there');
    });

    it('trims the question before sending it', async () => {
      await send('   padded   ');

      const req = http.expectOne(`${API}/sessions/s1/messages`);
      expect(req.request.body).toEqual({ content: 'padded' });
      req.flush({ userMessage: userMsg(1, 'padded'), assistantMessage: botMsg(2, 'ok') });
      http.expectOne(`${API}/sessions`).flush([session('s1', 'padded')]);
    });

    it('a 503 keeps the question, marks it failed, shows the server message and offers "Send again"', async () => {
      await send('are you there');
      http.expectOne(`${API}/sessions/s1/messages`).flush(
        { error: { code: 'ai_service_unavailable', message: 'The AI service is temporarily unavailable. Your message was saved - try sending again shortly.' } },
        { status: 503, statusText: 'Service Unavailable' },
      );
      http.expectOne(`${API}/sessions`).flush([session('s1', 'are you there')]);
      fixture.detectChanges();

      expect(component.sending()).toBeFalse();
      expect(component.messages().map((m) => [m.content, !!m.failed])).toEqual([['are you there', true]]);
      expect(component.sendError()).toContain('temporarily unavailable');
      expect(el('.composer-error')?.textContent).toContain('temporarily unavailable');
      expect(text()).toContain('Send again');
    });

    it('over the chat quota (429): keeps the question as failed, shows the wait, and offers "Send again"', async () => {
      await send('one question too many');
      http.expectOne(`${API}/sessions/s1/messages`).flush(
        { error: { code: 'rate_limited', message: 'Too many requests. Try again in 30 seconds.' } },
        { status: 429, statusText: 'Too Many Requests' },
      );
      http.expectOne(`${API}/sessions`).flush([session('s1', 'one question too many')]);
      fixture.detectChanges();

      expect(component.messages().map((m) => [m.content, !!m.failed])).toEqual([['one question too many', true]]);
      expect(el('.composer-error')?.textContent).toContain('Try again in 30 seconds');
      expect(text()).toContain('Send again');
      expect(component.sending()).toBeFalse();
    });

    it('"Send again" sends the same text once more, without a duplicate of the failed copy', async () => {
      await send('try me');
      http.expectOne(`${API}/sessions/s1/messages`).flush({ error: { code: 'x', message: 'down' } }, { status: 503, statusText: 'x' });
      http.expectOne(`${API}/sessions`).flush([session('s1', 'try me')]);

      component.retry();
      const again = http.expectOne(`${API}/sessions/s1/messages`);
      expect(again.request.body).toEqual({ content: 'try me' });
      expect(component.sendError()).toBeNull();
      expect(component.messages().filter((m) => m.content === 'try me').length).toBe(1);

      again.flush({ userMessage: userMsg(5, 'try me'), assistantMessage: botMsg(6, 'finally') });
      fixture.detectChanges();

      expect(component.messages().map((m) => m.id)).toEqual([5, 6]);
      expect(text()).not.toContain('Send again');
    });

    it('a second failure after "Send again" is shown the same way', async () => {
      await send('again and again');
      http.expectOne(`${API}/sessions/s1/messages`).flush({}, { status: 503, statusText: 'x' });
      http.expectOne(`${API}/sessions`).flush([session('s1', 'again and again')]);

      component.retry();
      http.expectOne(`${API}/sessions/s1/messages`).flush({ error: { code: 'x', message: 'still down' } }, { status: 503, statusText: 'x' });

      expect(component.sendError()).toBe('still down');
      expect(component.messages().filter((m) => m.failed).length).toBe(1);
    });

    it('does nothing on retry when there is nothing to retry', () => {
      component.retry();

      http.expectNone(() => true);
    });

    it('explains an unreachable server when the request never got an answer', async () => {
      await send('hello');
      http.expectOne(`${API}/sessions/s1/messages`).error(new ProgressEvent('error'));
      http.expectOne(`${API}/sessions`).flush([session('s1', 'hello')]);

      expect(component.sendError()).toContain('Could not reach the server');
      expect(component.messages()[0].failed).toBeTrue();
    });

    it('ignores a second send while the first is still going', async () => {
      await send('first');
      await send('second');

      http.expectOne(`${API}/sessions/s1/messages`).flush({ userMessage: userMsg(1, 'first'), assistantMessage: botMsg(2, 'ok') });
      http.expectOne(`${API}/sessions`).flush([session('s1', 'first')]);
      http.expectNone(`${API}/sessions/s1/messages`);
    });
  });

  describe('the composer keys', () => {
    beforeEach(async () => {
      await setup();
      await open('s1');
    });

    it('Enter sends', () => {
      component.draft.set('via enter');
      const event = new KeyboardEvent('keydown', { key: 'Enter' });
      spyOn(event, 'preventDefault');

      component.onDraftKeydown(event);

      expect(event.preventDefault).toHaveBeenCalled();
      http.expectOne(`${API}/sessions/s1/messages`).flush({ userMessage: userMsg(1, 'via enter'), assistantMessage: botMsg(2, 'ok') });
      http.expectOne(`${API}/sessions`).flush([session('s1', 'via enter')]);
    });

    it('Shift+Enter is a new line and sends nothing', () => {
      component.draft.set('line one');
      const event = new KeyboardEvent('keydown', { key: 'Enter', shiftKey: true });
      spyOn(event, 'preventDefault');

      component.onDraftKeydown(event);

      expect(event.preventDefault).not.toHaveBeenCalled();
      http.expectNone(() => true);
    });

    it('other keys do nothing', () => {
      component.draft.set('typing');
      component.onDraftKeydown(new KeyboardEvent('keydown', { key: 'a' }));

      http.expectNone(() => true);
    });
  });

  describe('chat titles', () => {
    it('refetches the chat list after the first message of an untitled chat, so the sidebar shows the new title', async () => {
      await setup([session('s1', 'New chat')]);
      await open('s1');
      expect(text()).toContain('New chat');

      await send('Explain DNS');
      http.expectOne(`${API}/sessions/s1/messages`).flush({ userMessage: userMsg(1, 'Explain DNS'), assistantMessage: botMsg(2, 'ok') });
      http.expectOne(`${API}/sessions`).flush([session('s1', 'Explain DNS')]);
      fixture.detectChanges();

      expect(component.sessions()[0].title).toBe('Explain DNS');
      expect(el('.sidebar')?.textContent).toContain('Explain DNS');
    });

    it('also refreshes the title when the reply failed (the server titles the chat when it saves the question)', async () => {
      await setup([session('s1', 'New chat')]);
      await open('s1');

      await send('Explain DNS');
      http.expectOne(`${API}/sessions/s1/messages`).flush({}, { status: 503, statusText: 'x' });
      http.expectOne(`${API}/sessions`).flush([session('s1', 'Explain DNS')]);

      expect(component.sessions()[0].title).toBe('Explain DNS');
    });

    it('does not refetch the list when the chat already has a title', async () => {
      await setup([session('s1', 'My own title')]);
      await open('s1');

      await send('another question');
      http.expectOne(`${API}/sessions/s1/messages`).flush({ userMessage: userMsg(1, 'another question'), assistantMessage: botMsg(2, 'ok') });

      http.expectNone(`${API}/sessions`);
      expect(component.sessions()[0].title).toBe('My own title');
    });

    it('keeps the old title if the refetch fails (cosmetic only)', async () => {
      await setup([session('s1', 'New chat')]);
      await open('s1');

      await send('hello');
      http.expectOne(`${API}/sessions/s1/messages`).flush({ userMessage: userMsg(1, 'hello'), assistantMessage: botMsg(2, 'ok') });
      http.expectOne(`${API}/sessions`).flush({}, { status: 500, statusText: 'x' });

      expect(component.sessions()[0].title).toBe('New chat');
      expect(component.sendError()).toBeNull();
    });
  });

  describe('loading and errors', () => {
    it('shows the chat list returned by the server', async () => {
      await setup([session('s1', 'Alpha'), session('s2', 'Beta')]);
      fixture.detectChanges();

      expect(component.sessionsLoading()).toBeFalse();
      expect(el('.sidebar')?.textContent).toContain('Alpha');
      expect(el('.sidebar')?.textContent).toContain('Beta');
    });

    it('shows an error when the chat list cannot be loaded', async () => {
      query = new BehaviorSubject(convertToParamMap({}));
      await TestBed.configureTestingModule({
        imports: [ChatComponent],
        providers: [
          provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
          { provide: ActivatedRoute, useValue: { queryParamMap: query } },
          { provide: DirectHubService, useValue: hub },
        ],
      }).compileComponents();
      http = TestBed.inject(HttpTestingController);
      fixture = TestBed.createComponent(ChatComponent);
      component = fixture.componentInstance;
      fixture.detectChanges();

      http.expectOne(`${API}/sessions`).flush({ error: { code: 'internal_error', message: 'An unexpected error occurred.' } }, { status: 500, statusText: 'x' });
      fixture.detectChanges();

      expect(component.sessionsLoading()).toBeFalse();
      expect(el('.form-error')?.textContent).toContain('An unexpected error occurred.');
    });

    it('opening a chat loads its messages and shows them in order', async () => {
      await setup();
      await open('s1', [userMsg(1, 'first question'), botMsg(2, 'first answer')]);

      expect(component.messages().map((m) => m.content)).toEqual(['first question', 'first answer']);
      expect(text()).toContain('first answer');
    });

    it('an unreadable chat shows the error and no messages', async () => {
      await setup();
      query.next(convertToParamMap({ session: 's1' }));
      fixture.detectChanges();
      http.expectOne(`${API}/sessions/s1/messages`).flush({ error: { code: 'session_not_found', message: 'Chat not found.' } }, { status: 404, statusText: 'x' });
      fixture.detectChanges();

      expect(component.messages()).toEqual([]);
      expect(component.sessionsError()).toBe('Chat not found.');
    });

    it('closing the selection clears the messages', async () => {
      await setup();
      await open('s1', [userMsg(1, 'q')]);

      query.next(convertToParamMap({}));
      fixture.detectChanges();

      expect(component.messages()).toEqual([]);
    });
  });

  describe('sidebar actions', () => {
    beforeEach(async () => setup([session('s1', 'Alpha')]));

    it('"New chat" creates a chat, puts it on top and selects it without loading its (empty) messages', async () => {
      const navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);

      component.newChat();
      http.expectOne({ method: 'POST', url: `${API}/sessions` }).flush(session('s9', 'New chat'));

      expect(component.sessions().map((s) => s.id)).toEqual(['s9', 's1']);
      expect(component.messages()).toEqual([]);
      expect(navigate).toHaveBeenCalledWith([], jasmine.objectContaining({ queryParams: { session: 's9' } }));
    });

    it('shows the server\'s message when a new chat cannot be created', () => {
      component.newChat();
      http.expectOne({ method: 'POST', url: `${API}/sessions` }).flush({ error: { code: 'x', message: 'Could not create the chat.' } }, { status: 500, statusText: 'x' });

      expect(component.sessionsError()).toBe('Could not create the chat.');
      expect(component.sessions().length).toBe(1);
    });

    it('sending with no chat open creates one first, then sends into it', async () => {
      spyOn(TestBed.inject(Router), 'navigate').and.callFake(async () => {
        query.next(convertToParamMap({ session: 's9' }));
        return true;
      });
      component.draft.set('first ever question');

      const sending = component.send();
      http.expectOne({ method: 'POST', url: `${API}/sessions` }).flush(session('s9', 'New chat'));
      await sending;

      const req = http.expectOne(`${API}/sessions/s9/messages`);
      expect(req.request.body).toEqual({ content: 'first ever question' });
      req.flush({ userMessage: userMsg(1, 'first ever question'), assistantMessage: botMsg(2, 'welcome') });
      http.expectOne(`${API}/sessions`).flush([session('s9', 'first ever question'), session('s1', 'Alpha')]);

      expect(component.messages().map((m) => m.id)).toEqual([1, 2]);
    });

    it('if the chat cannot be created, nothing is sent and the draft is kept', async () => {
      component.draft.set('keep me');

      const sending = component.send();
      http.expectOne({ method: 'POST', url: `${API}/sessions` }).flush({ error: { code: 'x', message: 'nope' } }, { status: 500, statusText: 'x' });
      await sending;

      expect(component.sendError()).toBe('nope');
      expect(component.draft()).toBe('keep me');
      http.expectNone(`${API}/sessions/s9/messages`);
    });

    it('Log out clears the session and goes to the login page', () => {
      const navigateByUrl = spyOn(TestBed.inject(Router), 'navigateByUrl').and.resolveTo(true);

      component.logout();

      expect(auth.logout).toHaveBeenCalled();
      expect(navigateByUrl).toHaveBeenCalledWith('/login');
    });
  });
});
