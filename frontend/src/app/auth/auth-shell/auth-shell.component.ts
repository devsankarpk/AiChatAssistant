import { Component } from '@angular/core';

@Component({
  selector: 'app-auth-shell',
  template: `
    <div class="auth-shell">
      <div class="form-side">
        <p class="wordmark">AI Chat Assistant</p>
        <div class="form-slot">
          <ng-content />
        </div>
      </div>

      <aside class="sample" aria-label="Example conversation">
        <div class="transcript">
          <div class="turn user">
            <span class="speaker">You</span>
            <p>Make this sound less stiff: "Per our discussion, please find attached the revised schedule."</p>
          </div>
          <div class="turn assistant">
            <span class="speaker">Assistant</span>
            <p>Here's the updated schedule we talked about. Let me know if anything looks off.</p>
          </div>
          <div class="turn user">
            <span class="speaker">You</span>
            <p>Shorter.</p>
          </div>
          <div class="turn assistant">
            <span class="speaker">Assistant</span>
            <p>Updated schedule attached. Anything look off?</p>
          </div>
        </div>
        <p class="sample-note">Every chat is saved, so you can pick one back up where you left off.</p>
      </aside>
    </div>
  `,
  styles: `
    .auth-shell {
      display: grid;
      grid-template-columns: minmax(0, 1fr);
      min-height: 100vh;
    }

    .form-side {
      display: flex;
      flex-direction: column;
      padding: 1.5rem 1rem 3rem;
    }

    .wordmark {
      margin: 0 0 3rem;
      font-family: var(--font-voice);
      font-size: var(--step-1);
      font-weight: 500;
    }

    .form-slot {
      display: flex;
      flex: 1;
      align-items: flex-start;
    }

    .sample {
      display: none;
    }

    @media (min-width: 60rem) {
      .auth-shell {
        grid-template-columns: minmax(24rem, 5fr) 6fr;
      }

      .form-side {
        padding: 2rem 3rem;
      }

      .form-slot {
        align-items: center;
        justify-content: center;
        padding-bottom: 5rem;
      }

      .sample {
        display: flex;
        flex-direction: column;
        justify-content: center;
        gap: 2rem;
        padding: 3rem 4rem;
        background: var(--surface);
        border-left: 1px solid var(--rule);
      }
    }

    .transcript {
      display: flex;
      flex-direction: column;
      gap: 1.5rem;
      max-width: 34rem;
    }

    .turn {
      display: grid;
      grid-template-columns: 6rem minmax(0, 1fr);
      gap: 1rem;

      p {
        margin: 0;
      }
    }

    .speaker {
      padding-top: 0.2rem;
      font-size: var(--step--1);
      color: var(--ink-muted);
    }

    .turn.user .speaker {
      color: var(--accent);
      font-weight: 600;
    }

    .turn.user p {
      font-size: var(--step-0);
    }

    .turn.assistant p {
      font-family: var(--font-voice);
      font-size: var(--step-2);
      line-height: 1.35;
    }

    .sample-note {
      margin: 0 0 0 7rem;
      max-width: 27rem;
      color: var(--ink-muted);
      font-size: var(--step--1);
    }
  `,
})
export class AuthShellComponent {}
