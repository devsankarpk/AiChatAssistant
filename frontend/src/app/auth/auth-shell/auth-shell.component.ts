import { Component } from '@angular/core';

/** The page every auth form sits in: a centred card on the soft page background. */
@Component({
  selector: 'app-auth-shell',
  template: `
    <div class="auth-shell">
      <p class="brand">AI Chat Assistant</p>
      <div class="card auth-panel">
        <ng-content />
      </div>
      <p class="tagline">Every chat is saved, so you can pick one back up where you left off.</p>
    </div>
  `,
  styles: `
    .auth-shell {
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      gap: 1.5rem;
      min-height: 100vh;
      padding: 2rem 1rem;
    }

    .auth-panel {
      width: 100%;
      max-width: 26rem;
      padding: 2rem;
      box-shadow: var(--shadow-md);
    }

    .tagline {
      margin: 0;
      max-width: 26rem;
      color: var(--text-muted);
      font-size: var(--step--1);
      text-align: center;
      text-wrap: balance;
    }

    @media (max-width: 30rem) {
      .auth-panel {
        padding: 1.5rem 1.25rem;
      }
    }
  `,
})
export class AuthShellComponent {}
