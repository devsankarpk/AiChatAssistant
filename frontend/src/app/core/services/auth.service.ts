import { HttpClient } from '@angular/common/http';
import { Injectable, computed, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';

import { environment } from '../../../environments/environment';
import {
  AuthMessageResponse,
  AuthResponse,
  ForgotPasswordRequest,
  RegistrationPendingResponse,
  ResendVerificationRequest,
  VerifyEmailRequest,
  isRegistrationPending,
  LoginRequest,
  RegisterRequest,
  ResetPasswordRequest,
  UserSummary,
} from '../models/auth.models';
import { decodeJwtPayload, isExpired, rolesFromClaims } from '../utils/jwt';

const TOKEN_KEY = 'aichat.auth.token';
const USER_KEY = 'aichat.auth.user';

/**
 * Owns the JWT and the logged-in user's summary in localStorage (so a page refresh keeps
 * the session), and exposes reactive state via signals. Real authorization always happens
 * server-side; the roles/claims here are for UI conditionals only (see CLAUDE.md).
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly tokenSignal = signal<string | null>(readStoredToken());
  private readonly userSignal = signal<UserSummary | null>(readStoredUser());

  readonly token = this.tokenSignal.asReadonly();
  readonly user = this.userSignal.asReadonly();

  readonly isAuthenticated = computed(() => {
    const token = this.tokenSignal();
    if (!token) {
      return false;
    }
    return !isExpired(decodeJwtPayload(token));
  });

  readonly roles = computed(() => rolesFromClaims(decodeJwtPayload(this.tokenSignal() ?? '')));

  constructor(private readonly http: HttpClient) {
    // A token left over from a previous, now-expired session shouldn't look logged in.
    if (this.tokenSignal() && !this.isAuthenticated()) {
      this.clearSession();
    }
  }

  /** Signs the person in, unless the server requires email confirmation first (then it answers "pending" and no session is stored). */
  register(request: RegisterRequest): Observable<AuthResponse | RegistrationPendingResponse> {
    return this.http.post<AuthResponse | RegistrationPendingResponse>(`${environment.apiBaseUrl}/auth/register`, request).pipe(
      tap((response) => {
        if (!isRegistrationPending(response)) {
          this.storeSession(response);
        }
      }),
    );
  }

  verifyEmail(request: VerifyEmailRequest): Observable<AuthMessageResponse> {
    return this.http.post<AuthMessageResponse>(`${environment.apiBaseUrl}/auth/verify-email`, request);
  }

  /** Always answers the same, whether or not the address needs a new link. */
  resendVerification(request: ResendVerificationRequest): Observable<AuthMessageResponse> {
    return this.http.post<AuthMessageResponse>(`${environment.apiBaseUrl}/auth/resend-verification`, request);
  }

  /** Takes over a session the server just issued (for example the new token returned by a password change). */
  acceptSession(response: AuthResponse): void {
    this.storeSession(response);
  }

  /** Keeps the remembered name in step after the person renames themselves. */
  updateStoredName(name: string): void {
    const user = this.userSignal();
    if (user) {
      const updated = { ...user, name };
      try {
        localStorage.setItem(USER_KEY, JSON.stringify(updated));
      } catch {
        // storage blocked: the name still updates for this page view
      }
      this.userSignal.set(updated);
    }
  }

  login(request: LoginRequest): Observable<AuthResponse> {
    return this.http
      .post<AuthResponse>(`${environment.apiBaseUrl}/auth/login`, request)
      .pipe(tap((response) => this.storeSession(response)));
  }

  logout(): void {
    this.clearSession();
  }

  /** Always resolves the same way whether or not the email is registered - the backend
   * deliberately doesn't reveal that, so there's nothing session-specific to update here. */
  forgotPassword(request: ForgotPasswordRequest): Observable<AuthMessageResponse> {
    return this.http.post<AuthMessageResponse>(`${environment.apiBaseUrl}/auth/forgot-password`, request);
  }

  resetPassword(request: ResetPasswordRequest): Observable<AuthMessageResponse> {
    return this.http.post<AuthMessageResponse>(`${environment.apiBaseUrl}/auth/reset-password`, request);
  }

  hasRole(role: string): boolean {
    return this.roles().includes(role);
  }

  getToken(): string | null {
    return this.tokenSignal();
  }

  private storeSession(response: AuthResponse): void {
    localStorage.setItem(TOKEN_KEY, response.token);
    localStorage.setItem(USER_KEY, JSON.stringify(response.user));
    this.tokenSignal.set(response.token);
    this.userSignal.set(response.user);
  }

  private clearSession(): void {
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(USER_KEY);
    this.tokenSignal.set(null);
    this.userSignal.set(null);
  }
}

function readStoredToken(): string | null {
  try {
    return localStorage.getItem(TOKEN_KEY);
  } catch {
    return null; // private-browsing / storage disabled
  }
}

function readStoredUser(): UserSummary | null {
  try {
    const raw = localStorage.getItem(USER_KEY);
    return raw ? (JSON.parse(raw) as UserSummary) : null;
  } catch {
    return null;
  }
}
