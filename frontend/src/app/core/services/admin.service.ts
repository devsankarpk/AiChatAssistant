import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';
import { AdminUser, PagedAdminUsers, PagedUsageLogResponse, UsageSummary } from '../models/admin.models';

export interface UsageRange {
  from?: string | null;
  to?: string | null;
}

export interface UsageQuery {
  page: number;
  pageSize: number;
  from?: string | null;
  to?: string | null;
}

@Injectable({ providedIn: 'root' })
export class AdminService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBaseUrl}/admin`;

  getUsage(query: UsageQuery): Observable<PagedUsageLogResponse> {
    let params = new HttpParams().set('page', query.page).set('pageSize', query.pageSize);
    if (query.from) {
      params = params.set('from', query.from);
    }
    if (query.to) {
      params = params.set('to', query.to);
    }
    return this.http.get<PagedUsageLogResponse>(`${this.base}/usage`, { params });
  }

  /** Totals, biggest spenders and the per-day series for the whole range. */
  getUsageSummary(range: UsageRange): Observable<UsageSummary> {
    return this.http.get<UsageSummary>(`${this.base}/usage/summary`, { params: rangeParams(range) });
  }

  /** The usage rows in the range as a CSV file. */
  exportUsage(range: UsageRange): Observable<Blob> {
    return this.http.get(`${this.base}/usage/export`, { params: rangeParams(range), responseType: 'blob' });
  }

  getUsers(query: { search?: string; page: number; pageSize: number }): Observable<PagedAdminUsers> {
    let params = new HttpParams().set('page', query.page).set('pageSize', query.pageSize);
    if (query.search) {
      params = params.set('search', query.search);
    }
    return this.http.get<PagedAdminUsers>(`${this.base}/users`, { params });
  }

  /** Makes the person an Admin or a User. Their current sessions end; the new role applies when they log in again. */
  changeRole(userId: number, role: 'Admin' | 'User'): Observable<AdminUser> {
    return this.http.put<AdminUser>(`${this.base}/users/${userId}/role`, { role });
  }

  disableUser(userId: number): Observable<AdminUser> {
    return this.http.post<AdminUser>(`${this.base}/users/${userId}/disable`, {});
  }

  enableUser(userId: number): Observable<AdminUser> {
    return this.http.post<AdminUser>(`${this.base}/users/${userId}/enable`, {});
  }
}

function rangeParams(range: UsageRange): HttpParams {
  let params = new HttpParams();
  if (range.from) {
    params = params.set('from', range.from);
  }
  if (range.to) {
    params = params.set('to', range.to);
  }
  return params;
}
