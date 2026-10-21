import { Component, effect, inject, untracked } from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { AuthService } from './core/services/auth.service';
import { DirectStateService } from './core/services/direct-state.service';
import { LoadingService } from './core/services/loading.service';
import { ThemeService } from './core/services/theme.service';
import { ToastService } from './core/services/toast.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss',
})
export class AppComponent {
  readonly loading = inject(LoadingService);
  readonly toast = inject(ToastService);
  // Injected only so the stored theme is applied on every route, including the auth pages.
  private readonly theme = inject(ThemeService);
  private readonly auth = inject(AuthService);
  private readonly direct = inject(DirectStateService);

  constructor() {
    // One live connection for the whole signed-in session (not per page), so the unread badge and
    // message toasts work everywhere. Signing out drops it and clears the previous user's chats.
    effect(() => {
      const signedIn = this.auth.isAuthenticated();
      untracked(() => (signedIn ? this.direct.start() : this.direct.stop()));
    });
  }
}
