import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { environment } from '../../../environments/environment';
import { authInterceptor } from '../../auth/auth.interceptor';
import { errorInterceptor } from '../error.interceptor';
import { AuthResponse } from '../models/auth.models';
import { AuthService } from './auth.service';

const API = environment.apiBaseUrl;
const user = { id: 1, name: 'Ada', email: 'ada@example.com', roles: ['User'] };

function jwt(secondsFromNow: number): string {
  const payload = btoa(JSON.stringify({ sub: '1', email: 'ada@example.com', role: 'User', exp: Math.floor(Date.now() / 1000) + secondsFromNow }));
  return `${btoa('{"alg":"none"}')}.${payload}.sig`;
}

const pair = (access: string, refresh: string): AuthResponse => ({ token: access, refreshToken: refresh, expiresAt: '', refreshExpiresAt: '', user });
const unauthorized = { error: { code: 'unauthorized', message: 'Authentication is required.' } };

function seed(access: string, refresh?: string): void {
  localStorage.clear();
  localStorage.setItem('aichat.auth.token', access);
  localStorage.setItem('aichat.auth.user', JSON.stringify(user));
  if (refresh) {
    localStorage.setItem('aichat.auth.refresh', refresh);
  }
}

/** Phase 23: short-lived access tokens renewed by a rotating refresh token, once, without the person noticing. */
describe('session renewal', () => {
  let auth: AuthService;
  let http: HttpClient;
  let mock: HttpTestingController;

  function setup(access: string, refresh?: string): void {
    seed(access, refresh);
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(withInterceptors([errorInterceptor, authInterceptor])), provideHttpClientTesting()],
    });
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpClient);
    mock = TestBed.inject(HttpTestingController);
  }

  afterEach(() => {
    mock.verify();
    localStorage.clear();
  });

  describe('AuthService', () => {
    it('stays signed in with an expired access token while it has a refresh token', () => {
      setup(jwt(-60), 'r1');

      expect(auth.isAuthenticated()).toBeTrue();
      expect(auth.hasRefreshToken()).toBeTrue();
    });

    it('is signed out with an expired access token and no refresh token (and clears the leftovers)', () => {
      setup(jwt(-60));

      expect(auth.isAuthenticated()).toBeFalse();
      expect(localStorage.getItem('aichat.auth.token')).toBeNull();
    });

    it('stores the refresh token from a login', () => {
      setup(jwt(900));

      auth.login({ email: 'ada@example.com', password: 'x' }).subscribe();
      mock.expectOne(`${API}/auth/login`).flush(pair(jwt(900), 'r-login'));

      expect(localStorage.getItem('aichat.auth.refresh')).toBe('r-login');
    });

    it('refresh() sends the stored token and replaces both tokens with the new pair', () => {
      setup(jwt(-60), 'r1');
      const fresh = jwt(900);

      auth.refresh().subscribe();
      const req = mock.expectOne(`${API}/auth/refresh`);
      expect(req.request.body).toEqual({ refreshToken: 'r1' });
      req.flush(pair(fresh, 'r2'));

      expect(auth.getToken()).toBe(fresh);
      expect(localStorage.getItem('aichat.auth.refresh')).toBe('r2');
    });

    it('shares one request between callers that renew at the same moment', () => {
      setup(jwt(-60), 'r1');

      auth.refresh().subscribe();
      auth.refresh().subscribe();
      auth.refresh().subscribe();

      mock.expectOne(`${API}/auth/refresh`).flush(pair(jwt(900), 'r2'));
    });

    it('signs the person out when the refresh token is refused', () => {
      setup(jwt(-60), 'r1');
      let failed = false;

      auth.refresh().subscribe({ error: () => (failed = true) });
      mock.expectOne(`${API}/auth/refresh`).flush({ error: { code: 'invalid_refresh_token', message: 'x' } }, { status: 401, statusText: 'Unauthorized' });

      expect(failed).toBeTrue();
      expect(auth.isAuthenticated()).toBeFalse();
      expect(localStorage.getItem('aichat.auth.refresh')).toBeNull();
    });

    it('adopts the session another tab just renewed instead of signing out when its own refresh loses the race', () => {
      setup(jwt(-60), 'r1');
      const otherTabsAccess = jwt(900);
      let result: AuthResponse | undefined;

      auth.refresh().subscribe((r) => (result = r));
      // Meanwhile the other tab exchanged r1 and stored its new pair.
      localStorage.setItem('aichat.auth.token', otherTabsAccess);
      localStorage.setItem('aichat.auth.refresh', 'r2-from-other-tab');
      mock.expectOne(`${API}/auth/refresh`).flush({ error: { code: 'invalid_refresh_token', message: 'x' } }, { status: 401, statusText: 'Unauthorized' });

      expect(result?.token).toBe(otherTabsAccess);
      expect(auth.getToken()).toBe(otherTabsAccess);
      expect(auth.isAuthenticated()).toBeTrue();
    });

    it('freshToken() returns the current token without a request while it has time left', async () => {
      setup(jwt(900), 'r1');

      expect(await auth.freshToken()).toBe(auth.getToken()!);
    });

    it('freshToken() renews a token that is about to lapse', async () => {
      setup(jwt(10), 'r1');
      const fresh = jwt(900);

      const pending = auth.freshToken();
      mock.expectOne(`${API}/auth/refresh`).flush(pair(fresh, 'r2'));

      expect(await pending).toBe(fresh);
    });

    it('freshToken() gives an empty string when there is no session', async () => {
      setup(jwt(-60));

      expect(await auth.freshToken()).toBe('');
    });

    it('logout() clears the session at once and tells the server to revoke the refresh token', () => {
      setup(jwt(900), 'r1');

      auth.logout();

      expect(auth.isAuthenticated()).toBeFalse();
      expect(localStorage.getItem('aichat.auth.refresh')).toBeNull();
      const req = mock.expectOne(`${API}/auth/logout`);
      expect(req.request.body).toEqual({ refreshToken: 'r1' });
      req.flush(null, { status: 204, statusText: 'No Content' });
    });

    it('logout() still works when the server cannot be reached', () => {
      setup(jwt(900), 'r1');

      auth.logout();
      mock.expectOne(`${API}/auth/logout`).error(new ProgressEvent('error'));

      expect(auth.isAuthenticated()).toBeFalse();
    });

    it('logout() makes no request when there is no refresh token', () => {
      setup(jwt(900));

      auth.logout();

      mock.expectNone(`${API}/auth/logout`);
    });

    it('logoutAll() calls the server and then clears the session', () => {
      setup(jwt(900), 'r1');

      auth.logoutAll().subscribe();
      mock.expectOne(`${API}/auth/logout-all`).flush(null, { status: 204, statusText: 'No Content' });

      expect(auth.isAuthenticated()).toBeFalse();
    });
  });

  describe('interceptors', () => {
    it('renews once on a 401 and repeats the request with the new token', () => {
      setup(jwt(-60), 'r1');
      const fresh = jwt(900);
      let body: unknown;

      http.get(`${API}/chat/sessions`).subscribe((b) => (body = b));
      mock.expectOne(`${API}/chat/sessions`).flush(unauthorized, { status: 401, statusText: 'Unauthorized' });
      mock.expectOne(`${API}/auth/refresh`).flush(pair(fresh, 'r2'));
      const retry = mock.expectOne(`${API}/chat/sessions`);
      expect(retry.request.headers.get('Authorization')).toBe(`Bearer ${fresh}`);
      retry.flush([{ id: 1 }]);

      expect(body).toEqual([{ id: 1 }]);
    });

    it('renews only once for several requests that fail together', () => {
      setup(jwt(-60), 'r1');
      const fresh = jwt(900);

      http.get(`${API}/chat/sessions`).subscribe();
      http.get(`${API}/account`).subscribe();
      mock.expectOne(`${API}/chat/sessions`).flush(unauthorized, { status: 401, statusText: 'Unauthorized' });
      mock.expectOne(`${API}/account`).flush(unauthorized, { status: 401, statusText: 'Unauthorized' });
      mock.expectOne(`${API}/auth/refresh`).flush(pair(fresh, 'r2'));

      mock.expectOne(`${API}/chat/sessions`).flush([]);
      mock.expectOne(`${API}/account`).flush({});
    });

    it('signs out and reports the original 401 when the renewal fails', () => {
      setup(jwt(-60), 'r1');
      let status = 0;

      http.get(`${API}/chat/sessions`).subscribe({ error: (e) => (status = e.status) });
      mock.expectOne(`${API}/chat/sessions`).flush(unauthorized, { status: 401, statusText: 'Unauthorized' });
      mock.expectOne(`${API}/auth/refresh`).flush({ error: { code: 'invalid_refresh_token', message: 'x' } }, { status: 401, statusText: 'Unauthorized' });

      expect(status).toBe(401);
      expect(auth.isAuthenticated()).toBeFalse();
    });

    it('does not loop: a request that is still refused after a successful renewal is reported, not retried again', () => {
      setup(jwt(-60), 'r1');
      let status = 0;

      http.get(`${API}/admin/usage`).subscribe({ error: (e) => (status = e.status) });
      mock.expectOne(`${API}/admin/usage`).flush(unauthorized, { status: 401, statusText: 'Unauthorized' });
      mock.expectOne(`${API}/auth/refresh`).flush(pair(jwt(900), 'r2'));
      mock.expectOne(`${API}/admin/usage`).flush(unauthorized, { status: 401, statusText: 'Unauthorized' });
      // the second refusal ends the session via the error interceptor; no second refresh is attempted
      mock.expectNone(`${API}/auth/refresh`);
      mock.match(`${API}/auth/logout`).forEach((r) => r.flush(null, { status: 204, statusText: 'No Content' }));

      expect(status).toBe(401);
    });

    it('never renews for an /auth/ request (a failed login must not trigger a refresh)', () => {
      setup(jwt(900), 'r1');

      http.post(`${API}/auth/login`, {}).subscribe({ error: () => undefined });
      mock.expectOne(`${API}/auth/login`).flush(unauthorized, { status: 401, statusText: 'Unauthorized' });

      mock.expectNone(`${API}/auth/refresh`);
      mock.match(`${API}/auth/logout`).forEach((r) => r.flush(null, { status: 204, statusText: 'No Content' }));
    });

    it('does not try to renew without a refresh token', () => {
      setup(jwt(900));

      http.get(`${API}/chat/sessions`).subscribe({ error: () => undefined });
      mock.expectOne(`${API}/chat/sessions`).flush(unauthorized, { status: 401, statusText: 'Unauthorized' });

      mock.expectNone(`${API}/auth/refresh`);
    });

    it('leaves other errors (403, 404, 503) alone', () => {
      setup(jwt(900), 'r1');

      for (const status of [403, 404, 503]) {
        http.get(`${API}/chat/sessions`).subscribe({ error: () => undefined });
        mock.expectOne(`${API}/chat/sessions`).flush({ error: { code: 'x', message: 'x' } }, { status, statusText: 'x' });
      }

      mock.expectNone(`${API}/auth/refresh`);
    });

    it('never sends the token to another host', () => {
      setup(jwt(900), 'r1');

      http.get('https://example.org/data').subscribe();

      expect(mock.expectOne('https://example.org/data').request.headers.has('Authorization')).toBeFalse();
    });
  });
});
