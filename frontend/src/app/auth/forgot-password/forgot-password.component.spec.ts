import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { environment } from '../../../environments/environment';
import { ForgotPasswordComponent } from './forgot-password.component';

describe('ForgotPasswordComponent', () => {
  let component: ForgotPasswordComponent;
  let fixture: ComponentFixture<ForgotPasswordComponent>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ForgotPasswordComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ForgotPasswordComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => httpMock.verify());

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('shows the generic confirmation message on success, regardless of whether the email exists', () => {
    component.form.setValue({ email: 'anyone@example.com' });
    component.submit();

    httpMock
      .expectOne(`${environment.apiBaseUrl}/auth/forgot-password`)
      .flush({ message: 'If that email is registered, a password reset link has been sent.' });

    expect(component.submittedMessage()).toContain('If that email is registered');
  });

  it('does not submit an invalid email', () => {
    component.form.setValue({ email: 'not-an-email' });
    component.submit();
    expect(component.submitting()).toBeFalse();
    httpMock.expectNone(() => true);
  });
});
