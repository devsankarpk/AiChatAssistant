import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';

import { isRegistrationPending } from '../../core/models/auth.models';
import { AuthService } from '../../core/services/auth.service';
import { extractErrorMessage } from '../../core/utils/http-error';
import { PasswordVisibilityDirective } from '../../core/directives/password-visibility.directive';
import { AuthShellComponent } from '../auth-shell/auth-shell.component';

@Component({
  selector: 'app-register',
  imports: [ReactiveFormsModule, RouterLink, AuthShellComponent, PasswordVisibilityDirective],
  templateUrl: './register.component.html',
  styleUrl: './register.component.scss',
})
export class RegisterComponent {
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(120)]],
    email: ['', [Validators.required, Validators.email]],
    // Mirrors RegisterRequestValidator.cs (MinimumLength(8)) so the user hits this
    // before the round trip, not after.
    password: ['', [Validators.required, Validators.minLength(8)]],
  });

  readonly submitting = signal(false);
  readonly errorMessage = signal<string | null>(null);
  /** Set when the server wants the email confirmed first: the page then says "check your email" instead of signing in. */
  readonly pendingEmail = signal<string | null>(null);
  readonly resendMessage = signal<string | null>(null);

  submit(): void {
    if (this.form.invalid || this.submitting()) {
      this.form.markAllAsTouched();
      return;
    }

    this.submitting.set(true);
    this.errorMessage.set(null);

    this.auth.register(this.form.getRawValue()).subscribe({
      next: (response) => {
        if (isRegistrationPending(response)) {
          this.submitting.set(false);
          this.pendingEmail.set(this.form.getRawValue().email.trim());
        } else {
          this.router.navigateByUrl('/');
        }
      },
      error: (err: HttpErrorResponse) => {
        this.submitting.set(false);
        this.errorMessage.set(extractErrorMessage(err));
      },
    });
  }

  /** Asks for another confirmation link. The server answers the same whatever the address, so this always shows the same note. */
  resend(): void {
    const email = this.pendingEmail();
    if (!email) {
      return;
    }
    this.resendMessage.set(null);
    this.auth.resendVerification({ email }).subscribe({
      next: (res) => this.resendMessage.set(res.message),
      error: (err: HttpErrorResponse) => this.errorMessage.set(extractErrorMessage(err)),
    });
  }
}
