import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { environment } from '../../../environments/environment';
import { AdminUser } from '../../core/models/admin.models';
import { AuthService } from '../../core/services/auth.service';
import { UsersComponent } from './users.component';

const BASE = `${environment.apiBaseUrl}/admin`;
const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

function person(id: number, over: Partial<AdminUser> = {}): AdminUser {
  return { id, name: `Person ${id}`, email: `p${id}@example.com`, roles: ['User'], createdAt: '2026-11-01T10:00:00Z', lastSeenAt: null, disabledAt: null, ...over };
}

function pageOf(items: AdminUser[], over: object = {}) {
  return { items, page: 1, pageSize: 20, totalCount: items.length, totalPages: items.length ? 1 : 0, ...over };
}

/** The admin Users page, with the API answered by hand. */
describe('UsersComponent', () => {
  let fixture: ComponentFixture<UsersComponent>;
  let component: UsersComponent;
  let http: HttpTestingController;

  const root = () => fixture.nativeElement as HTMLElement;
  const el = <T extends Element>(s: string) => root().querySelector<T>(s);
  const all = <T extends Element>(s: string) => Array.from(root().querySelectorAll<T>(s));
  const text = () => root().textContent ?? '';
  const button = (label: string, within: ParentNode = root()) =>
    Array.from(within.querySelectorAll<HTMLButtonElement>('button')).find((b) => b.textContent?.trim() === label);
  const rowOf = (id: number) => all<HTMLElement>('tbody tr').find((r) => r.textContent?.includes(`p${id}@example.com`))!;
  const listRequest = (): TestRequest => http.expectOne((r) => r.url === `${BASE}/users`);

  async function create(first = pageOf([person(1), person(2)]), me = 1): Promise<void> {
    await TestBed.configureTestingModule({
      imports: [UsersComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: AuthService, useValue: { user: () => ({ id: me, name: 'Me', email: 'me@example.com', roles: ['Admin'] }) } },
      ],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(UsersComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    listRequest().flush(first);
    fixture.detectChanges();
  }

  afterEach(() => http.verify());

  it('asks for page 1 with 20 people and no search, and lists them with role and status', async () => {
    await create(pageOf([person(1, { roles: ['Admin'] }), person(2, { disabledAt: '2026-11-02T10:00:00Z' })]));

    expect(all('tbody tr').length).toBe(2);
    expect(rowOf(1).textContent).toContain('Admin');
    expect(rowOf(1).textContent).toContain('Active');
    expect(rowOf(2).textContent).toContain('Disabled');
    expect(rowOf(2).textContent).toContain('Never'); // never seen
  });

  it('first request has the paging parameters', async () => {
    await TestBed.configureTestingModule({ imports: [UsersComponent], providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), { provide: AuthService, useValue: { user: () => null } }] }).compileComponents();
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(UsersComponent);
    fixture.detectChanges();

    const req = listRequest();

    expect(req.request.params.get('page')).toBe('1');
    expect(req.request.params.get('pageSize')).toBe('20');
    expect(req.request.params.has('search')).toBeFalse();
    req.flush(pageOf([]));
  });

  it('gives your own row no actions', async () => {
    await create(pageOf([person(1), person(2)]), 1);

    expect(rowOf(1).textContent).toContain('(you)');
    expect(rowOf(1).textContent).toContain('Not for your own account');
    expect(button('Disable', rowOf(1))).toBeUndefined();
    expect(button('Make admin', rowOf(1))).toBeUndefined();
    expect(button('Disable', rowOf(2))).toBeDefined();
  });

  it('searches after a short pause, from page 1, with the typed text', async () => {
    await create();
    component.page.set(3);

    component.onSearch('  ada ');
    await wait(300);
    const req = listRequest();

    expect(req.request.params.get('search')).toBe('ada');
    expect(req.request.params.get('page')).toBe('1');
    req.flush(pageOf([person(5)]));
  });

  it('sends one request for a burst of typing', async () => {
    await create();

    component.onSearch('a');
    component.onSearch('ad');
    component.onSearch('ada');
    await wait(300);

    listRequest().flush(pageOf([]));
  });

  describe('disabling', () => {
    it('asks first, and does nothing until Confirm', async () => {
      await create();

      button('Disable', rowOf(2))!.click();
      fixture.detectChanges();

      expect(rowOf(2).textContent).toContain('Disable and sign out Person 2?');
      http.expectNone((r) => r.url.includes('/disable'));
    });

    it('Cancel puts the buttons back and sends nothing', async () => {
      await create();
      button('Disable', rowOf(2))!.click();
      fixture.detectChanges();

      button('Cancel', rowOf(2))!.click();
      fixture.detectChanges();

      expect(button('Disable', rowOf(2))).toBeDefined();
      http.expectNone((r) => r.url.includes('/disable'));
    });

    it('Confirm disables, updates the row in place, and says so', async () => {
      await create();
      button('Disable', rowOf(2))!.click();
      fixture.detectChanges();

      button('Confirm', rowOf(2))!.click();
      const req = http.expectOne(`${BASE}/users/2/disable`);
      expect(req.request.method).toBe('POST');
      req.flush(person(2, { disabledAt: '2026-11-03T10:00:00Z' }));
      fixture.detectChanges();

      expect(rowOf(2).textContent).toContain('Disabled');
      expect(button('Enable', rowOf(2))).toBeDefined();
      expect(el('.notice')?.textContent).toContain('Person 2 was disabled and signed out.');
    });

    it('a second click while the request is running sends nothing more', async () => {
      await create();
      button('Disable', rowOf(2))!.click();
      fixture.detectChanges();

      component.confirm(component.users()[1]);
      component.confirm(component.users()[1]);

      http.expectOne(`${BASE}/users/2/disable`).flush(person(2, { disabledAt: '2026-11-03T10:00:00Z' }));
    });

    it('shows the server\'s refusal and leaves the row as it was', async () => {
      await create(pageOf([person(1), person(2, { roles: ['Admin'] })]), 1);
      button('Disable', rowOf(2))!.click();
      fixture.detectChanges();

      button('Confirm', rowOf(2))!.click();
      http.expectOne(`${BASE}/users/2/disable`).flush({ error: { code: 'last_admin', message: 'This is the last active administrator.' } }, { status: 409, statusText: 'Conflict' });
      fixture.detectChanges();

      expect(el('.form-error')?.textContent).toContain('last active administrator');
      expect(rowOf(2).textContent).toContain('Active');
      expect(button('Disable', rowOf(2))).toBeDefined();
    });
  });

  describe('enabling', () => {
    it('enables at once (nothing to confirm) and updates the row', async () => {
      await create(pageOf([person(1), person(2, { disabledAt: '2026-11-02T10:00:00Z' })]));

      button('Enable', rowOf(2))!.click();
      http.expectOne(`${BASE}/users/2/enable`).flush(person(2));
      fixture.detectChanges();

      expect(rowOf(2).textContent).toContain('Active');
      expect(el('.notice')?.textContent).toContain('can log in again');
    });
  });

  describe('roles', () => {
    it('Make admin asks, then sends the role, then tells the admin the person must log in again', async () => {
      await create();
      button('Make admin', rowOf(2))!.click();
      fixture.detectChanges();
      expect(rowOf(2).textContent).toContain('Make Person 2 an Admin?');

      button('Confirm', rowOf(2))!.click();
      const req = http.expectOne(`${BASE}/users/2/role`);
      expect(req.request.method).toBe('PUT');
      expect(req.request.body).toEqual({ role: 'Admin' });
      req.flush(person(2, { roles: ['Admin'] }));
      fixture.detectChanges();

      expect(rowOf(2).textContent).toContain('Admin');
      expect(el('.notice')?.textContent).toContain('log in again');
      expect(button('Make user', rowOf(2))).toBeDefined();
    });

    it('Make user sends the User role for an admin', async () => {
      await create(pageOf([person(1), person(2, { roles: ['Admin'] })]));
      button('Make user', rowOf(2))!.click();
      fixture.detectChanges();

      button('Confirm', rowOf(2))!.click();

      const req = http.expectOne(`${BASE}/users/2/role`);
      expect(req.request.body).toEqual({ role: 'User' });
      req.flush(person(2));
    });
  });

  describe('paging and errors', () => {
    it('Next asks for page 2; Previous and Next are disabled at the ends', async () => {
      await create(pageOf([person(1)], { totalCount: 41, totalPages: 3 }));
      expect(button('Previous')!.disabled).toBeTrue();

      button('Next')!.click();
      const req = listRequest();
      expect(req.request.params.get('page')).toBe('2');
      req.flush(pageOf([person(21)], { page: 2, totalCount: 41, totalPages: 3 }));
      fixture.detectChanges();

      expect(button('Previous')!.disabled).toBeFalse();
      expect(text()).toContain('Page 2 of 3, 41 people');
    });

    it('shows a hint when no one matches, but not while loading', async () => {
      await create(pageOf([]));

      expect(text()).toContain('No one matches');
    });

    it('shows the server\'s message when the list cannot load', async () => {
      await TestBed.configureTestingModule({ imports: [UsersComponent], providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), { provide: AuthService, useValue: { user: () => null } }] }).compileComponents();
      http = TestBed.inject(HttpTestingController);
      fixture = TestBed.createComponent(UsersComponent);
      fixture.detectChanges();

      listRequest().flush({ error: { code: 'forbidden', message: 'You do not have access.' } }, { status: 403, statusText: 'Forbidden' });
      fixture.detectChanges();

      expect(el('.form-error')?.textContent).toContain('You do not have access.');
      expect(text()).not.toContain('No one matches');
    });
  });
});
