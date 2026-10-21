import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { environment } from '../../../environments/environment';
import { DirectChatService } from './direct-chat.service';

describe('DirectChatService', () => {
  let service: DirectChatService;
  let http: HttpTestingController;
  const base = `${environment.apiBaseUrl}/direct`;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(DirectChatService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('searches people by name with the term as a query parameter', () => {
    service.searchUsers('ali ce').subscribe();

    const req = http.expectOne((r) => r.url === `${base}/users`);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('search')).toBe('ali ce');
    req.flush([]);
  });

  it('lists conversations', () => {
    service.listConversations().subscribe();

    const req = http.expectOne(`${base}/conversations`);
    expect(req.request.method).toBe('GET');
    req.flush([]);
  });

  it('starts a conversation by posting the user id', () => {
    service.startConversation(7).subscribe();

    const req = http.expectOne(`${base}/conversations`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ userId: 7 });
    req.flush({});
  });

  it('gets messages, adding before and limit only when given', () => {
    service.getMessages('c1').subscribe();
    const plain = http.expectOne((r) => r.url === `${base}/conversations/c1/messages`);
    expect(plain.request.params.keys().length).toBe(0);
    plain.flush([]);

    service.getMessages('c1', 40, 50).subscribe();
    const paged = http.expectOne((r) => r.url === `${base}/conversations/c1/messages`);
    expect(paged.request.params.get('before')).toBe('40');
    expect(paged.request.params.get('limit')).toBe('50');
    paged.flush([]);
  });

  it('sends a message as { content }', () => {
    service.sendMessage('c1', 'hello').subscribe();

    const req = http.expectOne(`${base}/conversations/c1/messages`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ content: 'hello' });
    req.flush({});
  });

  it('marks a conversation read with an empty POST', () => {
    service.markRead('c1').subscribe();

    const req = http.expectOne(`${base}/conversations/c1/read`);
    expect(req.request.method).toBe('POST');
    req.flush({ conversationId: 'c1', readCount: 0, upToMessageId: null });
  });
});
