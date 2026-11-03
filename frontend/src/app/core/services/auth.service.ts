import { HttpClient } from '@angular/common/http';
import { Injectable, computed, signal } from '@angular/core';
import { Observable, catchError, finalize, firstValueFrom, of, shareReplay, tap, throwError } from 'rxjs';

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
const REFRESH_KEY = 'aichat.auth.refresh';

/** An access token with less than this left is renewed before it is used (the hub and long-idle tabs). */
const RENEW_BEFORE_SECONDS = 30;

/**
 * Owns the access token (short-lived JWT), the refresh token and the logged-in user's summary in localStorage (so a page refresh keeps
 * the session), and exposes reactive state via signals. A session is "signed in" while it has a valid access token OR a refresh token
 * (the access token lasts minutes; the refresh token renews it, see `refresh()`). Real authorization always happens
 * server-side; the roles/claims here are for UI conditionals only (see CLAUDE.md).
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly tokenSignal = signal<string | null>(readStoredToken());
  private readonly userSignal = signal<UserSummary | null>(readStoredUser());
  private readonly refreshSignal = signal<string | null>(readStored(REFRESH_KEY));
  /** The renewal in flight, shared by everyone who needs a token at that moment (one request, not one per caller). */
  private inFlightRefresh: Observable<AuthResponse> | null = null;

  readonly token = this.tokenSignal.asReadonly();
  readonly user = this.userSignal.asReadonly();

  readonly isAuthenticated = computed(() => {
    if (this.refreshSignal()) {
      return true; // the access token may have lapsed, but the refresh token can renew it
    }
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
    // Another tab renewed or ended the session: follow it, so this tab never keeps using a refresh token that was already exchanged.
    if (typeof window !== 'undefined') {
      window.addEventListener('storage', (event) => {
        if (event.key === null || event.key === TOKEN_KEY || event.key === REFRESH_KEY || event.key === USER_KEY) {
          this.tokenSignal.set(readStored(TOKEN_KEY));
          this.refreshSignal.set(readStored(REFRESH_KEY));
          this.userSignal.set(readStoredUser());
        }
      });
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

  /**
   * Ends the session here at once and tells the server to end it too (the refresh token stops working everywhere). The server call is
   * best-effort: being offline must not stop the person from leaving.
   */
  logout(): void {
    const refreshToken = this.refreshSignal();
    this.clearSession();
    if (refreshToken) {
      this.http.post(`${environment.apiBaseUrl}/auth/logout`, { refreshToken }).pipe(catchError(() => of(null))).subscribe();
    }
  }

  /** Signs the person out of every device (needs a valid access token), then clears this one. */
  logoutAll(): Observable<void> {
    return this.http.post<void>(`${environment.apiBaseUrl}/auth/logout-all`, {}).pipe(tap(() => this.clearSession()));
  }

  hasRefreshToken(): boolean {
    return !!this.refreshSignal();
  }

  /**
   * Exchanges the refresh token for a new pair and stores it. Callers that need a token at the same moment share one request.
   * If it fails because another tab already renewed (the stored refresh token changed meanwhile), that tab's session is adopted
   * instead of signing the person out; any other failure ends the session.
   */
  refresh(): Observable<AuthResponse> {
    if (this.inFlightRefresh) {
      return this.inFlightRefresh;
    }
    const used = this.refreshSignal();
    if (!used) {
      return throwError(() => new Error('no refresh token'));
    }
    const request = this.http.post<AuthResponse>(`${environment.apiBaseUrl}/auth/refresh`, { refreshToken: used }).pipe(
      tap((response) => this.storeSession(response)),
      catchError((error) => {
        const stored = readStored(REFRESH_KEY);
        const token = readStored(TOKEN_KEY);
        const user = readStoredUser();
        if (stored && stored !== used && token && user) {
          this.tokenSignal.set(token);
          this.refreshSignal.set(stored);
          this.userSignal.set(user);
          return of({ token, refreshToken: stored, expiresAt: '', user } as AuthResponse);
        }
        this.clearSession();
        return throwError(() => error);
      }),
      finalize(() => (this.inFlightRefresh = null)),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    this.inFlightRefresh = request;
    return request;
  }

  /** A token that is good for at least a little longer, renewing first if it is not. Resolves '' when there is no session. */
  async freshToken(): Promise<string> {
    const token = this.tokenSignal();
    const claims = token ? decodeJwtPayload(token) : null;
    if (token && claims && claims.exp - Date.now() / 1000 > RENEW_BEFORE_SECONDS) {
      return token;
    }
    if (!this.refreshSignal()) {
      return token && !isExpired(claims) ? token : '';
    }
    try {
      return (await firstValueFrom(this.refresh())).token;
    } catch {
      return '';
    }
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
    if (response.refreshToken) {
      localStorage.setItem(REFRESH_KEY, response.refreshToken);
      this.refreshSignal.set(response.refreshToken);
    }
    this.tokenSignal.set(response.token);
    this.userSignal.set(response.user);
  }

  private clearSession(): void {
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(USER_KEY);
    localStorage.removeItem(REFRESH_KEY);
    this.refreshSignal.set(null);
    this.tokenSignal.set(null);
    this.userSignal.set(null);
  }
}

function readStored(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null; // private-browsing / storage disabled
  }
}

function readStoredToken(): string | null {
  return readStored(TOKEN_KEY);
}

function readStoredUser(): UserSummary | null {
  try {
    const raw = localStorage.getItem(USER_KEY);
    return raw ? (JSON.parse(raw) as UserSummary) : null;
  } catch {
    return null;
  }
}
