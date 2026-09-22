import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';
import { PagedUsageLogResponse } from '../models/admin.models';

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
}
