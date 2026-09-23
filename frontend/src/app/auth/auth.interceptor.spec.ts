import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { environment } from '../../environments/environment';
import { authInterceptor } from './auth.interceptor';

/** A syntactically valid, unsigned, non-expired JWT. AuthService's constructor self-clears
 * anything that doesn't parse as one (see auth.service.spec.ts), so a plain placeholder string
 * won't survive being read back out via getToken() - it has to look like a real token. */
function fakeJwt(): string {
  const header = btoa(JSON.stringify({ alg: 'none', typ: 'JWT' }));
  const payload = btoa(JSON.stringify({ sub: '1', email: 'a@example.com', role: 'User', exp: Math.floor(Date.now() / 1000) + 3600 }));
  return `${header}.${payload}.signature`;
}

describe('authInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    localStorage.clear();
  });

  it('attaches Authorization: Bearer <token> to a request aimed at our own API', () => {
    const token = fakeJwt();
    localStorage.setItem('aichat.auth.token', token);

    http.get(`${environment.apiBaseUrl}/chat/sessions`).subscribe();

    const req = httpMock.expectOne(`${environment.apiBaseUrl}/chat/sessions`);
    expect(req.request.headers.get('Authorization')).toBe(`Bearer ${token}`);
    req.flush([]);
  });

  it('adds no Authorization header when there is no token', () => {
    http.get(`${environment.apiBaseUrl}/chat/sessions`).subscribe();

    const req = httpMock.expectOne(`${environment.apiBaseUrl}/chat/sessions`);
    expect(req.request.headers.has('Authorization')).toBeFalse();
    req.flush([]);
  });

  it('never sends the token to a third-party host, even with one stored', () => {
    localStorage.setItem('aichat.auth.token', fakeJwt());

    http.get('https://third-party.example.com/data').subscribe();

    const req = httpMock.expectOne('https://third-party.example.com/data');
    expect(req.request.headers.has('Authorization')).toBeFalse();
    req.flush({});
  });
});
