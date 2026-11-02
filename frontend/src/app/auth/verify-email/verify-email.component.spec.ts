import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';

import { environment } from '../../../environments/environment';
import { VerifyEmailComponent } from './verify-email.component';

const URL = `${environment.apiBaseUrl}/auth/verify-email`;

describe('VerifyEmailComponent', () => {
  let fixture: ComponentFixture<VerifyEmailComponent>;
  let http: HttpTestingController;
  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';

  async function create(token: string | null): Promise<void> {
    await TestBed.configureTestingModule({
      imports: [VerifyEmailComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(token ? { token } : {}) } } },
      ],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(VerifyEmailComponent);
    fixture.detectChanges();
  }

  afterEach(() => http.verify());

  it('uses the token in the address at once, and says it is working', async () => {
    await create('abc123');

    const req = http.expectOne(URL);
    expect(req.request.body).toEqual({ token: 'abc123' });
    expect(text()).toContain('Confirming your email');
    req.flush({ message: 'Your email address is confirmed. You can now log in.' });
  });

  it('on success says so and offers to log in', async () => {
    await create('abc123');
    http.expectOne(URL).flush({ message: 'Your email address is confirmed. You can now log in.' });
    fixture.detectChanges();

    expect(text()).toContain('Email confirmed');
    expect(text()).toContain('You can now log in.');
    expect(fixture.nativeElement.querySelector('a[href="/login"]')).not.toBeNull();
  });

  it('a used, expired or wrong link shows the server\'s message and points to logging in for a new one', async () => {
    await create('stale');
    http.expectOne(URL).flush({ error: { code: 'invalid_token', message: 'This confirmation link is invalid or has expired. Request a new one.' } }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(text()).toContain("We couldn't confirm that");
    expect(fixture.nativeElement.querySelector('.form-error')?.textContent).toContain('invalid or has expired');
    expect(text()).toContain("we'll offer to send a new link");
  });

  it('a link with no token sends nothing and says it is incomplete', async () => {
    await create(null);

    http.expectNone(URL);
    expect(text()).toContain('This link is incomplete');
  });

  it('an unreachable server is explained in plain words', async () => {
    await create('abc123');
    http.expectOne(URL).error(new ProgressEvent('error'));
    fixture.detectChanges();

    expect(text()).toContain('Could not reach the server');
  });
});
