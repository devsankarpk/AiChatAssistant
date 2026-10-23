import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject, Subject } from 'rxjs';

import { environment } from '../../environments/environment';
import { AssistantFailedEvent, AssistantRepliedEvent, MessageResponse } from '../core/models/chat.models';
import { DirectHubService } from '../core/services/direct-hub.service';
import { ChatComponent } from './chat.component';

const API = `${environment.apiBaseUrl}/chat`;
const question: MessageResponse = { id: 10, role: 'user', content: 'What is DNS?', createdAt: '2026-10-23T10:00:00Z' };
const reply: MessageResponse = { id: 11, role: 'assistant', content: 'It maps names to addresses.', createdAt: '2026-10-23T10:00:05Z' };

/** Async AI replies (Phase 18) in the chat page: pending, replied and failed, with the hub faked and the API answered by hand. */
describe('ChatComponent with async replies', () => {
  let fixture: ComponentFixture<ChatComponent>;
  let component: ChatComponent;
  let http: HttpTestingController;
  let query: BehaviorSubject<ReturnType<typeof convertToParamMap>>;
  const hub = {
    assistantReplied$: new Subject<AssistantRepliedEvent>(),
    assistantFailed$: new Subject<AssistantFailedEvent>(),
    reconnected$: new Subject<void>(),
  };

  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';
  const el = (selector: string) => (fixture.nativeElement as HTMLElement).querySelector(selector);

  /** Opens chat "s1" and answers its three startup requests. */
  async function openChat(messages: MessageResponse[] = [], jobs: unknown[] = []): Promise<void> {
    query.next(convertToParamMap({ session: 's1' }));
    fixture.detectChanges();
    http.expectOne(`${API}/sessions/s1/messages`).flush(messages);
    http.expectOne(`${API}/sessions/s1/jobs`).flush(jobs);
    fixture.detectChanges();
  }

  async function send(content: string): Promise<void> {
    component.draft.set(content);
    await component.send();
  }

  beforeEach(async () => {
    query = new BehaviorSubject(convertToParamMap({}));
    hub.assistantReplied$ = new Subject();
    hub.assistantFailed$ = new Subject();
    hub.reconnected$ = new Subject();
    await TestBed.configureTestingModule({
      imports: [ChatComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { queryParamMap: query } },
        { provide: DirectHubService, useValue: hub },
      ],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ChatComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    http.expectOne(`${API}/sessions`).flush([{ id: 's1', title: 'DNS', createdAt: '2026-10-23T09:00:00Z' }]);
  });

  afterEach(() => http.verify());

  it('a 202 keeps the question on screen and shows "Thinking…" until the reply arrives, with the composer locked', async () => {
    await openChat();
    const sending = send('What is DNS?');
    http.expectOne(`${API}/sessions/s1/messages`).flush({ userMessage: question, replyStatus: 'pending', jobId: 'job-1' }, { status: 202, statusText: 'Accepted' });
    await sending;
    fixture.detectChanges();

    expect(component.messages().map((m) => m.content)).toEqual(['What is DNS?']);
    expect(component.replyPending()).toBeTrue();
    expect(text()).toContain('Thinking');
    expect((el('button.send') as HTMLButtonElement).disabled).toBeTrue();
  });

  it('the AssistantReplied event adds the reply, clears "Thinking…" and unlocks the composer', async () => {
    await openChat();
    const sending = send('What is DNS?');
    http.expectOne(`${API}/sessions/s1/messages`).flush({ userMessage: question, replyStatus: 'pending', jobId: 'job-1' }, { status: 202, statusText: 'Accepted' });
    await sending;

    hub.assistantReplied$.next({ jobId: 'job-1', sessionId: 's1', userMessageId: 10, assistantMessage: reply });
    fixture.detectChanges();

    expect(component.messages().map((m) => m.content)).toEqual(['What is DNS?', 'It maps names to addresses.']);
    expect(component.replyPending()).toBeFalse();
    expect(text()).not.toContain('Thinking');
    component.draft.set('next');
    fixture.detectChanges();
    expect((el('button.send') as HTMLButtonElement).disabled).toBeFalse();
  });

  it('the same reply delivered twice is shown once', async () => {
    await openChat([question, reply]);

    hub.assistantReplied$.next({ jobId: 'job-1', sessionId: 's1', userMessageId: 10, assistantMessage: reply });
    hub.assistantReplied$.next({ jobId: 'job-1', sessionId: 's1', userMessageId: 10, assistantMessage: reply });

    expect(component.messages().filter((m) => m.id === 11).length).toBe(1);
  });

  it('a reply for a chat that is not open is not drawn here (it is loaded when that chat is opened)', async () => {
    await openChat([question]);

    hub.assistantReplied$.next({ jobId: 'job-9', sessionId: 'another-chat', userMessageId: 99, assistantMessage: { ...reply, id: 99 } });

    expect(component.messages().map((m) => m.id)).toEqual([10]);
  });

  it('a reply that finishes while another chat is open still ends the pending state of the chat it belongs to', async () => {
    await openChat([], []);
    const sending = send('What is DNS?');
    http.expectOne(`${API}/sessions/s1/messages`).flush({ userMessage: question, replyStatus: 'pending', jobId: 'job-1' }, { status: 202, statusText: 'Accepted' });
    await sending;

    query.next(convertToParamMap({ session: 's2' }));
    fixture.detectChanges();
    http.expectOne(`${API}/sessions/s2/messages`).flush([]);
    http.expectOne(`${API}/sessions/s2/jobs`).flush([]);
    hub.assistantReplied$.next({ jobId: 'job-1', sessionId: 's1', userMessageId: 10, assistantMessage: reply });

    expect(component.pendingJobs()).toEqual({});
    expect(component.replyPending()).toBeFalse();
  });

  it('the AssistantFailed event shows "The assistant couldn\'t reply." with Send again under the question', async () => {
    await openChat([question], [{ jobId: 'job-1', userMessageId: 10, status: 'pending' }]);
    expect(component.replyPending()).toBeTrue();

    hub.assistantFailed$.next({ jobId: 'job-1', sessionId: 's1', userMessageId: 10, error: { code: 'ai_service_unavailable', message: 'x' } });
    fixture.detectChanges();

    expect(component.replyPending()).toBeFalse();
    expect(text()).toContain("The assistant couldn't reply.");
    expect(el('.reply-failed button')?.textContent).toContain('Send again');
  });

  it('Send again asks the server to retry the same job, with no new question, and goes back to "Thinking…"', async () => {
    await openChat([question], [{ jobId: 'job-1', userMessageId: 10, status: 'failed', error: 'x' }]);
    expect(text()).toContain("The assistant couldn't reply.");

    (el('.reply-failed button') as HTMLButtonElement).click();
    const retry = http.expectOne(`${API}/jobs/job-1/retry`);
    expect(retry.request.method).toBe('POST');
    retry.flush({ jobId: 'job-1', replyStatus: 'pending' }, { status: 202, statusText: 'Accepted' });
    fixture.detectChanges();

    expect(component.replyPending()).toBeTrue();
    expect(text()).not.toContain("The assistant couldn't reply.");
    expect(component.messages().length).toBe(1);
  });

  it('a retry the server refuses (broker down) shows the error and keeps the failed state', async () => {
    await openChat([question], [{ jobId: 'job-1', userMessageId: 10, status: 'failed' }]);

    (el('.reply-failed button') as HTMLButtonElement).click();
    http.expectOne(`${API}/jobs/job-1/retry`).flush(
      { error: { code: 'ai_service_unavailable', message: 'The AI service is temporarily unavailable. Try again shortly.' } },
      { status: 503, statusText: 'Service Unavailable' },
    );
    fixture.detectChanges();

    expect(component.sendError()).toContain('temporarily unavailable');
    expect(text()).toContain("The assistant couldn't reply.");
    expect(component.replyPending()).toBeFalse();
  });

  it('opening a chat after a reload shows a reply that is still being produced', async () => {
    await openChat([question], [{ jobId: 'job-1', userMessageId: 10, status: 'pending' }]);

    expect(component.replyPending()).toBeTrue();
    expect(text()).toContain('Thinking');
  });

  it('a chat with no open jobs shows nothing extra', async () => {
    await openChat([question, reply], []);

    expect(component.replyPending()).toBeFalse();
    expect(component.failedJobs()).toEqual({});
    expect(text()).not.toContain('Thinking');
  });

  it('after a reconnect it refetches messages and jobs, picking up a reply whose push was missed', async () => {
    await openChat([question], [{ jobId: 'job-1', userMessageId: 10, status: 'pending' }]);

    hub.reconnected$.next();
    http.expectOne(`${API}/sessions/s1/messages`).flush([question, reply]);
    http.expectOne(`${API}/sessions/s1/jobs`).flush([]);
    fixture.detectChanges();

    expect(component.messages().map((m) => m.id)).toEqual([10, 11]);
    expect(component.replyPending()).toBeFalse();
  });

  it('while a reply is outstanding it looks for a missed push every 15 seconds, and stops once it is delivered', fakeAsync(() => {
    query.next(convertToParamMap({ session: 's1' }));
    fixture.detectChanges();
    http.expectOne(`${API}/sessions/s1/messages`).flush([question]);
    http.expectOne(`${API}/sessions/s1/jobs`).flush([{ jobId: 'job-1', userMessageId: 10, status: 'pending' }]);
    fixture.detectChanges();
    tick(0);

    tick(15_000);
    http.expectOne(`${API}/sessions/s1/messages`).flush([question]);
    http.expectOne(`${API}/sessions/s1/jobs`).flush([{ jobId: 'job-1', userMessageId: 10, status: 'pending' }]);
    tick(15_000);
    http.expectOne(`${API}/sessions/s1/messages`).flush([question, reply]);
    http.expectOne(`${API}/sessions/s1/jobs`).flush([]);
    fixture.detectChanges();
    tick(0);
    expect(component.replyPending()).toBeFalse();

    tick(60_000); // nothing outstanding: no more requests
    http.expectNone(() => true);
  }));

  it('a synchronous 200 (async mode off) still shows both messages at once and never enters the pending state', async () => {
    await openChat();
    const sending = send('What is DNS?');
    http.expectOne(`${API}/sessions/s1/messages`).flush({ userMessage: question, assistantMessage: reply });
    await sending;

    expect(component.messages().map((m) => m.content)).toEqual(['What is DNS?', 'It maps names to addresses.']);
    expect(component.replyPending()).toBeFalse();
  });

  it('does not send a second question while the first reply is still being produced', async () => {
    await openChat([question], [{ jobId: 'job-1', userMessageId: 10, status: 'pending' }]);

    component.draft.set('and another thing');
    await component.send();

    http.expectNone(`${API}/sessions/s1/messages`);
  });
});
