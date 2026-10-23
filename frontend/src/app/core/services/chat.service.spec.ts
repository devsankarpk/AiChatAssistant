import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { environment } from '../../../environments/environment';
import { isAccepted } from '../models/chat.models';
import { ChatService } from './chat.service';

const API = `${environment.apiBaseUrl}/chat`;

describe('ChatService', () => {
  let service: ChatService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(ChatService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('sends a message with POST to the chat\'s messages URL', () => {
    service.sendMessage('s1', 'hello').subscribe();

    const req = http.expectOne(`${API}/sessions/s1/messages`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ content: 'hello' });
    req.flush({});
  });

  it('can tell a synchronous answer (both messages) from an accepted one (a job id)', () => {
    let sync: unknown;
    let accepted: unknown;
    service.sendMessage('s1', 'a').subscribe((r) => (sync = r));
    http.expectOne(`${API}/sessions/s1/messages`).flush({ userMessage: { id: 1 }, assistantMessage: { id: 2 } });
    service.sendMessage('s1', 'b').subscribe((r) => (accepted = r));
    http.expectOne(`${API}/sessions/s1/messages`).flush({ userMessage: { id: 3 }, replyStatus: 'pending', jobId: 'j' }, { status: 202, statusText: 'Accepted' });

    expect(isAccepted(sync as never)).toBeFalse();
    expect(isAccepted(accepted as never)).toBeTrue();
  });

  it('lists the open reply jobs of a chat with GET .../jobs', () => {
    let jobs: unknown;
    service.getOpenReplyJobs('s1').subscribe((j) => (jobs = j));

    const req = http.expectOne(`${API}/sessions/s1/jobs`);
    expect(req.request.method).toBe('GET');
    req.flush([{ jobId: 'j', userMessageId: 1, status: 'failed' }]);
    expect(jobs).toEqual([{ jobId: 'j', userMessageId: 1, status: 'failed' }]);
  });

  it('retries a failed reply with POST .../jobs/{id}/retry and an empty body', () => {
    service.retryReply('job-7').subscribe();

    const req = http.expectOne(`${API}/jobs/job-7/retry`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({});
    req.flush({ jobId: 'job-7', replyStatus: 'pending' }, { status: 202, statusText: 'Accepted' });
  });

  it('still wraps the other endpoints (sessions list, create, messages) on the same base URL', () => {
    service.getSessions().subscribe();
    http.expectOne(`${API}/sessions`).flush([]);
    service.createSession('t').subscribe();
    const create = http.expectOne(`${API}/sessions`);
    expect(create.request.method).toBe('POST');
    expect(create.request.body).toEqual({ title: 't' });
    create.flush({});
    service.getMessages('s1').subscribe();
    http.expectOne(`${API}/sessions/s1/messages`).flush([]);
  });
});
