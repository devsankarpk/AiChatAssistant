import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';

export type Theme = 'light' | 'dark';

const THEME_KEY = 'aichat.theme';

/**
 * Light is the default regardless of the OS setting; dark is opt-in and remembered per browser.
 * Storage is a per-viewer convenience only, so every access is wrapped: the app must render
 * correctly when localStorage is blocked or throws.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly root = inject(DOCUMENT).documentElement;
  readonly theme = signal<Theme>(readStoredTheme());

  constructor() {
    this.apply(this.theme());
  }

  toggle(): void {
    const next: Theme = this.theme() === 'dark' ? 'light' : 'dark';
    this.theme.set(next);
    this.apply(next);
    try {
      localStorage.setItem(THEME_KEY, next);
    } catch {
      // Blocked storage: the choice just won't survive a reload.
    }
  }

  private apply(theme: Theme): void {
    if (theme === 'dark') {
      this.root.setAttribute('data-theme', 'dark');
    } else {
      this.root.removeAttribute('data-theme');
    }
  }
}

function readStoredTheme(): Theme {
  try {
    return localStorage.getItem(THEME_KEY) === 'dark' ? 'dark' : 'light';
  } catch {
    return 'light';
  }
}
