import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { environment } from '../../environments/environment';
import { errorInterceptor } from './error.interceptor';
import { AuthService } from './services/auth.service';
import { LoadingService } from './services/loading.service';
import { ToastService } from './services/toast.service';

describe('errorInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let loading: LoadingService;
  let toast: ToastService;
  let auth: AuthService;
  let router: Router;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([errorInterceptor])), provideHttpClientTesting(), provideRouter([])],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
    loading = TestBed.inject(LoadingService);
    toast = TestBed.inject(ToastService);
    auth = TestBed.inject(AuthService);
    router = TestBed.inject(Router);
  });

  afterEach(() => {
    httpMock.verify();
    localStorage.clear();
  });

  it('tracks LoadingService around a request, on both success and failure', () => {
    http.get(`${environment.apiBaseUrl}/chat/sessions`).subscribe({ error: () => {} });
    expect(loading.isLoading()).toBeTrue();
    httpMock.expectOne(`${environment.apiBaseUrl}/chat/sessions`).flush([]);
    expect(loading.isLoading()).toBeFalse();

    http.get(`${environment.apiBaseUrl}/chat/sessions`).subscribe({ error: () => {} });
    expect(loading.isLoading()).toBeTrue();
    httpMock.expectOne(`${environment.apiBaseUrl}/chat/sessions`).flush(null, { status: 500, statusText: 'Error' });
    expect(loading.isLoading()).toBeFalse();
  });

  it('toasts and re-throws on an unexpected 500', () => {
    let sawError = false;
    http.get(`${environment.apiBaseUrl}/chat/sessions`).subscribe({ error: () => (sawError = true) });

    httpMock
      .expectOne(`${environment.apiBaseUrl}/chat/sessions`)
      .flush({ error: { code: 'internal_error', message: 'Boom' } }, { status: 500, statusText: 'Error' });

    expect(sawError).toBeTrue();
    expect(toast.toasts().length).toBe(1);
    expect(toast.toasts()[0].message).toBe('Boom');
  });

  it('does not toast a well-handled 503 (chat has its own inline UI for this)', () => {
    http.get(`${environment.apiBaseUrl}/chat/sessions`).subscribe({ error: () => {} });

    httpMock.expectOne(`${environment.apiBaseUrl}/chat/sessions`).flush(
      { error: { code: 'ai_service_unavailable', message: 'AI is down' } },
      { status: 503, statusText: 'Service Unavailable' },
    );

    expect(toast.toasts().length).toBe(0);
  });

  it('does not toast a plain validation 400', () => {
    http.post(`${environment.apiBaseUrl}/chat/sessions`, {}).subscribe({ error: () => {} });

    httpMock
      .expectOne(`${environment.apiBaseUrl}/chat/sessions`)
      .flush({ error: { code: 'validation_error', message: 'Bad input' } }, { status: 400, statusText: 'Bad Request' });

    expect(toast.toasts().length).toBe(0);
  });

  it('on a stale-session "unauthorized" error, logs out, toasts, and redirects to /login', () => {
    localStorage.setItem('aichat.auth.token', 'stale-token');
    const navigateSpy = spyOn(router, 'navigateByUrl');

    http.get(`${environment.apiBaseUrl}/chat/sessions`).subscribe({ error: () => {} });

    httpMock
      .expectOne(`${environment.apiBaseUrl}/chat/sessions`)
      .flush({ error: { code: 'unauthorized', message: 'Authentication is required.' } }, { status: 401, statusText: 'Unauthorized' });

    expect(auth.getToken()).toBeNull();
    expect(toast.toasts().length).toBe(1);
    expect(navigateSpy).toHaveBeenCalledWith('/login');
  });

  it('leaves a request to a different host alone entirely (no loading, no interception)', () => {
    http.get('https://third-party.example.com/data').subscribe({ error: () => {} });
    expect(loading.isLoading()).toBeFalse();
    httpMock.expectOne('https://third-party.example.com/data').flush({});
  });
});
