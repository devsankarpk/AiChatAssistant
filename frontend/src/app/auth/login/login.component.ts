import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import { AuthService } from '../../core/services/auth.service';
import { extractErrorMessage } from '../../core/utils/http-error';
import { PasswordVisibilityDirective } from '../../core/directives/password-visibility.directive';
import { AuthShellComponent } from '../auth-shell/auth-shell.component';

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule, RouterLink, AuthShellComponent, PasswordVisibilityDirective],
  templateUrl: './login.component.html',
  styleUrl: './login.component.scss',
})
export class LoginComponent {
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly form = this.fb.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required]],
  });

  readonly submitting = signal(false);
  readonly errorMessage = signal<string | null>(null);
  /** True when the server said the password was right but the email is not confirmed yet: the page then offers a new link. */
  readonly needsConfirmation = signal(false);
  readonly resendMessage = signal<string | null>(null);

  submit(): void {
    if (this.form.invalid || this.submitting()) {
      this.form.markAllAsTouched();
      return;
    }

    this.submitting.set(true);
    this.errorMessage.set(null);
    this.needsConfirmation.set(false);
    this.resendMessage.set(null);

    this.auth.login(this.form.getRawValue()).subscribe({
      next: () => {
        const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl') ?? '/';
        this.router.navigateByUrl(returnUrl);
      },
      error: (err: HttpErrorResponse) => {
        this.submitting.set(false);
        this.needsConfirmation.set(err.status === 403 && err.error?.error?.code === 'email_not_verified');
        this.errorMessage.set(extractErrorMessage(err));
      },
    });
  }

  /** Asks for another confirmation link for the typed address. The answer is the same whatever the address. */
  resendConfirmation(): void {
    this.resendMessage.set(null);
    this.auth.resendVerification({ email: this.form.getRawValue().email.trim() }).subscribe({
      next: (res) => this.resendMessage.set(res.message),
      error: (err: HttpErrorResponse) => this.errorMessage.set(extractErrorMessage(err)),
    });
  }
}
