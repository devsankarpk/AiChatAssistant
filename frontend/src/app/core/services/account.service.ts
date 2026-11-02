import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, tap } from 'rxjs';

import { environment } from '../../../environments/environment';
import { ChangePasswordRequest, DeleteAccountRequest, ProfileResponse, UpdateProfileRequest } from '../models/account.models';
import { AuthResponse } from '../models/auth.models';
import { AuthService } from './auth.service';

/** The signed-in person's own account. Keeps the stored session in step with what the server says (a new name, a new token, a deleted account). */
@Injectable({ providedIn: 'root' })
export class AccountService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly base = `${environment.apiBaseUrl}/account`;

  getProfile(): Observable<ProfileResponse> {
    return this.http.get<ProfileResponse>(this.base);
  }

  updateProfile(request: UpdateProfileRequest): Observable<ProfileResponse> {
    return this.http.put<ProfileResponse>(`${this.base}/profile`, request).pipe(tap((profile) => this.auth.updateStoredName(profile.name)));
  }

  /** Every other session is signed out by the server; the response carries a new token so this one keeps working. */
  changePassword(request: ChangePasswordRequest): Observable<AuthResponse> {
    return this.http.post<AuthResponse>(`${this.base}/change-password`, request).pipe(tap((response) => this.auth.acceptSession(response)));
  }

  /** Permanently deletes the account; on success the local session is cleared too. */
  deleteAccount(request: DeleteAccountRequest): Observable<void> {
    return this.http.post<void>(`${this.base}/delete`, request).pipe(tap(() => this.auth.logout()));
  }
}
