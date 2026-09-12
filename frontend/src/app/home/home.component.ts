import { Component, inject } from '@angular/core';
import { Router, RouterLink } from '@angular/router';

import { AuthService } from '../core/services/auth.service';

/**
 * Stand-in landing page: proves the auth flow works end to end (login persists
 * on refresh, logout clears it, roles decoded from the JWT drive the Admin link).
 * The real chat UI replaces this in Phase 6.
 */
@Component({
  selector: 'app-home',
  imports: [RouterLink],
  templateUrl: './home.component.html',
  styleUrl: './home.component.scss',
})
export class HomeComponent {
  private readonly router = inject(Router);

  readonly auth = inject(AuthService);

  logout(): void {
    this.auth.logout();
    this.router.navigateByUrl('/login');
  }
}
