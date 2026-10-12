import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';

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
}
