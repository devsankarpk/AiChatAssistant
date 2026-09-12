import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { AuthService } from '../../core/services/auth.service';

/**
 * Blocks a route unless the caller is logged in AND their token carries the Admin role.
 * This is a UI convenience only - the real check is `[Authorize(Roles = "Admin")]` server-side
 * (see CLAUDE.md); a forged/edited client-side claim gets nowhere against the API.
 */
export const adminGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (!auth.isAuthenticated()) {
    return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
  }

  return auth.hasRole('Admin') ? true : router.createUrlTree(['/']);
};
