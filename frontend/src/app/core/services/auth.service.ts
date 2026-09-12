import { HttpClient } from '@angular/common/http';
import { Injectable, computed, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';

import { environment } from '../../../environments/environment';
import { AuthResponse, LoginRequest, RegisterRequest, UserSummary } from '../models/auth.models';
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

  register(request: RegisterRequest): Observable<AuthResponse> {
    return this.http
      .post<AuthResponse>(`${environment.apiBaseUrl}/auth/register`, request)
      .pipe(tap((response) => this.storeSession(response)));
  }

  login(request: LoginRequest): Observable<AuthResponse> {
    return this.http
      .post<AuthResponse>(`${environment.apiBaseUrl}/auth/login`, request)
      .pipe(tap((response) => this.storeSession(response)));
  }

  logout(): void {
    this.clearSession();
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
