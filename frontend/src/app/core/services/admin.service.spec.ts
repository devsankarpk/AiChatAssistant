import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { environment } from '../../../environments/environment';
import { AdminService } from './admin.service';

const URL = `${environment.apiBaseUrl}/admin/usage`;

describe('AdminService', () => {
  let service: AdminService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(AdminService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('asks GET /admin/usage with page and pageSize', () => {
    service.getUsage({ page: 2, pageSize: 50 }).subscribe();

    const req = http.expectOne((r) => r.url === URL);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.get('pageSize')).toBe('50');
    req.flush({});
  });

  it('leaves the date parameters out when there is no filter', () => {
    service.getUsage({ page: 1, pageSize: 20, from: null, to: undefined }).subscribe();

    const req = http.expectOne((r) => r.url === URL);
    expect(req.request.params.has('from')).toBeFalse();
    expect(req.request.params.has('to')).toBeFalse();
    req.flush({});
  });

  it('leaves out empty-string dates too', () => {
    service.getUsage({ page: 1, pageSize: 20, from: '', to: '' }).subscribe();

    const req = http.expectOne((r) => r.url === URL);
    expect(req.request.params.keys().sort()).toEqual(['page', 'pageSize']);
    req.flush({});
  });

  it('sends from and to when given', () => {
    service.getUsage({ page: 1, pageSize: 20, from: '2026-03-05T00:00:00.000Z', to: '2026-03-10T23:59:59.999Z' }).subscribe();

    const req = http.expectOne((r) => r.url === URL);
    expect(req.request.params.get('from')).toBe('2026-03-05T00:00:00.000Z');
    expect(req.request.params.get('to')).toBe('2026-03-10T23:59:59.999Z');
    req.flush({});
  });

  it('returns the server\'s page as is', () => {
    let result: unknown;
    service.getUsage({ page: 1, pageSize: 20 }).subscribe((r) => (result = r));

    http.expectOne((r) => r.url === URL).flush({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 });

    expect(result).toEqual({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 });
  });

  it('passes a server error through to the caller', () => {
    let status = 0;
    service.getUsage({ page: 1, pageSize: 20 }).subscribe({ error: (e) => (status = e.status) });

    http.expectOne((r) => r.url === URL).flush({ error: { code: 'forbidden', message: 'no' } }, { status: 403, statusText: 'Forbidden' });

    expect(status).toBe(403);
  });

  describe('Phase 26: totals, export and users', () => {
    const ADMIN = `${environment.apiBaseUrl}/admin`;

    it('asks GET /admin/usage/summary with the dates, and leaves empty ones out', () => {
      service.getUsageSummary({ from: '2026-03-05T00:00:00Z', to: null }).subscribe();

      const req = http.expectOne((r) => r.url === `${ADMIN}/usage/summary`);
      expect(req.request.method).toBe('GET');
      expect(req.request.params.get('from')).toBe('2026-03-05T00:00:00Z');
      expect(req.request.params.has('to')).toBeFalse();
      req.flush({});
    });

    it('asks GET /admin/usage/export as a blob', () => {
      service.exportUsage({}).subscribe();

      const req = http.expectOne((r) => r.url === `${ADMIN}/usage/export`);
      expect(req.request.responseType).toBe('blob');
      req.flush(new Blob(['x']));
    });

    it('lists users with paging and an optional search', () => {
      service.getUsers({ page: 2, pageSize: 20, search: 'ada' }).subscribe();
      const withSearch = http.expectOne((r) => r.url === `${ADMIN}/users`);
      expect(withSearch.request.params.get('search')).toBe('ada');
      expect(withSearch.request.params.get('page')).toBe('2');
      withSearch.flush({});

      service.getUsers({ page: 1, pageSize: 20, search: '' }).subscribe();
      const without = http.expectOne((r) => r.url === `${ADMIN}/users`);
      expect(without.request.params.has('search')).toBeFalse();
      without.flush({});
    });

    it('changes a role with PUT and the role name', () => {
      service.changeRole(7, 'Admin').subscribe();

      const req = http.expectOne(`${ADMIN}/users/7/role`);
      expect(req.request.method).toBe('PUT');
      expect(req.request.body).toEqual({ role: 'Admin' });
      req.flush({});
    });

    it('disables and enables with POST', () => {
      service.disableUser(7).subscribe();
      const disable = http.expectOne(`${ADMIN}/users/7/disable`);
      expect(disable.request.method).toBe('POST');
      disable.flush({});

      service.enableUser(7).subscribe();
      const enable = http.expectOne(`${ADMIN}/users/7/enable`);
      expect(enable.request.method).toBe('POST');
      enable.flush({});
    });
  });
});
