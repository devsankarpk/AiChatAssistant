import { Component, inject } from '@angular/core';
import { Router, RouterLink } from '@angular/router';

import { AuthService } from '../core/services/auth.service';
import { ThemeService } from '../core/services/theme.service';
import { TopNavComponent } from '../shared/top-nav/top-nav.component';

/**
 * Placeholder for doctor-appointment booking. Real booking (doctors, slots, bookings) needs its
 * own plan; until then this page only says so. See README, "Ideas not planned yet".
 */
@Component({
  selector: 'app-appointments',
  imports: [RouterLink, TopNavComponent],
  template: `
    <app-top-nav>
      <button type="button" class="btn btn-quiet" [attr.aria-pressed]="theme.theme() === 'dark'" (click)="theme.toggle()">
        {{ theme.theme() === 'dark' ? 'Light theme' : 'Dark theme' }}
      </button>
      <button type="button" class="btn btn-quiet" (click)="logout()">Log out</button>
    </app-top-nav>

    <main class="page-body">
      <section class="card soon">
        <h1>Doctor appointments are coming soon</h1>
        <p class="muted">You'll be able to pick a doctor and book a time right here. We're still building it.</p>
        <a class="btn btn-primary" routerLink="/">Back to the chatbot</a>
      </section>
    </main>
  `,
  styles: `
    .page-body {
      display: flex;
      justify-content: center;
      padding: 4rem 1rem;
    }

    .soon {
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 0.75rem;
      width: 100%;
      max-width: 30rem;
      padding: 2.5rem 2rem;
      text-align: center;
      box-shadow: var(--shadow-md);

      h1 {
        margin: 0;
        font-size: var(--step-2);
      }

      p {
        margin: 0 0 0.75rem;
      }

      a.btn {
        text-decoration: none;
      }
    }
  `,
})
export class AppointmentsComponent {
  readonly theme = inject(ThemeService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  logout(): void {
    this.auth.logout();
    this.router.navigateByUrl('/login');
  }
}
