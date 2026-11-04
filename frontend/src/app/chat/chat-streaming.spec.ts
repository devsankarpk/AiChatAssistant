import { provideHttpClient } from '@angular/common/http';
import { HttpErrorResponse } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, Observable, Subject } from 'rxjs';

import { environment } from '../../environments/environment';
import { MessageResponse, ReplyStreamEvent } from '../core/models/chat.models';
import { AuthService } from '../core/services/auth.service';
import { ChatService } from '../core/services/chat.service';
import { DirectHubService } from '../core/services/direct-hub.service';
import { ChatComponent } from './chat.component';

const API = `${environment.apiBaseUrl}/chat`;
const msg = (id: number, role: 'user' | 'assistant', content: string): MessageResponse => ({ id, role, content, createdAt: '2026-11-03T10:00:00Z' });

/** Builds the text/event-stream body a server would send, optionally in awkward pieces. */
function sse(...events: [string, unknown][]): string {
  return events.map(([name, data]) => `event: ${name}\ndata: ${JSON.stringify(data)}\n\n`).join('');
}

function responseOf(chunks: string[], init: ResponseInit = { status: 200 }): Response {
  const encoder = new TextEncoder();
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      chunks.forEach((c) => controller.enqueue(encoder.encode(c)));
      controller.close();
    },
  });
  return new Response(body, init);
}

function collect(source: ReturnType<ChatService['streamMessage']>): Promise<{ events: ReplyStreamEvent[]; error?: HttpErrorResponse }> {
  return new Promise((resolve) => {
    const events: ReplyStreamEvent[] = [];
    source.subscribe({
      next: (e) => events.push(e),
      error: (error) => resolve({ events, error }),
      complete: () => resolve({ events }),
    });
  });
}

