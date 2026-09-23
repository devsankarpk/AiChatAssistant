import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { environment } from '../../../environments/environment';
import { AuthService } from './auth.service';

const TOKEN_KEY = 'aichat.auth.token';

/** Builds a syntactically valid, unsigned JWT carrying the given claims - decodeJwtPayload()
 * never checks the signature (that's the server's job on every real request), so this is enough
 * to exercise AuthService's client-side expiry/role logic. */
function fakeJwt(claims: Record<string, unknown>): string {
  const header = btoa(JSON.stringify({ alg: 'none', typ: 'JWT' }));
  const payload = btoa(JSON.stringify(claims));
  return `${header}.${payload}.signature`;
}

function validToken(role: string | string[] = 'User'): string {
  return fakeJwt({ sub: '1', email: 'a@example.com', role, exp: Math.floor(Date.now() / 1000) + 3600 });
}

const testUser = { id: 1, name: 'A', email: 'a@example.com', roles: ['User'] };

describe('AuthService', () => {
  let service: AuthService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
  });

  afterEach(() => {
    httpMock?.verify();
    localStorage.clear();
  });

  function create(): void {
    service = TestBed.inject(AuthService);
    httpMock = TestBed.inject(HttpTestingController);
  }

  it('starts logged out when nothing is stored', () => {
    create();
    expect(service.isAuthenticated()).toBeFalse();
    expect(service.user()).toBeNull();
    expect(service.getToken()).toBeNull();
  });

  it('register() stores the token and user, and becomes authenticated', () => {
    create();
    const token = validToken();

    service.register({ name: 'A', email: 'a@example.com', password: 'Passw0rd!' }).subscribe();

    const req = httpMock.expectOne(`${environment.apiBaseUrl}/auth/register`);
    expect(req.request.method).toBe('POST');
    req.flush({ token, expiresAt: new Date().toISOString(), user: testUser });

    expect(service.isAuthenticated()).toBeTrue();
    expect(service.user()).toEqual(testUser);
    expect(service.getToken()).toBe(token);
    expect(localStorage.getItem(TOKEN_KEY)).toBe(token);
  });

  it('login() stores the session the same way as register()', () => {
    create();
    const token = validToken('Admin');
    const adminUser = { id: 2, name: 'B', email: 'b@example.com', roles: ['Admin'] };

    service.login({ email: 'b@example.com', password: 'Passw0rd!' }).subscribe();
    httpMock.expectOne(`${environment.apiBaseUrl}/auth/login`).flush({
      token,
      expiresAt: new Date().toISOString(),
      user: adminUser,
    });

    expect(service.isAuthenticated()).toBeTrue();
    expect(service.hasRole('Admin')).toBeTrue();
    expect(service.hasRole('User')).toBeFalse();
  });

  it('logout() clears the session from both signals and localStorage', () => {
    create();
    service.login({ email: 'a@example.com', password: 'x' }).subscribe();
    httpMock
      .expectOne(`${environment.apiBaseUrl}/auth/login`)
      .flush({ token: validToken(), expiresAt: new Date().toISOString(), user: testUser });
    expect(service.isAuthenticated()).toBeTrue();

    service.logout();

    expect(service.isAuthenticated()).toBeFalse();
    expect(service.user()).toBeNull();
    expect(service.getToken()).toBeNull();
    expect(localStorage.getItem(TOKEN_KEY)).toBeNull();
  });

  it('does not consider an expired token authenticated, and clears it on construction', () => {
    const expired = fakeJwt({ sub: '1', email: 'a@example.com', role: 'User', exp: Math.floor(Date.now() / 1000) - 3600 });
    localStorage.setItem(TOKEN_KEY, expired);
    localStorage.setItem('aichat.auth.user', JSON.stringify(testUser));

    create();

    expect(service.isAuthenticated()).toBeFalse();
    expect(service.getToken()).toBeNull();
    expect(localStorage.getItem(TOKEN_KEY)).toBeNull();
  });

  it('picks up a still-valid stored session on construction (refresh persists login)', () => {
    localStorage.setItem(TOKEN_KEY, validToken());
    localStorage.setItem('aichat.auth.user', JSON.stringify(testUser));

    create();

    expect(service.isAuthenticated()).toBeTrue();
    expect(service.user()).toEqual(testUser);
  });
});
