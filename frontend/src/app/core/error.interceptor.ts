import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { finalize, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';

import { environment } from '../../environments/environment';
import { ApiErrorResponse } from './models/api-error.model';
import { AuthService } from './services/auth.service';
import { LoadingService } from './services/loading.service';
import { ToastService } from './services/toast.service';
import { extractErrorMessage } from './utils/http-error';

/**
 * Global HTTP cross-cutting concerns, layered on top of (never replacing) each component's own
 * error handling:
 *  - drives LoadingService for every request to our API, for a page-wide loading indicator
 *  - auto-logs-out and redirects to /login on a stale/invalid token (error code "unauthorized" -
 *    see AuthController's OnChallenge handler in Program.cs) - a component showing that inline
 *    as plain text would just be a dead end for the user, since nothing short of logging back in
 *    fixes it
 *  - toasts truly unexpected failures (network unreachable, an uncaught 5xx) globally; leaves
 *    everything else (validation, 401 on login itself, 404, the chat-specific 503 handling, ...)
 *    to the component that already has better, more specific UI for it - toasting those too
 *    would just be a redundant second notification for the same thing
 */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  if (!req.url.startsWith(environment.apiBaseUrl)) {
    return next(req);
  }

  const loading = inject(LoadingService);
  const toast = inject(ToastService);
  const auth = inject(AuthService);
  const router = inject(Router);

  loading.start();

  return next(req).pipe(
    catchError((err: HttpErrorResponse) => {
      const code = (err.error as ApiErrorResponse | undefined)?.error?.code;

      if (code === 'unauthorized') {
        auth.logout();
        toast.show('Your session has expired. Please log in again.', 'error');
        router.navigateByUrl('/login');
      } else if (err.status === 0 || err.status === 500 || err.status === 502) {
        toast.show(extractErrorMessage(err), 'error');
      }

      return throwError(() => err);
    }),
    finalize(() => loading.stop()),
  );
};
