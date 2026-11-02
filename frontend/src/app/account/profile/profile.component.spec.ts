import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { environment } from '../../../environments/environment';
import { DirectStateService } from '../../core/services/direct-state.service';
import { AuthService } from '../../core/services/auth.service';
import { DELETE_PHRASE, ProfileComponent } from './profile.component';

const BASE = `${environment.apiBaseUrl}/account`;
const profile = { id: 1, name: 'Ada Lovelace', email: 'ada@example.com', roles: ['User'], createdAt: '2026-01-15T10:00:00Z', emailVerified: true };

function fakeJwt(): string {
  const payload = btoa(JSON.stringify({ sub: '1', email: 'ada@example.com', role: 'User', exp: Math.floor(Date.now() / 1000) + 3600 }));
  return `${btoa('{"alg":"none"}')}.${payload}.sig`;
}

/** The account page: details, rename, change password, the privacy note and the two-step delete. */
describe('ProfileComponent', () => {
  let fixture: ComponentFixture<ProfileComponent>;
  let component: ProfileComponent;
  let http: HttpTestingController;
  let navigateByUrl: jasmine.Spy;
  let auth: AuthService;

  const el = <T extends Element>(s: string) => (fixture.nativeElement as HTMLElement).querySelector<T>(s);
  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';
  const button = (label: string) => Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')).find((b) => b.textContent?.trim() === label) as HTMLButtonElement | undefined;

  async function create(answer: object = profile, status = 200): Promise<void> {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [ProfileComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), { provide: DirectStateService, useValue: { unreadTotal: signal(0) } }],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
    auth = TestBed.inject(AuthService);
    auth.acceptSession({ token: fakeJwt(), expiresAt: '', user: { id: 1, name: 'Ada Lovelace', email: 'ada@example.com', roles: ['User'] } });
    navigateByUrl = spyOn(TestBed.inject(Router), 'navigateByUrl').and.resolveTo(true);
    fixture = TestBed.createComponent(ProfileComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    const req = http.expectOne(BASE);
    if (status === 200) req.flush(answer);
    else req.flush(answer, { status, statusText: 'x' });
    fixture.detectChanges();
  }

  afterEach(() => {
    http.verify();
    localStorage.clear();
  });

  describe('details', () => {
    it('shows the email, the member-since date and whether the address is confirmed', async () => {
      await create();

      expect(text()).toContain('ada@example.com');
      expect(text()).toContain('January 15, 2026');
      expect(text()).toContain('Confirmed');
    });

    it('says "Not confirmed yet" for an unconfirmed address', async () => {
      await create({ ...profile, emailVerified: false });

      expect(text()).toContain('Not confirmed yet');
    });

    it('shows the server\'s message when the profile cannot be loaded', async () => {
      await create({ error: { code: 'internal_error', message: 'An unexpected error occurred.' } }, 500);

      expect(el('.form-error')?.textContent).toContain('An unexpected error occurred.');
      expect(el('#name-title')).toBeNull();
    });

    it('explains what is kept, who can see it and how long, in plain words', async () => {
      await create();

      const note = el('.privacy')?.textContent ?? '';
      expect(note).toContain('one-way hash');
      expect(note).toContain('AI provider');
      expect(note).toContain('administrators');
      expect(note).toContain('only to the two people');
      expect(note).toContain('until you delete your account');
    });
  });

  describe('name', () => {
    beforeEach(async () => create());

    it('starts filled in with the current name, and Save is off until it changes', () => {
      expect(component.nameForm.controls.name.value).toBe('Ada Lovelace');
      expect(button('Save name')?.disabled).toBeTrue();

      component.nameForm.setValue({ name: 'Ada King' });
      fixture.detectChanges();

      expect(button('Save name')?.disabled).toBeFalse();
    });

    it('saves a new name with PUT, shows "Name saved." and updates the remembered name', () => {
      component.nameForm.setValue({ name: '  Ada King  ' });
      component.saveName();

      const req = http.expectOne(`${BASE}/profile`);
      expect(req.request.body).toEqual({ name: 'Ada King' });
      req.flush({ ...profile, name: 'Ada King' });
      fixture.detectChanges();

      expect(el('[role=status]')?.textContent).toContain('Name saved.');
      expect(auth.user()?.name).toBe('Ada King');
      expect(button('Save name')?.disabled).toBeTrue(); // nothing left to save
    });

    it('does not send an unchanged or blank name', () => {
      component.saveName();
      component.nameForm.setValue({ name: '   ' });
      component.saveName();

      http.expectNone(`${BASE}/profile`);
      expect(component.nameForm.controls.name.invalid).toBeTrue();
    });

    it('shows a server error inline and keeps the old name', () => {
      component.nameForm.setValue({ name: 'New' });
      component.saveName();

      http.expectOne(`${BASE}/profile`).flush({ error: { code: 'validation_error', message: 'Name is too long.' } }, { status: 400, statusText: 'Bad Request' });
      fixture.detectChanges();

      expect(el('.form-error')?.textContent).toContain('Name is too long.');
      expect(auth.user()?.name).toBe('Ada Lovelace');
    });
  });

  describe('password', () => {
    beforeEach(async () => create());

    const fill = (current = 'old-password-1', next = 'new-password-2', confirm = 'new-password-2') =>
      component.passwordForm.setValue({ currentPassword: current, newPassword: next, confirmPassword: confirm });

    it('sends the current and new password, takes the new token, clears the form and says other devices were signed out', () => {
      const fresh = fakeJwt();
      fill();
      component.savePassword();

      const req = http.expectOne(`${BASE}/change-password`);
      expect(req.request.body).toEqual({ currentPassword: 'old-password-1', newPassword: 'new-password-2' });
      req.flush({ token: fresh, expiresAt: '', user: { id: 1, name: 'Ada Lovelace', email: 'ada@example.com', roles: ['User'] } });
      fixture.detectChanges();

      expect(auth.getToken()).toBe(fresh);
      expect(text()).toContain('Your other devices have been signed out');
      expect(component.passwordForm.getRawValue()).toEqual({ currentPassword: '', newPassword: '', confirmPassword: '' });
    });

    it('a wrong current password shows the server\'s message inline and keeps what was typed', () => {
      fill();
      component.savePassword();

      http.expectOne(`${BASE}/change-password`).flush({ error: { code: 'invalid_current_password', message: 'The current password is not correct.' } }, { status: 400, statusText: 'Bad Request' });
      fixture.detectChanges();

      expect(el('.form-error')?.textContent).toContain('The current password is not correct.');
      expect(component.savingPassword()).toBeFalse();
      expect(component.passwordForm.controls.newPassword.value).toBe('new-password-2');
    });

    it('a locked-out change (423) shows the wait', () => {
      fill();
      component.savePassword();

      http.expectOne(`${BASE}/change-password`).flush({ error: { code: 'account_locked', message: 'Too many incorrect passwords. Try again in 15 minutes.' } }, { status: 423, statusText: 'Locked' });

      expect(component.passwordError()).toContain('Try again in 15 minutes');
    });

    it('refuses locally: mismatched confirmation, a short new password, an empty current password', () => {
      fill('old-password-1', 'new-password-2', 'different');
      component.savePassword();
      fill('old-password-1', 'short', 'short');
      component.savePassword();
      fill('', 'new-password-2', 'new-password-2');
      component.savePassword();

      http.expectNone(`${BASE}/change-password`);
      expect(component.passwordForm.invalid).toBeTrue();
    });

    it('says the passwords do not match once the confirmation is touched', () => {
      fill('old-password-1', 'new-password-2', 'different');
      component.passwordForm.controls.confirmPassword.markAsTouched();
      fixture.detectChanges();

      expect(text()).toContain("The two passwords don't match.");
    });

    it('ignores a second submit while the first is still running', () => {
      fill();
      component.savePassword();
      component.savePassword();

      http.expectOne(`${BASE}/change-password`).flush({ token: fakeJwt(), expiresAt: '', user: { id: 1, name: 'A', email: 'a', roles: [] } });
    });
  });

  describe('deleting the account', () => {
    beforeEach(async () => create());

    it('starts closed: only a "Delete my account…" button and a warning, no password field', () => {
      expect(button('Delete my account…')).toBeDefined();
      expect(el('#delete-password')).toBeNull();
      expect(text()).toContain('This cannot be undone');
    });

    it('the first click only reveals the confirmation step; nothing is sent', () => {
      button('Delete my account…')!.click();
      fixture.detectChanges();

      expect(el('#delete-password')).not.toBeNull();
      expect(text()).toContain(`type ${DELETE_PHRASE}`);
      http.expectNone(`${BASE}/delete`);
    });

    it('the delete button stays disabled until BOTH the password and the exact phrase are in', () => {
      component.askToDelete();
      fixture.detectChanges();
      const danger = () => button('Delete account permanently')!;

      expect(danger().disabled).toBeTrue();
      component.deleteForm.setValue({ password: 'secret', phrase: '' });
      fixture.detectChanges();
      expect(danger().disabled).toBeTrue();
      component.deleteForm.setValue({ password: 'secret', phrase: 'delete' }); // wrong case
      fixture.detectChanges();
      expect(danger().disabled).toBeTrue();
      component.deleteForm.setValue({ password: '', phrase: DELETE_PHRASE });
      fixture.detectChanges();
      expect(danger().disabled).toBeTrue();
      component.deleteForm.setValue({ password: 'secret', phrase: DELETE_PHRASE });
      fixture.detectChanges();
      expect(danger().disabled).toBeFalse();
    });

    it('Cancel closes the step and forgets what was typed', () => {
      component.askToDelete();
      component.deleteForm.setValue({ password: 'secret', phrase: DELETE_PHRASE });

      component.cancelDelete();
      fixture.detectChanges();

      expect(el('#delete-password')).toBeNull();
      expect(component.deleteForm.getRawValue()).toEqual({ password: '', phrase: '' });
    });

    it('confirming POSTs the password, clears the session and goes to the login page', () => {
      component.askToDelete();
      component.deleteForm.setValue({ password: 'secret', phrase: DELETE_PHRASE });
      component.confirmDelete();

      const req = http.expectOne(`${BASE}/delete`);
      expect(req.request.body).toEqual({ password: 'secret' });
      req.flush(null, { status: 204, statusText: 'No Content' });

      expect(auth.isAuthenticated()).toBeFalse();
      expect(navigateByUrl).toHaveBeenCalledWith('/login');
    });

    it('a wrong password shows the server\'s message, keeps the person signed in and on the page', () => {
      component.askToDelete();
      component.deleteForm.setValue({ password: 'wrong', phrase: DELETE_PHRASE });
      component.confirmDelete();

      http.expectOne(`${BASE}/delete`).flush({ error: { code: 'invalid_current_password', message: 'The password is not correct.' } }, { status: 400, statusText: 'Bad Request' });
      fixture.detectChanges();

      expect(el('.form-error')?.textContent).toContain('The password is not correct.');
      expect(auth.isAuthenticated()).toBeTrue();
      expect(navigateByUrl).not.toHaveBeenCalled();
      expect(component.deleting()).toBeFalse();
    });

    it('the only Admin is told why they cannot delete themselves (409)', () => {
      component.askToDelete();
      component.deleteForm.setValue({ password: 'secret', phrase: DELETE_PHRASE });
      component.confirmDelete();

      http.expectOne(`${BASE}/delete`).flush({ error: { code: 'last_admin', message: 'You are the only Admin. Make someone else an Admin before deleting this account.' } }, { status: 409, statusText: 'Conflict' });

      expect(component.deleteError()).toContain('only Admin');
      expect(auth.isAuthenticated()).toBeTrue();
    });

    it('does nothing when the phrase is not typed, even if the method is called directly', () => {
      component.askToDelete();
      component.deleteForm.setValue({ password: 'secret', phrase: 'nope' });

      component.confirmDelete();

      http.expectNone(`${BASE}/delete`);
    });
  });

  describe('log out', () => {
    it('signs out and goes to the login page', async () => {
      await create();

      component.logout();

      expect(auth.isAuthenticated()).toBeFalse();
      expect(navigateByUrl).toHaveBeenCalledWith('/login');
    });
  });
});
