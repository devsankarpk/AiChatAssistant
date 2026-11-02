import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';

import { AuthService } from '../../core/services/auth.service';
import { extractErrorMessage } from '../../core/utils/http-error';
import { AuthShellComponent } from '../auth-shell/auth-shell.component';

/** The page behind the "confirm your email" link: it uses the one-time token in the address straight away and says how it went. */
@Component({
  selector: 'app-verify-email',
  imports: [RouterLink, AuthShellComponent],
  template: `
    <app-auth-shell>
      @if (!token) {
        <div class="auth-card">
          <h1>This link is incomplete</h1>
          <p class="muted">The confirmation link is missing part of its address. Open it straight from the email, or log in to ask for a new one.</p>
          <a class="btn btn-primary" routerLink="/login">Log in</a>
        </div>
      } @else if (state() === 'working') {
        <div class="auth-card" role="status">
          <h1>Confirming your email…</h1>
        </div>
      } @else if (state() === 'done') {
        <div class="auth-card" role="status">
          <h1>Email confirmed</h1>
          <p>{{ message() }}</p>
          <a class="btn btn-primary" routerLink="/login">Log in</a>
        </div>
      } @else {
        <div class="auth-card">
          <h1>We couldn't confirm that</h1>
          <p class="form-error" role="alert">{{ message() }}</p>
          <p class="muted">Log in with your email and password and we'll offer to send a new link.</p>
          <a class="btn btn-primary" routerLink="/login">Log in</a>
        </div>
      }
    </app-auth-shell>
  `,
})
export class VerifyEmailComponent {
  private readonly auth = inject(AuthService);

  // Read once: this page is only ever reached through a one-time emailed link (same reasoning as the reset-password page).
  readonly token = inject(ActivatedRoute).snapshot.queryParamMap.get('token');
  readonly state = signal<'working' | 'done' | 'failed'>('working');
  readonly message = signal('');

  constructor() {
    if (!this.token) {
      return;
    }
    this.auth.verifyEmail({ token: this.token }).subscribe({
      next: (res) => {
        this.message.set(res.message);
        this.state.set('done');
      },
      error: (err: HttpErrorResponse) => {
        this.message.set(extractErrorMessage(err));
        this.state.set('failed');
      },
    });
  }
}
