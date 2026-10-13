import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

/**
 * The bar across the top of the signed-in pages: the product mark and the main sections
 * (today only Chatbot; Messages is added in Phase 12). Book appointment is a pinned row at the top of
 * the chat sidebar, not a tab here. A page may project extra controls into the right-hand end
 * (`<app-top-nav><button>...</button></app-top-nav>`); the chat page projects nothing because
 * its sidebar already carries the account controls.
 */
@Component({
  selector: 'app-top-nav',
  imports: [RouterLink, RouterLinkActive],
  template: `
    <header class="top-nav">
      <a class="brand" routerLink="/" aria-label="AI Chat Assistant, home">
        <span class="brand-text">AI Chat Assistant</span>
      </a>

      <nav class="tabs" aria-label="Main">
        <!-- The chat URL carries ?session=<id>, so ignore query params or the tab would go inactive. -->
        <a
          routerLink="/"
          routerLinkActive="active"
          #chatbot="routerLinkActive"
          [routerLinkActiveOptions]="{ paths: 'exact', queryParams: 'ignored', fragment: 'ignored', matrixParams: 'ignored' }"
          [attr.aria-current]="chatbot.isActive ? 'page' : null"
          >Chatbot</a
        >
      </nav>

      <div class="nav-end"><ng-content /></div>
    </header>
  `,
  styles: `
    .top-nav {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: 0.25rem 1.25rem;
      min-height: 3.25rem;
      padding: 0 1rem;
      border-bottom: 1px solid var(--border);
      background: var(--surface);
    }

    a.brand {
      color: var(--text);
      text-decoration: none;
    }

    .tabs {
      display: flex;
      gap: 0.25rem;
    }

    .tabs a {
      padding: 0.4rem 0.8rem;
      border-radius: var(--radius-sm);
      color: var(--text-muted);
      font-weight: 500;
      text-decoration: none;

      &:hover {
        background: var(--surface-muted);
        color: var(--text);
      }

      &.active {
        background: var(--accent-soft);
        color: var(--accent);
      }
    }

    .nav-end {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      margin-left: auto;
    }

    /* The chat page projects nothing, so don't let an empty slot take space. */
    .nav-end:empty {
      display: none;
    }

    @media (max-width: 40rem) {
      .top-nav {
        gap: 0.25rem 0.75rem;
        padding: 0.4rem 0.75rem;
      }

      /* Narrow screens: tabs stay on the first row, projected controls get their own row. */
      .nav-end {
        order: 3;
        flex-basis: 100%;
        justify-content: flex-end;
      }

      .brand-text {
        position: absolute;
        width: 1px;
        height: 1px;
        overflow: hidden;
        clip: rect(0 0 0 0);
        white-space: nowrap;
      }
    }
  `,
})
export class TopNavComponent {}
