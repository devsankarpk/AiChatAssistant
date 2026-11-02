import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { environment } from '../../../environments/environment';
import { isRegistrationPending } from '../models/auth.models';
import { AuthService } from './auth.service';

const API = `${environment.apiBaseUrl}/auth`;

function fakeJwt(): string {
  const payload = btoa(JSON.stringify({ sub: '1', email: 'a@example.com', role: 'User', exp: Math.floor(Date.now() / 1000) + 3600 }));
  return `${btoa('{"alg":"none"}')}.${payload}.sig`;
}

/** The email-confirmation side of AuthService (Phase 22). */
describe('AuthService email confirmation', () => {
  let service: AuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    localStorage.clear();
  });

  it('a "pending" registration answer does NOT sign the person in or store anything', () => {
    let result: unknown;
    service.register({ name: 'A', email: 'a@example.com', password: 'long-enough-1' }).subscribe((r) => (result = r));

    http.expectOne(`${API}/register`).flush({ message: 'Check your email', verificationRequired: true }, { status: 202, statusText: 'Accepted' });

    expect(isRegistrationPending(result as never)).toBeTrue();
    expect(service.isAuthenticated()).toBeFalse();
    expect(localStorage.getItem('aichat.auth.token')).toBeNull();
  });

  it('a normal registration answer still signs the person in', () => {
    let result: unknown;
    service.register({ name: 'A', email: 'a@example.com', password: 'long-enough-1' }).subscribe((r) => (result = r));

    http.expectOne(`${API}/register`).flush({ token: fakeJwt(), expiresAt: '', user: { id: 1, name: 'A', email: 'a@example.com', roles: ['User'] } }, { status: 201, statusText: 'Created' });

    expect(isRegistrationPending(result as never)).toBeFalse();
    expect(service.isAuthenticated()).toBeTrue();
  });

  it('confirms the email with POST /auth/verify-email and the token', () => {
    service.verifyEmail({ token: 'abc' }).subscribe();

    const req = http.expectOne(`${API}/verify-email`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ token: 'abc' });
    req.flush({ message: 'ok' });
  });

  it('asks for another link with POST /auth/resend-verification', () => {
    service.resendVerification({ email: 'a@example.com' }).subscribe();

    const req = http.expectOne(`${API}/resend-verification`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ email: 'a@example.com' });
    req.flush({ message: 'sent' });
  });

  it('acceptSession stores the token and user a server response carries', () => {
    const token = fakeJwt();

    service.acceptSession({ token, expiresAt: '', user: { id: 7, name: 'Seven', email: 's@example.com', roles: ['User'] } });

    expect(service.getToken()).toBe(token);
    expect(service.user()?.name).toBe('Seven');
    expect(JSON.parse(localStorage.getItem('aichat.auth.user')!).id).toBe(7);
  });

  it('updateStoredName changes only the name, and does nothing when nobody is signed in', () => {
    service.updateStoredName('Nobody'); // no session: must not throw or invent one
    expect(service.user()).toBeNull();

    service.acceptSession({ token: fakeJwt(), expiresAt: '', user: { id: 1, name: 'Old', email: 'a@example.com', roles: ['User'] } });
    service.updateStoredName('New');

    expect(service.user()).toEqual({ id: 1, name: 'New', email: 'a@example.com', roles: ['User'] });
  });

  it('isRegistrationPending tells the two answer shapes apart', () => {
    expect(isRegistrationPending({ message: 'm', verificationRequired: true })).toBeTrue();
    expect(isRegistrationPending({ token: 't', expiresAt: '', user: { id: 1, name: 'A', email: 'a', roles: [] } })).toBeFalse();
  });
});
