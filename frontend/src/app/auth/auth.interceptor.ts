import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';

import { environment } from '../../environments/environment';
import { ApiErrorResponse } from '../core/models/api-error.model';
import { AuthService } from '../core/services/auth.service';

const withToken = (req: HttpRequest<unknown>, token: string) => req.clone({ setHeaders: { Authorization: `Bearer ${token}` } });

/**
 * Attaches `Authorization: Bearer <token>` to requests aimed at our own API.
 * Scoped to `environment.apiBaseUrl` so the token is never sent to a third-party
 * host some later feature might call.
 *
 * When the API answers `401 unauthorized` (the short-lived access token has lapsed) and a refresh token exists, it renews the pair once
 * and repeats the request once with the new token. Requests to /auth/* are never retried (a failing refresh must not loop), and if the
 * renewal fails the original 401 goes on to the error interceptor, which signs the person out.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const token = auth.getToken();

  if (!req.url.startsWith(environment.apiBaseUrl)) {
    return next(req);
  }

  const outgoing = token ? withToken(req, token) : req;
  const retryable = !req.url.startsWith(`${environment.apiBaseUrl}/auth/`);

  return next(outgoing).pipe(
    catchError((err: HttpErrorResponse) => {
      const code = (err.error as ApiErrorResponse | undefined)?.error?.code;
      if (err.status !== 401 || code !== 'unauthorized' || !retryable || !auth.hasRefreshToken()) {
        return throwError(() => err);
      }
      return auth.refresh().pipe(
        // The renewal itself failed: report the original 401, not the renewal's.
        catchError(() => throwError(() => err)),
        switchMap((renewed) => next(withToken(req, renewed.token))),
      );
    }),
  );
};
