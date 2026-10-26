import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';

import { environment } from '../../../environments/environment';
import { LoginComponent } from './login.component';

const URL = `${environment.apiBaseUrl}/auth/login`;

function fakeJwt(): string {
  const payload = btoa(JSON.stringify({ sub: '1', email: 'ada@example.com', role: 'User', exp: Math.floor(Date.now() / 1000) + 3600 }));
  return `${btoa('{"alg":"none"}')}.${payload}.sig`;
}

/** What the person sees when logging in goes right or wrong. */
describe('LoginComponent flows', () => {
  let fixture: ComponentFixture<LoginComponent>;
  let component: LoginComponent;
  let http: HttpTestingController;
  let navigateByUrl: jasmine.Spy;

  const el = <T extends Element>(s: string) => (fixture.nativeElement as HTMLElement).querySelector<T>(s);

  function create(returnUrl: string | null = null): void {
    TestBed.configureTestingModule({
      imports: [LoginComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(returnUrl ? { returnUrl } : {}) } } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    navigateByUrl = spyOn(TestBed.inject(Router), 'navigateByUrl').and.resolveTo(true);
    fixture = TestBed.createComponent(LoginComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  beforeEach(() => localStorage.clear());
  afterEach(() => {
    http.verify();
    localStorage.clear();
  });

  const fill = (email = 'ada@example.com', password = 'secret-pass') => component.form.setValue({ email, password });

  it('sends the typed email and password to POST /auth/login', () => {
    create();
    fill();

    component.submit();

    const req = http.expectOne(URL);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ email: 'ada@example.com', password: 'secret-pass' });
    req.flush({ token: fakeJwt(), expiresAt: '2030-01-01T00:00:00Z', user: { id: 1, name: 'Ada', email: 'ada@example.com', roles: ['User'] } });
  });

  it('wrong password: shows the server\'s message inline, re-enables the button, stays on the page', () => {
    create();
    fill('ada@example.com', 'wrong');
    component.submit();

    http.expectOne(URL).flush({ error: { code: 'invalid_credentials', message: 'Invalid email or password.' } }, { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();

    expect(component.submitting()).toBeFalse();
    expect(el('.form-error')?.textContent).toContain('Invalid email or password.');
    expect(el('.form-error')?.getAttribute('role')).toBe('alert');
    expect((el('button[type=submit]') as HTMLButtonElement).disabled).toBeFalse();
    expect(navigateByUrl).not.toHaveBeenCalled();
    expect(localStorage.getItem('aichat.auth.token')).toBeNull();
  });

  it('server unreachable: says so in plain words', () => {
    create();
    fill();
    component.submit();

    http.expectOne(URL).error(new ProgressEvent('error'));
    fixture.detectChanges();

    expect(el('.form-error')?.textContent).toContain('Could not reach the server');
    expect(component.submitting()).toBeFalse();
  });

  it('an unexpected server error without the API shape gets the generic message', () => {
    create();
    fill();
    component.submit();

    http.expectOne(URL).flush('Bad gateway', { status: 502, statusText: 'Bad Gateway' });

    expect(component.errorMessage()).toBe('Something went wrong. Please try again.');
  });

  it('a new attempt clears the previous error', () => {
    create();
    fill();
    component.submit();
    http.expectOne(URL).flush({ error: { code: 'x', message: 'Invalid email or password.' } }, { status: 401, statusText: 'x' });

    component.submit();

    expect(component.errorMessage()).toBeNull();
    http.expectOne(URL).flush({}, { status: 500, statusText: 'x' });
  });

  it('shows "Logging in…" and ignores a second click while the request is running', () => {
    create();
    fill();
    component.submit();
    component.submit();
    fixture.detectChanges();

    expect(el('button[type=submit]')?.textContent).toContain('Logging in…');
    expect((el('button[type=submit]') as HTMLButtonElement).disabled).toBeTrue();
    http.expectOne(URL).flush({}, { status: 500, statusText: 'x' }); // exactly one request
  });

  it('success goes to the chat, or to the page the person was sent from', () => {
    create();
    fill();
    component.submit();
    http.expectOne(URL).flush({ token: fakeJwt(), expiresAt: '', user: { id: 1, name: 'Ada', email: 'ada@example.com', roles: ['User'] } });

    expect(navigateByUrl).toHaveBeenCalledWith('/');
  });

  it('after being bounced from a protected page, success returns there', () => {
    create('/messages?c=3');
    fill();
    component.submit();
    http.expectOne(URL).flush({ token: fakeJwt(), expiresAt: '', user: { id: 1, name: 'Ada', email: 'ada@example.com', roles: ['User'] } });

    expect(navigateByUrl).toHaveBeenCalledWith('/messages?c=3');
  });

  describe('before anything is sent', () => {
    beforeEach(() => create());

    it('an empty form sends nothing and shows both field messages', () => {
      component.submit();
      fixture.detectChanges();

      http.expectNone(URL);
      const messages = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('.field-error')).map((e) => e.textContent);
      expect(messages).toEqual(['Enter a valid email address.', 'Enter your password.']);
    });

    it('a malformed email is refused locally', () => {
      fill('not-an-email', 'secret-pass');
      component.submit();

      http.expectNone(URL);
      expect(component.form.controls.email.invalid).toBeTrue();
    });

    it('a missing password is refused locally', () => {
      fill('ada@example.com', '');
      component.submit();

      http.expectNone(URL);
    });
  });
});
