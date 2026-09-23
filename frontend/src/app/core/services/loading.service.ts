import { Injectable, computed, signal } from '@angular/core';

/** A simple in-flight-request counter, driven by errorInterceptor, for a global "something's
 * loading" indicator. Counter (not a boolean) so two overlapping requests don't have the second
 * one's completion prematurely turn the indicator off while the first is still in flight. */
@Injectable({ providedIn: 'root' })
export class LoadingService {
  private readonly activeRequests = signal(0);

  readonly isLoading = computed(() => this.activeRequests() > 0);

  start(): void {
    this.activeRequests.update((n) => n + 1);
  }

  stop(): void {
    this.activeRequests.update((n) => Math.max(0, n - 1));
  }
}
