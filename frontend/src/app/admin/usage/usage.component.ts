import { CurrencyPipe, DatePipe, DecimalPipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';

import { AdminService } from '../../core/services/admin.service';
import { ThemeService } from '../../core/services/theme.service';
import { UsageLogResponse, UsageSummary } from '../../core/models/admin.models';
import { extractErrorMessage } from '../../core/utils/http-error';

@Component({
  selector: 'app-usage',
  imports: [FormsModule, RouterLink, DatePipe, CurrencyPipe, DecimalPipe],
  templateUrl: './usage.component.html',
  styleUrl: './usage.component.scss',
})
export class UsageComponent {
  private readonly admin = inject(AdminService);
  readonly theme = inject(ThemeService);

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

  /** Totals, top spenders and the per-day series for the whole date range, worked out by the server (not by this page of rows). */
  readonly summary = signal<UsageSummary | null>(null);
  readonly summaryError = signal<string | null>(null);

  readonly maxDayTokens = computed(() => Math.max(1, ...(this.summary()?.byDay ?? []).map((d) => d.tokens)));

  readonly exporting = signal(false);

  constructor() {
    this.fetch();
    this.loadSummary();
  }

  /** A row written before cost tracking existed has no model and no cost: say so rather than showing $0.00. */
  isTracked(item: UsageLogResponse): boolean {
    return item.costEstimate > 0 || item.model !== '';
  }

  applyFilter(): void {
    this.page.set(1);
    this.fetch();
    this.loadSummary();
  }

  /** Downloads the rows in the date range as a CSV file. */
  exportCsv(): void {
    this.exporting.set(true);
    this.error.set(null);
    this.admin.exportUsage(this.range()).subscribe({
      next: (blob) => {
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = `usage-${new Date().toISOString().slice(0, 10)}.csv`;
        link.click();
        URL.revokeObjectURL(url);
        this.exporting.set(false);
      },
      error: (err: HttpErrorResponse) => {
        this.exporting.set(false);
        this.error.set(extractErrorMessage(err));
      },
    });
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

  private range(): { from: string | null; to: string | null } {
    return {
      from: this.fromDate() ? new Date(this.fromDate()).toISOString() : null,
      // Include the whole "to" day, not just its midnight.
      to: this.toDate() ? new Date(`${this.toDate()}T23:59:59.999`).toISOString() : null,
    };
  }

  private loadSummary(): void {
    this.summaryError.set(null);
    this.admin.getUsageSummary(this.range()).subscribe({
      next: (summary) => this.summary.set(summary),
      error: (err: HttpErrorResponse) => this.summaryError.set(extractErrorMessage(err)),
    });
  }

  private fetch(): void {
    this.loading.set(true);
    this.error.set(null);
    this.admin
      .getUsage({ page: this.page(), pageSize: this.pageSize(), ...this.range() })
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
