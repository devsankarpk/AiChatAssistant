import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { environment } from '../../../environments/environment';
import { AccountService } from './account.service';
import { AuthService } from './auth.service';

const BASE = `${environment.apiBaseUrl}/account`;

function fakeJwt(): string {
  const payload = btoa(JSON.stringify({ sub: '1', email: 'a@example.com', role: 'User', exp: Math.floor(Date.now() / 1000) + 3600 }));
  return `${btoa('{"alg":"none"}')}.${payload}.sig`;
}

describe('AccountService', () => {
  let service: AccountService;
  let auth: AuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    auth = TestBed.inject(AuthService);
    service = TestBed.inject(AccountService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    localStorage.clear();
  });

  it('reads the profile with GET /account', () => {
    service.getProfile().subscribe();

    const req = http.expectOne(BASE);
    expect(req.request.method).toBe('GET');
    req.flush({});
  });

  it('renames with PUT /account/profile and keeps the remembered name in step', () => {
    auth.acceptSession({ token: fakeJwt(), expiresAt: '', user: { id: 1, name: 'Old', email: 'a@example.com', roles: ['User'] } });

    service.updateProfile({ name: 'New Name' }).subscribe();

    const req = http.expectOne(`${BASE}/profile`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ name: 'New Name' });
    req.flush({ id: 1, name: 'New Name', email: 'a@example.com', roles: ['User'], createdAt: '', emailVerified: true });

    expect(auth.user()?.name).toBe('New Name');
    expect(JSON.parse(localStorage.getItem('aichat.auth.user')!).name).toBe('New Name');
  });

  it('a failed rename leaves the remembered name alone', () => {
    auth.acceptSession({ token: fakeJwt(), expiresAt: '', user: { id: 1, name: 'Old', email: 'a@example.com', roles: ['User'] } });

    service.updateProfile({ name: '' }).subscribe({ error: () => undefined });
    http.expectOne(`${BASE}/profile`).flush({ error: { code: 'validation_error', message: 'x' } }, { status: 400, statusText: 'Bad Request' });

    expect(auth.user()?.name).toBe('Old');
  });

  it('changes the password with POST /account/change-password and takes over the new token', () => {
    const fresh = fakeJwt();

    service.changePassword({ currentPassword: 'old-password', newPassword: 'new-password-1' }).subscribe();

    const req = http.expectOne(`${BASE}/change-password`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ currentPassword: 'old-password', newPassword: 'new-password-1' });
    req.flush({ token: fresh, expiresAt: '', user: { id: 1, name: 'A', email: 'a@example.com', roles: ['User'] } });

    expect(auth.getToken()).toBe(fresh);
    expect(auth.isAuthenticated()).toBeTrue();
  });

  it('a wrong current password does not touch the stored session', () => {
    const before = fakeJwt();
    auth.acceptSession({ token: before, expiresAt: '', user: { id: 1, name: 'A', email: 'a@example.com', roles: ['User'] } });

    service.changePassword({ currentPassword: 'wrong', newPassword: 'new-password-1' }).subscribe({ error: () => undefined });
    http.expectOne(`${BASE}/change-password`).flush({ error: { code: 'invalid_current_password', message: 'no' } }, { status: 400, statusText: 'Bad Request' });

    expect(auth.getToken()).toBe(before);
  });

  it('deleting the account POSTs the password and, on success, clears the local session', () => {
    auth.acceptSession({ token: fakeJwt(), expiresAt: '', user: { id: 1, name: 'A', email: 'a@example.com', roles: ['User'] } });

    service.deleteAccount({ password: 'secret-password' }).subscribe();

    const req = http.expectOne(`${BASE}/delete`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ password: 'secret-password' });
    req.flush(null, { status: 204, statusText: 'No Content' });

    expect(auth.isAuthenticated()).toBeFalse();
    expect(localStorage.getItem('aichat.auth.token')).toBeNull();
  });

  it('a refused delete (wrong password) keeps the person signed in', () => {
    auth.acceptSession({ token: fakeJwt(), expiresAt: '', user: { id: 1, name: 'A', email: 'a@example.com', roles: ['User'] } });

    service.deleteAccount({ password: 'wrong' }).subscribe({ error: () => undefined });
    http.expectOne(`${BASE}/delete`).flush({ error: { code: 'invalid_current_password', message: 'no' } }, { status: 400, statusText: 'Bad Request' });

    expect(auth.isAuthenticated()).toBeTrue();
  });
});
