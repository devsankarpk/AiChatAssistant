import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { environment } from '../../../environments/environment';
import { UsageLogResponse } from '../../core/models/admin.models';
import { UsageComponent } from './usage.component';

const URL = `${environment.apiBaseUrl}/admin/usage`;

function row(id: number, over: Partial<UsageLogResponse> = {}): UsageLogResponse {
  return {
    id, userId: 1, userName: 'Ada Lovelace', userEmail: 'ada@example.com', sessionId: 's1', sessionTitle: 'Analytical engine',
    tokensUsed: 100, costEstimate: 0, createdAt: '2026-03-05T10:00:00Z', ...over,
  };
}

function page(items: UsageLogResponse[], over: Partial<{ page: number; totalCount: number; totalPages: number }> = {}) {
  return { items, page: 1, pageSize: 20, totalCount: items.length, totalPages: items.length ? 1 : 0, ...over };
}

/** Paging, the date filter, the cost column and errors on the admin usage page. */
describe('UsageComponent flows', () => {
  let fixture: ComponentFixture<UsageComponent>;
  let component: UsageComponent;
  let http: HttpTestingController;

  const el = <T extends Element>(s: string) => (fixture.nativeElement as HTMLElement).querySelector<T>(s);
  const all = (s: string) => Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>(s));
  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';
  const expectRequest = (): TestRequest => http.expectOne((r) => r.url === URL);
  const button = (label: string) => all('button, a').find((b) => b.textContent?.trim() === label) as HTMLButtonElement;

  /** Creates the page and answers its first request. */
  function create(first: object = page([])): TestRequest {
    fixture = TestBed.createComponent(UsageComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    const req = expectRequest();
    req.flush(first);
    fixture.detectChanges();
    return req;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [UsageComponent], providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('asks for page 1, 20 per page and no dates', () => {
    const req = create();

    expect(req.request.params.get('page')).toBe('1');
    expect(req.request.params.get('pageSize')).toBe('20');
    expect(req.request.params.has('from')).toBeFalse();
    expect(req.request.params.has('to')).toBeFalse();
  });

  it('shows who asked, in which chat, and the tokens', () => {
    create(page([row(1, { tokensUsed: 1234 })]));

    const cells = all('tbody tr:first-child td');
    expect(cells[1].querySelector('.user')?.textContent).toBe('Ada Lovelace');
    expect(cells[1].querySelector('.email')?.textContent).toBe('ada@example.com');
    expect(cells[2].textContent?.trim()).toBe('Analytical engine');
    expect(cells[3].textContent?.trim()).toBe('1,234');
  });

  describe('cost column', () => {
    it('says "Not tracked" (not $0.00) while cost is zero', () => {
      create(page([row(1, { costEstimate: 0 })]));

      expect(el('tbody tr td.num:last-child')?.textContent).toContain('Not tracked');
      expect(text()).not.toContain('$0.00');
    });

    it('shows money once a cost exists', () => {
      create(page([row(1, { costEstimate: 0.5 }), row(2, { costEstimate: 0 })]));

      const cells = all('tbody tr td.num:last-child').map((c) => c.textContent?.trim());
      expect(cells[0]).toBe('$0.50');
      expect(cells[1]).toBe('Not tracked');
    });
  });

  describe('empty and loading', () => {
    it('shows the hint when nothing matches, and no summary tiles', () => {
      create(page([]));

      expect(text()).toContain('No replies in this date range');
      expect(el('.stats')).toBeNull();
    });

    it('does not show the empty hint while the first load is still running', () => {
      fixture = TestBed.createComponent(UsageComponent);
      fixture.detectChanges();

      expect(text()).not.toContain('No replies in this date range');
      expectRequest().flush(page([]));
    });
  });

  describe('summary and chart', () => {
    it('shows the reply count for the range and the tokens on this page', () => {
      create(page([row(1, { tokensUsed: 10 }), row(2, { tokensUsed: 30 })], { totalCount: 57, totalPages: 3 }));

      const tiles = all('.stat').map((s) => [s.querySelector('.stat-label')?.textContent, s.querySelector('.stat-value')?.textContent]);
      expect(tiles).toEqual([['Replies in range', '57'], ['Tokens on this page', '40']]);
    });

    it('draws the per-day chart only when the page spans more than one day', () => {
      create(page([row(1), row(2)]));
      expect(el('.chart')).toBeNull();
    });

    it('adds up tokens per day, oldest day first', () => {
      create(page([
        row(1, { tokensUsed: 5, createdAt: '2026-03-06T08:00:00Z' }),
        row(2, { tokensUsed: 7, createdAt: '2026-03-05T08:00:00Z' }),
        row(3, { tokensUsed: 3, createdAt: '2026-03-06T20:00:00Z' }),
      ]));

      expect(component.dailyTotals()).toEqual([{ day: '2026-03-05', tokens: 7 }, { day: '2026-03-06', tokens: 8 }]);
      expect(el('.chart')).not.toBeNull();
      expect(all('.bar-col').length).toBe(2);
    });
  });

  describe('paging', () => {
    const second = () => page([row(21)], { page: 2, totalCount: 41, totalPages: 3 });

    it('on the first page Previous is disabled, Next is enabled, and the label says which page', () => {
      create(page([row(1)], { totalCount: 41, totalPages: 3 }));

      expect(button('Previous').disabled).toBeTrue();
      expect(button('Next').disabled).toBeFalse();
      expect(el('.pager')?.textContent).toContain('Page 1 of 3');
      expect(el('.pager')?.textContent).toContain('41 replies');
    });

    it('Next asks for page 2 and shows it', () => {
      create(page([row(1)], { totalCount: 41, totalPages: 3 }));

      button('Next').click();
      const req = expectRequest();
      expect(req.request.params.get('page')).toBe('2');
      req.flush(second());
      fixture.detectChanges();

      expect(component.page()).toBe(2);
      expect(button('Previous').disabled).toBeFalse();
      expect(el('.pager')?.textContent).toContain('Page 2 of 3');
    });

    it('Previous goes back one page', () => {
      create(page([row(1)], { totalCount: 41, totalPages: 3 }));
      button('Next').click();
      expectRequest().flush(second());
      fixture.detectChanges();

      button('Previous').click();

      expect(expectRequest().request.params.get('page')).toBe('1');
    });

    it('on the last page Next is disabled, and asking for more does nothing', () => {
      create(page([row(1)], { totalCount: 5, totalPages: 1 }));

      expect(button('Next').disabled).toBeTrue();
      component.nextPage();
      component.prevPage();
      http.expectNone((r) => r.url === URL);
    });

    it('with no results the pager reads "Page 1 of 1, 0 replies" and both buttons are off', () => {
      create(page([]));

      expect(el('.pager')?.textContent).toContain('Page 1 of 1');
      expect(button('Previous').disabled).toBeTrue();
      expect(button('Next').disabled).toBeTrue();
    });
  });

  describe('date filter', () => {
    it('sends the start of the "from" day and the end of the "to" day', () => {
      create();
      component.fromDate.set('2026-03-05');
      component.toDate.set('2026-03-10');

      component.applyFilter();
      const req = expectRequest();

      expect(req.request.params.get('from')).toBe(new Date('2026-03-05').toISOString());
      expect(req.request.params.get('to')).toBe(new Date('2026-03-10T23:59:59.999').toISOString());
      req.flush(page([]));
    });

    it('works with only one end', () => {
      create();
      component.fromDate.set('2026-03-05');

      component.applyFilter();
      const req = expectRequest();

      expect(req.request.params.has('from')).toBeTrue();
      expect(req.request.params.has('to')).toBeFalse();
      req.flush(page([]));
    });

    it('goes back to page 1 when a filter is applied from a later page', () => {
      create(page([row(1)], { totalCount: 41, totalPages: 3 }));
      button('Next').click();
      expectRequest().flush(page([row(21)], { page: 2, totalCount: 41, totalPages: 3 }));

      component.fromDate.set('2026-03-05');
      component.applyFilter();
      const req = expectRequest();

      expect(req.request.params.get('page')).toBe('1');
      req.flush(page([]));
    });

    it('"Clear dates" empties both dates and reloads without them', () => {
      create();
      component.fromDate.set('2026-03-05');
      component.toDate.set('2026-03-10');

      component.clearFilter();
      const req = expectRequest();

      expect(component.fromDate()).toBe('');
      expect(component.toDate()).toBe('');
      expect(req.request.params.has('from')).toBeFalse();
      expect(req.request.params.has('to')).toBeFalse();
      req.flush(page([]));
    });

    it('submitting the form applies the filter', () => {
      create();
      component.fromDate.set('2026-03-05');

      (el('form.filter') as HTMLFormElement).dispatchEvent(new Event('submit'));

      expect(expectRequest().request.params.has('from')).toBeTrue();
    });
  });

  describe('errors', () => {
    it('shows the server\'s message and stops loading', () => {
      fixture = TestBed.createComponent(UsageComponent);
      component = fixture.componentInstance;
      fixture.detectChanges();

      expectRequest().flush({ error: { code: 'validation_error', message: "'from' must not be after 'to'." } }, { status: 400, statusText: 'Bad Request' });
      fixture.detectChanges();

      expect(component.loading()).toBeFalse();
      expect(el('.form-error')?.textContent).toContain("'from' must not be after 'to'.");
    });

    it('explains an unreachable server', () => {
      fixture = TestBed.createComponent(UsageComponent);
      component = fixture.componentInstance;
      fixture.detectChanges();

      expectRequest().error(new ProgressEvent('error'));
      fixture.detectChanges();

      expect(el('.form-error')?.textContent).toContain('Could not reach the server');
    });

    it('clears an earlier error when the next load works', () => {
      fixture = TestBed.createComponent(UsageComponent);
      component = fixture.componentInstance;
      fixture.detectChanges();
      expectRequest().flush({}, { status: 500, statusText: 'x' });
      fixture.detectChanges();
      expect(el('.form-error')).not.toBeNull();

      component.applyFilter();
      expectRequest().flush(page([row(1)]));
      fixture.detectChanges();

      expect(el('.form-error')).toBeNull();
      expect(text()).toContain('Ada Lovelace');
    });
  });
});
