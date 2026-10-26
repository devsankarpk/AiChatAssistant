import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { environment } from '../../../environments/environment';
import { RegisterComponent } from './register.component';

const URL = `${environment.apiBaseUrl}/auth/register`;

function fakeJwt(): string {
  const payload = btoa(JSON.stringify({ sub: '2', email: 'new@example.com', role: 'User', exp: Math.floor(Date.now() / 1000) + 3600 }));
  return `${btoa('{"alg":"none"}')}.${payload}.sig`;
}

/** What the person sees when creating an account goes right or wrong. */
describe('RegisterComponent flows', () => {
  let fixture: ComponentFixture<RegisterComponent>;
  let component: RegisterComponent;
  let http: HttpTestingController;
  let navigateByUrl: jasmine.Spy;

  const el = <T extends Element>(s: string) => (fixture.nativeElement as HTMLElement).querySelector<T>(s);
  const fill = (name = 'New Person', email = 'new@example.com', password = 'long-enough') => component.form.setValue({ name, email, password });

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ imports: [RegisterComponent], providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    http = TestBed.inject(HttpTestingController);
    navigateByUrl = spyOn(TestBed.inject(Router), 'navigateByUrl').and.resolveTo(true);
    fixture = TestBed.createComponent(RegisterComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => {
    http.verify();
    localStorage.clear();
  });

  it('sends name, email and password to POST /auth/register', () => {
    fill('New Person', 'new@example.com', 'long-enough');
    component.submit();

    const req = http.expectOne(URL);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ name: 'New Person', email: 'new@example.com', password: 'long-enough' });
    req.flush({ token: fakeJwt(), expiresAt: '', user: { id: 2, name: 'New Person', email: 'new@example.com', roles: ['User'] } });
  });

  it('success signs the person in and opens the chat', () => {
    fill();
    component.submit();
    http.expectOne(URL).flush({ token: fakeJwt(), expiresAt: '', user: { id: 2, name: 'New Person', email: 'new@example.com', roles: ['User'] } });

    expect(navigateByUrl).toHaveBeenCalledWith('/');
    expect(localStorage.getItem('aichat.auth.token')).not.toBeNull();
  });

  it('an email that is already registered shows the server\'s message inline and stays on the page', () => {
    fill();
    component.submit();

    http.expectOne(URL).flush({ error: { code: 'email_taken', message: 'An account with that email already exists.' } }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(el('.form-error')?.textContent).toContain('An account with that email already exists.');
    expect(component.submitting()).toBeFalse();
    expect(navigateByUrl).not.toHaveBeenCalled();
    expect(localStorage.getItem('aichat.auth.token')).toBeNull();
  });

  it('a server-side validation message is shown as it is', () => {
    fill();
    component.submit();

    http.expectOne(URL).flush({ error: { code: 'validation_error', message: 'Password must be at least 8 characters long.' } }, { status: 400, statusText: 'Bad Request' });

    expect(component.errorMessage()).toBe('Password must be at least 8 characters long.');
  });

  it('server unreachable: says so in plain words', () => {
    fill();
    component.submit();

    http.expectOne(URL).error(new ProgressEvent('error'));

    expect(component.errorMessage()).toContain('Could not reach the server');
  });

  it('shows "Creating…" style busy state and sends only one request for a double click', () => {
    fill();
    component.submit();
    component.submit();
    fixture.detectChanges();

    expect(component.submitting()).toBeTrue();
    expect((el('button[type=submit]') as HTMLButtonElement).disabled).toBeTrue();
    http.expectOne(URL).flush({}, { status: 500, statusText: 'x' });
  });

  describe('local checks, before any request', () => {
    it('refuses an empty form', () => {
      component.submit();

      http.expectNone(URL);
      expect(component.form.invalid).toBeTrue();
    });

    it('refuses a name longer than 120 characters but accepts exactly 120', () => {
      fill('a'.repeat(121));
      expect(component.form.controls.name.invalid).toBeTrue();
      fill('a'.repeat(120));
      expect(component.form.controls.name.invalid).toBeFalse();
    });

    it('refuses a malformed email', () => {
      fill('New', 'not-an-email', 'long-enough');
      component.submit();

      http.expectNone(URL);
      expect(component.form.controls.email.invalid).toBeTrue();
    });

    it('password: 7 characters refused, 8 accepted', () => {
      fill('New', 'new@example.com', '1234567');
      expect(component.form.controls.password.invalid).toBeTrue();
      fill('New', 'new@example.com', '12345678');
      expect(component.form.controls.password.invalid).toBeFalse();
    });
  });
});