describe('ChatService streaming', () => {
  let service: ChatService;
  let fetchSpy: jasmine.Spy;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: AuthService, useValue: { freshToken: () => Promise.resolve('tok-123') } }],
    });
    service = TestBed.inject(ChatService);
    fetchSpy = spyOn(window, 'fetch');
  });

  it('posts to the stream endpoint with the bearer token and the question', async () => {
    fetchSpy.and.resolveTo(responseOf([sse(['done', { assistantMessage: msg(2, 'assistant', 'hi') }])]));

    await collect(service.streamMessage('s1', 'Hello'));

    const [url, init] = fetchSpy.calls.mostRecent().args;
    expect(url).toBe(`${API}/sessions/s1/messages/stream`);
    expect(init.method).toBe('POST');
    expect(init.headers.Authorization).toBe('Bearer tok-123');
    expect(JSON.parse(init.body)).toEqual({ content: 'Hello' });
  });

  it('turns the events into user, delta and done steps, in order, and completes', async () => {
    fetchSpy.and.resolveTo(
      responseOf([
        sse(['user', msg(1, 'user', 'Hello')], ['delta', { text: 'Hi' }], ['delta', { text: ' there' }], ['done', { assistantMessage: msg(2, 'assistant', 'Hi there') }]),
      ]),
    );

    const { events, error } = await collect(service.streamMessage('s1', 'Hello'));

    expect(error).toBeUndefined();
    expect(events.map((e) => e.type)).toEqual(['user', 'delta', 'delta', 'done']);
    expect(events[1]).toEqual({ type: 'delta', text: 'Hi' });
    expect((events[3] as { message: MessageResponse }).message.content).toBe('Hi there');
  });

  it('copes with events split across network pieces, even in the middle of a line', async () => {
    const whole = sse(['delta', { text: 'Hel' }], ['delta', { text: 'lo' }], ['done', { assistantMessage: msg(2, 'assistant', 'Hello') }]);
    fetchSpy.and.resolveTo(responseOf([whole.slice(0, 15), whole.slice(15, 60), whole.slice(60)]));

    const { events } = await collect(service.streamMessage('s1', 'q'));

    expect(events.filter((e) => e.type === 'delta').map((e) => (e as { text: string }).text).join('')).toBe('Hello');
    expect(events.at(-1)?.type).toBe('done');
  });

  it('keeps multi-byte characters intact when a piece ends inside one', async () => {
    const bytes = new TextEncoder().encode(sse(['delta', { text: 'héllo 😀' }], ['done', { assistantMessage: msg(2, 'assistant', 'x') }]));
    const cut = bytes.indexOf(0xf0) + 2; // inside the emoji
    const body = new ReadableStream<Uint8Array>({
      start(c) {
        c.enqueue(bytes.slice(0, cut));
        c.enqueue(bytes.slice(cut));
        c.close();
      },
    });
    fetchSpy.and.resolveTo(new Response(body, { status: 200 }));

    const { events } = await collect(service.streamMessage('s1', 'q'));

    expect(events[0]).toEqual({ type: 'delta', text: 'héllo 😀' });
  });

  it('errors with the server body and status when the stream cannot start (503, 404, 400)', async () => {
    for (const status of [503, 404, 400]) {
      fetchSpy.and.resolveTo(new Response(JSON.stringify({ error: { code: 'x', message: `failed ${status}` } }), { status }));

      const { events, error } = await collect(service.streamMessage('s1', 'q'));

      expect(events).toEqual([]);
      expect(error?.status).toBe(status);
      expect(error?.error.error.message).toBe(`failed ${status}`);
    }
  });

  it('errors (status 0) when the network is down, and when the error body is not JSON', async () => {
    fetchSpy.and.rejectWith(new TypeError('Failed to fetch'));
    expect((await collect(service.streamMessage('s1', 'q'))).error?.status).toBe(0);

    fetchSpy.and.resolveTo(new Response('<html>bad gateway</html>', { status: 502 }));
    const { error } = await collect(service.streamMessage('s1', 'q'));
    expect(error?.status).toBe(502);
  });

  it('turns a mid-stream error event into an error after the words already delivered', async () => {
    fetchSpy.and.resolveTo(
      responseOf([sse(['delta', { text: 'par' }], ['error', { error: { code: 'ai_service_unavailable', message: 'The AI broke.' } }])]),
    );

    const { events, error } = await collect(service.streamMessage('s1', 'q'));

    expect(events).toEqual([{ type: 'delta', text: 'par' }]);
    expect(error?.error.error.message).toBe('The AI broke.');
  });

  it('treats a connection that closes without a done as a failure, not a short answer', async () => {
    fetchSpy.and.resolveTo(responseOf([sse(['delta', { text: 'half' }])]));

    const { events, error } = await collect(service.streamMessage('s1', 'q'));

    expect(events.length).toBe(1);
    expect(error).toBeDefined();
  });

  it('closes the connection when the subscriber unsubscribes (Stop)', async () => {
    let signal: AbortSignal | undefined;
    fetchSpy.and.callFake((_url: string, init: RequestInit) => {
      signal = init.signal as AbortSignal;
      return new Promise(() => undefined); // never answers
    });

    const sub = service.streamMessage('s1', 'q').subscribe();
    await new Promise((r) => setTimeout(r));
    sub.unsubscribe();

    expect(signal?.aborted).toBeTrue();
  });

  it('regenerate posts to its own endpoint with no body', async () => {
    fetchSpy.and.resolveTo(responseOf([sse(['done', { assistantMessage: msg(3, 'assistant', 'again') }])]));

    await collect(service.regenerate('s1'));

    const [url, init] = fetchSpy.calls.mostRecent().args;
    expect(url).toBe(`${API}/sessions/s1/regenerate`);
    expect(init.body).toBeUndefined();
  });

  it('ignores lines it does not understand', async () => {
    fetchSpy.and.resolveTo(
      responseOf([': keep-alive\n\nevent: mystery\ndata: {"a":1}\n\n' + sse(['delta', { text: 'ok' }], ['done', { assistantMessage: msg(2, 'assistant', 'ok') }])]),
    );

    const { events } = await collect(service.streamMessage('s1', 'q'));

    expect(events.map((e) => e.type)).toEqual(['delta', 'done']);
  });
});

