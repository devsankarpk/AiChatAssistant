import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';

import { environment } from '../../../environments/environment';
import { ResetPasswordComponent } from './reset-password.component';

function createWithToken(token: string | null): ComponentFixture<ResetPasswordComponent> {
  TestBed.configureTestingModule({
    imports: [ResetPasswordComponent],
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      {
        provide: ActivatedRoute,
        useValue: { snapshot: { queryParamMap: convertToParamMap(token ? { token } : {}) } },
      },
    ],
  });
  const fixture = TestBed.createComponent(ResetPasswordComponent);
  fixture.detectChanges();
  return fixture;
}

describe('ResetPasswordComponent', () => {
  let httpMock: HttpTestingController;

  afterEach(() => httpMock?.verify());

  it('shows an "invalid link" state when there is no token in the URL', () => {
    const fixture = createWithToken(null);
    httpMock = TestBed.inject(HttpTestingController);

    expect(fixture.componentInstance.token).toBeNull();
  });

  it('resets the password and shows the success message on submit', () => {
    const fixture = createWithToken('abc123');
    httpMock = TestBed.inject(HttpTestingController);
    const component = fixture.componentInstance;

    component.form.setValue({ newPassword: 'NewPassw0rd!', confirmPassword: 'NewPassw0rd!' });
    component.submit();

    const req = httpMock.expectOne(`${environment.apiBaseUrl}/auth/reset-password`);
    expect(req.request.body).toEqual({ token: 'abc123', newPassword: 'NewPassw0rd!' });
    req.flush({ message: 'Your password has been reset. You can now log in.' });

    expect(component.successMessage()).toContain('reset');
  });

  it('rejects mismatched passwords without submitting', () => {
    const fixture = createWithToken('abc123');
    httpMock = TestBed.inject(HttpTestingController);
    const component = fixture.componentInstance;

    component.form.setValue({ newPassword: 'NewPassw0rd!', confirmPassword: 'Different1!' });
    component.submit();

    expect(component.form.errors?.['passwordMismatch']).toBeTrue();
    httpMock.expectNone(() => true);
  });
});
