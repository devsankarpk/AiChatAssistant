import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';

import { environment } from '../../environments/environment';
import { AuthService } from '../core/services/auth.service';

/**
 * Attaches `Authorization: Bearer <token>` to requests aimed at our own API.
 * Scoped to `environment.apiBaseUrl` so the token is never sent to a third-party
 * host some later feature might call.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const token = inject(AuthService).getToken();

  if (!token || !req.url.startsWith(environment.apiBaseUrl)) {
    return next(req);
  }

  return next(
    req.clone({
      setHeaders: { Authorization: `Bearer ${token}` },
    }),
  );
};
