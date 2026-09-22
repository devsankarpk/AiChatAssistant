import { CurrencyPipe, DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';

import { AdminService } from '../../core/services/admin.service';
import { UsageLogResponse } from '../../core/models/admin.models';
import { extractErrorMessage } from '../../core/utils/http-error';

interface DayTotal {
  day: string;
  tokens: number;
}

@Component({
  selector: 'app-usage',
  imports: [FormsModule, RouterLink, DatePipe, CurrencyPipe],
  templateUrl: './usage.component.html',
  styleUrl: './usage.component.scss',
})
export class UsageComponent {
  private readonly admin = inject(AdminService);

  readonly items = signal<UsageLogResponse[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly page = signal(1);
  readonly pageSize = signal(20);
  readonly totalCount = signal(0);
  readonly totalPages = signal(0);

  // <input type="date"> values (yyyy-MM-dd), empty = no filter.
  readonly fromDate = signal('');
  readonly toDate = signal('');

  /** Tokens used per day, across the *currently loaded page* only - a page-scoped glance, not a
   * full aggregate query (Phase 7 marks this chart optional; a proper all-time aggregate would be
   * a separate backend endpoint, not something to compute by paging through everything client-side). */
  readonly dailyTotals = computed<DayTotal[]>(() => {
    const totals = new Map<string, number>();
    for (const item of this.items()) {
      const day = item.createdAt.slice(0, 10);
      totals.set(day, (totals.get(day) ?? 0) + item.tokensUsed);
    }
    return Array.from(totals, ([day, tokens]) => ({ day, tokens })).sort((a, b) => a.day.localeCompare(b.day));
  });

  readonly maxDailyTokens = computed(() => Math.max(1, ...this.dailyTotals().map((d) => d.tokens)));

  constructor() {
    this.fetch();
  }

  applyFilter(): void {
    this.page.set(1);
    this.fetch();
  }

  clearFilter(): void {
    this.fromDate.set('');
    this.toDate.set('');
    this.applyFilter();
  }

  prevPage(): void {
    if (this.page() > 1) {
      this.page.update((p) => p - 1);
      this.fetch();
    }
  }

  nextPage(): void {
    if (this.page() < this.totalPages()) {
      this.page.update((p) => p + 1);
      this.fetch();
    }
  }

  private fetch(): void {
    this.loading.set(true);
    this.error.set(null);
    this.admin
      .getUsage({
        page: this.page(),
        pageSize: this.pageSize(),
        from: this.fromDate() ? new Date(this.fromDate()).toISOString() : null,
        // Include the whole "to" day, not just its midnight.
        to: this.toDate() ? new Date(`${this.toDate()}T23:59:59.999`).toISOString() : null,
      })
      .subscribe({
        next: (res) => {
          this.items.set(res.items);
          this.totalCount.set(res.totalCount);
          this.totalPages.set(res.totalPages);
          this.loading.set(false);
        },
        error: (err: HttpErrorResponse) => {
          this.loading.set(false);
          this.error.set(extractErrorMessage(err));
        },
      });
  }
}
