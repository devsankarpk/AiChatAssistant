import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, inject, signal } from '@angular/core';

import { environment } from '../../../environments/environment';
import { extractErrorMessage } from '../../core/utils/http-error';

/** Calls the Admin-only backend-dotnet AdminController.Ping to prove RBAC works end to end. */
@Component({
  selector: 'app-admin-ping',
  imports: [],
  templateUrl: './ping.component.html',
  styleUrl: './ping.component.scss',
})
export class PingComponent implements OnInit {
  private readonly http = inject(HttpClient);

  readonly result = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.http.get<{ message: string; scope: string }>(`${environment.apiBaseUrl}/admin/ping`).subscribe({
      next: (res) => this.result.set(`${res.message} (${res.scope})`),
      error: (err: HttpErrorResponse) => this.error.set(extractErrorMessage(err)),
    });
  }
}
