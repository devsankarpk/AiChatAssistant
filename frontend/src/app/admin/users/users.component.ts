import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Subject, debounceTime, distinctUntilChanged } from 'rxjs';

import { AdminUser } from '../../core/models/admin.models';
import { AdminService } from '../../core/services/admin.service';
import { AuthService } from '../../core/services/auth.service';
import { ThemeService } from '../../core/services/theme.service';
import { extractErrorMessage } from '../../core/utils/http-error';

type PendingAction = { userId: number; kind: 'disable' | 'promote' | 'demote' };

/** Admin only: find people, change their role, disable or enable their account. Nothing here ever shows a password or a message. */
@Component({
  selector: 'app-users',
  imports: [FormsModule, RouterLink, DatePipe],
  templateUrl: './users.component.html',
  styleUrl: './users.component.scss',
})
export class UsersComponent {
  private readonly admin = inject(AdminService);
  private readonly auth = inject(AuthService);
  readonly theme = inject(ThemeService);

  readonly users = signal<AdminUser[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);

  readonly page = signal(1);
  readonly pageSize = 20;
  readonly totalCount = signal(0);
  readonly totalPages = signal(0);
  readonly search = signal('');

  /** The one action waiting for "Confirm" (changing roles and disabling are not one-click). */
  readonly pending = signal<PendingAction | null>(null);
  /** The person whose request is in flight, so a double click cannot send it twice. */
  readonly busyId = signal<number | null>(null);

  private readonly typed = new Subject<string>();

  constructor() {
    this.typed.pipe(debounceTime(250), distinctUntilChanged(), takeUntilDestroyed()).subscribe((term) => {
      this.search.set(term);
      this.page.set(1);
      this.load();
    });
    this.load();
  }

  isMe(user: AdminUser): boolean {
    return user.id === this.auth.user()?.id;
  }

  isAdmin(user: AdminUser): boolean {
    return user.roles.includes('Admin');
  }

  onSearch(term: string): void {
    this.typed.next(term.trim());
  }

  ask(user: AdminUser, kind: PendingAction['kind']): void {
    this.notice.set(null);
    this.pending.set({ userId: user.id, kind });
  }

  cancel(): void {
    this.pending.set(null);
  }

  isPending(user: AdminUser, kind: PendingAction['kind']): boolean {
    const p = this.pending();
    return p?.userId === user.id && p.kind === kind;
  }

  confirm(user: AdminUser): void {
    const action = this.pending();
    if (!action || action.userId !== user.id || this.busyId() !== null) {
      return;
    }
    const request =
      action.kind === 'disable'
        ? this.admin.disableUser(user.id)
        : this.admin.changeRole(user.id, action.kind === 'promote' ? 'Admin' : 'User');
    this.run(user, request, {
      disable: `${user.name} was disabled and signed out.`,
      promote: `${user.name} is now an Admin. They need to log in again to see it.`,
      demote: `${user.name} is now a User.`,
    }[action.kind]);
  }

  enable(user: AdminUser): void {
    if (this.busyId() !== null) {
      return;
    }
    this.run(user, this.admin.enableUser(user.id), `${user.name} can log in again.`);
  }

  prevPage(): void {
    if (this.page() > 1) {
      this.page.update((p) => p - 1);
      this.load();
    }
  }

  nextPage(): void {
    if (this.page() < this.totalPages()) {
      this.page.update((p) => p + 1);
      this.load();
    }
  }

  private run(user: AdminUser, request: ReturnType<AdminService['enableUser']>, success: string): void {
    this.busyId.set(user.id);
    this.error.set(null);
    this.notice.set(null);
    request.subscribe({
      next: (updated) => {
        this.users.update((list) => list.map((u) => (u.id === updated.id ? updated : u)));
        this.pending.set(null);
        this.busyId.set(null);
        this.notice.set(success);
      },
      error: (err: HttpErrorResponse) => {
        this.pending.set(null);
        this.busyId.set(null);
        this.error.set(extractErrorMessage(err));
      },
    });
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.admin.getUsers({ search: this.search(), page: this.page(), pageSize: this.pageSize }).subscribe({
      next: (res) => {
        this.users.set(res.items);
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
