import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { Router } from '@angular/router';

import { PasswordVisibilityDirective } from '../../core/directives/password-visibility.directive';
import { ProfileResponse } from '../../core/models/account.models';
import { AccountService } from '../../core/services/account.service';
import { AuthService } from '../../core/services/auth.service';
import { ThemeService } from '../../core/services/theme.service';
import { extractErrorMessage } from '../../core/utils/http-error';
import { TopNavComponent } from '../../shared/top-nav/top-nav.component';

function passwordsMatch(control: AbstractControl): ValidationErrors | null {
  const password = control.get('newPassword')?.value;
  const confirm = control.get('confirmPassword')?.value;
  return password && confirm && password !== confirm ? { passwordMismatch: true } : null;
}

/** The phrase the person types to unlock the delete button, so a slip of the mouse cannot delete an account. */
export const DELETE_PHRASE = 'DELETE';

/** Everything a person can do to their own account: see it, rename themselves, change the password, delete it, and read what is kept. */
@Component({
  selector: 'app-profile',
  imports: [ReactiveFormsModule, DatePipe, TopNavComponent, PasswordVisibilityDirective],
  templateUrl: './profile.component.html',
  styleUrl: './profile.component.scss',
})
export class ProfileComponent {
  private readonly fb = inject(FormBuilder);
  private readonly account = inject(AccountService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  readonly theme = inject(ThemeService);

  readonly deletePhrase = DELETE_PHRASE;
  readonly profile = signal<ProfileResponse | null>(null);
  readonly loadError = signal<string | null>(null);

  // ---- name ----
  readonly nameForm = this.fb.nonNullable.group({ name: ['', [Validators.required, Validators.maxLength(120), Validators.pattern(/\S/)]] });
  readonly savingName = signal(false);
  readonly nameMessage = signal<string | null>(null);
  readonly nameError = signal<string | null>(null);

  /**
   * A method, not a `computed`: a form control's value is not a signal, so a computed would be evaluated once (before the profile
   * arrives) and never again, which left "Save name" disabled forever. Evaluated on every change detection instead, which is cheap.
   */
  nameChanged(): boolean {
    return this.nameForm.controls.name.value.trim() !== (this.profile()?.name ?? '');
  }

  // ---- password ----
  readonly passwordForm = this.fb.nonNullable.group(
    {
      currentPassword: ['', [Validators.required]],
      newPassword: ['', [Validators.required, Validators.minLength(8), Validators.maxLength(128)]],
      confirmPassword: ['', [Validators.required]],
    },
    { validators: passwordsMatch },
  );
  readonly savingPassword = signal(false);
  readonly passwordMessage = signal<string | null>(null);
  readonly passwordError = signal<string | null>(null);

  // ---- delete ----
  readonly deleteStep = signal<'closed' | 'confirm'>('closed');
  readonly deleteForm = this.fb.nonNullable.group({
    password: ['', [Validators.required]],
    phrase: ['', [Validators.required]],
  });
  readonly signingOutAll = signal(false);
  readonly signOutAllError = signal<string | null>(null);
  readonly deleting = signal(false);
  readonly deleteError = signal<string | null>(null);

  constructor() {
    this.account.getProfile().subscribe({
      next: (profile) => {
        this.profile.set(profile);
        this.nameForm.setValue({ name: profile.name });
      },
      error: (err: HttpErrorResponse) => this.loadError.set(extractErrorMessage(err)),
    });
  }

  /** True once the typed phrase matches exactly (the delete button stays disabled until then). */
  deleteUnlocked(): boolean {
    const { password, phrase } = this.deleteForm.getRawValue();
    return password.length > 0 && phrase === DELETE_PHRASE;
  }

  saveName(): void {
    if (this.nameForm.invalid || !this.nameChanged() || this.savingName()) {
      this.nameForm.markAllAsTouched();
      return;
    }
    this.savingName.set(true);
    this.nameMessage.set(null);
    this.nameError.set(null);
    this.account.updateProfile({ name: this.nameForm.getRawValue().name.trim() }).subscribe({
      next: (profile) => {
        this.savingName.set(false);
        this.profile.set(profile);
        this.nameForm.setValue({ name: profile.name });
        this.nameMessage.set('Name saved.');
      },
      error: (err: HttpErrorResponse) => {
        this.savingName.set(false);
        this.nameError.set(extractErrorMessage(err));
      },
    });
  }

  savePassword(): void {
    if (this.passwordForm.invalid || this.savingPassword()) {
      this.passwordForm.markAllAsTouched();
      return;
    }
    this.savingPassword.set(true);
    this.passwordMessage.set(null);
    this.passwordError.set(null);
    const { currentPassword, newPassword } = this.passwordForm.getRawValue();
    this.account.changePassword({ currentPassword, newPassword }).subscribe({
      next: () => {
        this.savingPassword.set(false);
        this.passwordForm.reset();
        this.passwordMessage.set('Password changed. Your other devices have been signed out.');
      },
      error: (err: HttpErrorResponse) => {
        this.savingPassword.set(false);
        this.passwordError.set(extractErrorMessage(err));
      },
    });
  }

  askToDelete(): void {
    this.deleteError.set(null);
    this.deleteForm.reset();
    this.deleteStep.set('confirm');
  }

  cancelDelete(): void {
    this.deleteStep.set('closed');
    this.deleteForm.reset();
    this.deleteError.set(null);
  }

  confirmDelete(): void {
    if (!this.deleteUnlocked() || this.deleting()) {
      return;
    }
    this.deleting.set(true);
    this.deleteError.set(null);
    this.account.deleteAccount({ password: this.deleteForm.getRawValue().password }).subscribe({
      next: () => void this.router.navigateByUrl('/login'),
      error: (err: HttpErrorResponse) => {
        this.deleting.set(false);
        this.deleteError.set(extractErrorMessage(err));
      },
    });
  }

  signOutEverywhere(): void {
    if (this.signingOutAll()) {
      return;
    }
    this.signingOutAll.set(true);
    this.signOutAllError.set(null);
    this.auth.logoutAll().subscribe({
      next: () => void this.router.navigateByUrl('/login'),
      error: (err: HttpErrorResponse) => {
        this.signingOutAll.set(false);
        this.signOutAllError.set(extractErrorMessage(err));
      },
    });
  }

  logout(): void {
    this.auth.logout();
    void this.router.navigateByUrl('/login');
  }
}
