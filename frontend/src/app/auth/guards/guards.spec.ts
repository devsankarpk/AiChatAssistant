import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';

import { AuthService } from '../../core/services/auth.service';
import { adminGuard } from './admin.guard';
import { authGuard } from './auth.guard';

describe('route guards', () => {
  const authenticated = signal(false);
  const roles = signal<string[]>([]);
  const fakeAuth = { isAuthenticated: authenticated, hasRole: (role: string) => roles().includes(role) };

  let router: Router;

  beforeEach(() => {
    authenticated.set(false);
    roles.set([]);
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: fakeAuth }] });
    router = TestBed.inject(Router);
  });

  const run = (guard: typeof authGuard, url: string) =>
    TestBed.runInInjectionContext(() =>
      guard({} as ActivatedRouteSnapshot, { url } as RouterStateSnapshot),
    );

  const asPath = (result: unknown) => router.serializeUrl(result as UrlTree);

  describe('authGuard', () => {
    it('lets a logged-in user through', () => {
      authenticated.set(true);

      expect(run(authGuard, '/messages')).toBeTrue();
    });

    it('sends a logged-out user to /login and remembers where they were going', () => {
      expect(asPath(run(authGuard, '/messages?c=5'))).toBe('/login?returnUrl=%2Fmessages%3Fc%3D5');
    });

    it('treats an expired session (not authenticated) the same way', () => {
      authenticated.set(false);
      roles.set(['Admin']); // a role left in a stale token must not matter

      expect(run(authGuard, '/')).not.toBeTrue();
    });
  });

  describe('adminGuard', () => {
    it('lets an authenticated Admin through', () => {
      authenticated.set(true);
      roles.set(['Admin']);

      expect(run(adminGuard, '/admin/usage')).toBeTrue();
    });

    it('sends a signed-in ordinary user back to the chat, not to the login page', () => {
      authenticated.set(true);
      roles.set(['User']);

      expect(asPath(run(adminGuard, '/admin/usage'))).toBe('/');
    });

    it('sends a logged-out visitor to /login with the return URL', () => {
      expect(asPath(run(adminGuard, '/admin/usage'))).toBe('/login?returnUrl=%2Fadmin%2Fusage');
    });

    it('does not accept an Admin role on an expired session', () => {
      authenticated.set(false);
      roles.set(['Admin']);

      expect(asPath(run(adminGuard, '/admin/usage'))).toContain('/login');
    });

    it('does not treat a role named like Admin as Admin (case matters)', () => {
      authenticated.set(true);
      roles.set(['admin']);

      expect(asPath(run(adminGuard, '/admin/usage'))).toBe('/');
    });
  });
});