describe('ChatComponent streaming', () => {
  let fixture: ComponentFixture<ChatComponent>;
  let component: ChatComponent;
  let http: HttpTestingController;
  let stream$: Subject<ReplyStreamEvent>;
  let unsubscribed: boolean;

  const root = () => fixture.nativeElement as HTMLElement;
  const el = <T extends Element>(s: string) => root().querySelector<T>(s);
  const all = <T extends Element>(s: string) => Array.from(root().querySelectorAll<T>(s));
  const button = (label: string) => all<HTMLButtonElement>('button').find((b) => b.textContent?.trim() === label);
  const auth = { logout: () => undefined, hasRole: () => false, user: () => ({ id: 1, name: 'Ada', email: 'a@example.com', roles: ['User'] }) };
  const hub = { assistantReplied$: new Subject(), assistantFailed$: new Subject(), reconnected$: new Subject() };

  function makeStream(): Subject<ReplyStreamEvent> {
    unsubscribed = false;
    return new Subject<ReplyStreamEvent>();
  }

  async function open(messages: MessageResponse[]): Promise<void> {
    const query = new BehaviorSubject(convertToParamMap({ session: 's1' }));
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
    // The real service for everything except the two streaming calls, which are driven by hand through `stream$`.
    const real = TestBed.inject(ChatService);
    const controlled = () =>
      new Observable<ReplyStreamEvent>((subscriber) => {
        stream$ = makeStream();
        const inner = stream$.subscribe(subscriber);
        return () => {
          unsubscribed = true;
          inner.unsubscribe();
        };
      });
    spyOn(real, 'streamMessage').and.callFake(controlled);
    spyOn(real, 'regenerate').and.callFake(controlled);
    fixture = TestBed.createComponent(ChatComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    http.expectOne(`${API}/sessions`).flush([{ id: 's1', title: 'Chat', createdAt: '2026-11-03T09:00:00Z' }]);
    http.expectOne(`${API}/sessions/s1/messages`).flush(messages);
    http.expectOne(`${API}/sessions/s1/jobs`).flush([]);
    fixture.detectChanges();
  }

  function type(text: string): void {
    component.draft.set(text);
    fixture.detectChanges();
  }

  async function send(text: string): Promise<void> {
    type(text);
    await component.send();
    fixture.detectChanges();
  }

  const contents = () => component.messages().map((m) => m.content);

  it('shows Thinking until the first word, then the words as they arrive, then the saved answer', async () => {
    await open([]);
    await send('Hello');
    expect(el('.thinking')).not.toBeNull();
    expect(contents()).toEqual(['Hello']);

    stream$.next({ type: 'user', message: msg(1, 'user', 'Hello') });
    stream$.next({ type: 'delta', text: 'Hi' });
    fixture.detectChanges();
    expect(el('.thinking')).toBeNull();
    expect(contents()).toEqual(['Hello', 'Hi']);
    expect(component.messages()[1].streaming).toBeTrue();
    expect(el('.message.streaming')).not.toBeNull();

    stream$.next({ type: 'delta', text: ' there' });
    fixture.detectChanges();
    expect(contents()).toEqual(['Hello', 'Hi there']);

    stream$.next({ type: 'done', message: msg(2, 'assistant', 'Hi there') });
    stream$.complete();
    fixture.detectChanges();
    expect(component.messages().map((m) => m.id)).toEqual([1, 2]);
    expect(component.messages().some((m) => m.streaming)).toBeFalse();
    expect(component.sending()).toBeFalse();
    expect(el('.message.streaming')).toBeNull();
  });

  it('shows Stop instead of Send while the answer arrives, and Send again afterwards', async () => {
    await open([]);
    await send('Hello');

    expect(button('Stop')).toBeDefined();
    expect(el('button.send')).toBeNull();

    stream$.next({ type: 'done', message: msg(2, 'assistant', 'x') });
    fixture.detectChanges();
    expect(button('Stop')).toBeUndefined();
    expect(el('button.send')).not.toBeNull();
  });

  it('Stop closes the stream and keeps the words already shown', async () => {
    await open([]);
    await send('Hello');
    stream$.next({ type: 'user', message: msg(1, 'user', 'Hello') });
    stream$.next({ type: 'delta', text: 'Once upon' });
    fixture.detectChanges();

    button('Stop')!.click();
    fixture.detectChanges();

    expect(unsubscribed).toBeTrue();
    expect(contents()).toEqual(['Hello', 'Once upon']);
    expect(component.messages()[1].streaming).toBeFalse();
    expect(component.sending()).toBeFalse();
    expect(button('Stop')).toBeUndefined();
    expect(button('Copy')).toBeDefined();
  });

  it('a question that never reached the server is marked failed with Send again when the stream cannot start', async () => {
    await open([]);
    await send('Hello');

    stream$.error(new HttpErrorResponse({ status: 503, error: { error: { code: 'ai_service_unavailable', message: 'The AI is down.' } } }));
    fixture.detectChanges();

    expect(component.messages()[0].failed).toBeTrue();
    expect(el('.composer-error')?.textContent).toContain('The AI is down.');
    expect(component.sending()).toBeFalse();
    expect(button('Send again')).toBeDefined();
  });

  it('an error part-way drops the half answer and reloads what the server really holds', async () => {
    await open([]);
    await send('Hello');
    stream$.next({ type: 'user', message: msg(1, 'user', 'Hello') });
    stream$.next({ type: 'delta', text: 'half an ans' });

    stream$.error(new HttpErrorResponse({ status: 503, error: { error: { code: 'ai_service_unavailable', message: 'Cut off.' } } }));
    http.expectOne(`${API}/sessions/s1/messages`).flush([msg(1, 'user', 'Hello')]);
    http.expectOne(`${API}/sessions/s1/jobs`).flush([]);
    fixture.detectChanges();

    expect(contents()).toEqual(['Hello']);
    expect(el('.composer-error')?.textContent).toContain('Cut off.');
    expect(button('Get a reply')).toBeDefined(); // the saved question has no answer
  });

  it('offers Regenerate under the newest answer only', async () => {
    await open([msg(1, 'user', 'Q1'), msg(2, 'assistant', 'A1'), msg(3, 'user', 'Q2'), msg(4, 'assistant', 'A2')]);
    expect(all('button.regenerate').length).toBe(1);
    expect(all('.message.assistant')[1].querySelector('button.regenerate')).not.toBeNull();
  });

  it('Regenerate keeps the old answer until the first new word, then swaps it for the saved one', async () => {
    await open([msg(1, 'user', 'Q1'), msg(2, 'assistant', 'Old answer')]);

    (el('button.regenerate') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(contents()).toEqual(['Q1', 'Old answer']);

    stream$.next({ type: 'delta', text: 'New' });
    fixture.detectChanges();
    expect(contents()).toEqual(['Q1', 'New']);

    stream$.next({ type: 'done', message: msg(5, 'assistant', 'New answer') });
    fixture.detectChanges();
    expect(component.messages().map((m) => [m.id, m.content])).toEqual([[1, 'Q1'], [5, 'New answer']]);
  });

  it('a regenerate that cannot start leaves the old answer in place', async () => {
    await open([msg(1, 'user', 'Q1'), msg(2, 'assistant', 'Old answer')]);
    (el('button.regenerate') as HTMLButtonElement).click();

    stream$.error(new HttpErrorResponse({ status: 503, error: { error: { code: 'x', message: 'Not now.' } } }));
    fixture.detectChanges();

    expect(contents()).toEqual(['Q1', 'Old answer']);
    expect(el('.composer-error')?.textContent).toContain('Not now.');
  });

  it('a regenerate that breaks part-way restores the old answer from the server', async () => {
    await open([msg(1, 'user', 'Q1'), msg(2, 'assistant', 'Old answer')]);
    (el('button.regenerate') as HTMLButtonElement).click();
    stream$.next({ type: 'delta', text: 'New wor' });

    stream$.error(new HttpErrorResponse({ status: 503, error: { error: { code: 'x', message: 'Cut off.' } } }));
    http.expectOne(`${API}/sessions/s1/messages`).flush([msg(1, 'user', 'Q1'), msg(2, 'assistant', 'Old answer')]);
    http.expectOne(`${API}/sessions/s1/jobs`).flush([]);
    fixture.detectChanges();

    expect(contents()).toEqual(['Q1', 'Old answer']);
  });

  it('does not offer Regenerate while an answer is being written', async () => {
    await open([msg(1, 'user', 'Q1'), msg(2, 'assistant', 'A1')]);
    (el('button.regenerate') as HTMLButtonElement).click();
    stream$.next({ type: 'delta', text: 'x' });
    fixture.detectChanges();

    expect(el('button.regenerate')).toBeNull();
  });

  it('offers "Get a reply" under a question that was never answered, and it answers it', async () => {
    await open([msg(1, 'user', 'Lonely question')]);
    expect(button('Get a reply')).toBeDefined();

    button('Get a reply')!.click();
    stream$.next({ type: 'delta', text: 'Finally' });
    stream$.next({ type: 'done', message: msg(2, 'assistant', 'Finally') });
    fixture.detectChanges();

    expect(contents()).toEqual(['Lonely question', 'Finally']);
    expect(button('Get a reply')).toBeUndefined();
  });

  it('does not offer "Get a reply" for a question that has an answer', async () => {
    await open([msg(1, 'user', 'Q'), msg(2, 'assistant', 'A')]);
    expect(button('Get a reply')).toBeUndefined();
  });

  it('refuses a second send while one is streaming', async () => {
    await open([]);
    await send('first');
    type('second');

    await component.send();

    expect(contents()).toEqual(['first']);
  });
});
