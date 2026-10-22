import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { DirectStateService } from '../../core/services/direct-state.service';

import { TopNavComponent } from './top-nav.component';

@Component({ template: '' })
class BlankComponent {}

@Component({
  imports: [TopNavComponent],
  template: `<app-top-nav><button id="extra" type="button">Extra</button></app-top-nav>`,
})
class HostComponent {}

describe('TopNavComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let router: Router;
  const unread = signal(0);

  const links = () => Array.from<HTMLAnchorElement>(fixture.nativeElement.querySelectorAll('nav a'));

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        provideRouter([
          { path: '', component: BlankComponent },
          { path: 'appointments', component: BlankComponent },
          { path: 'messages', component: BlankComponent },
        ]),
        { provide: DirectStateService, useValue: { unreadTotal: unread } },
      ],
    }).compileComponents();

    unread.set(0);
    router = TestBed.inject(Router);
    fixture = TestBed.createComponent(HostComponent);
  });

  async function goTo(url: string): Promise<void> {
    await router.navigateByUrl(url);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('offers Chatbot and Messages tabs pointing at the right routes (Book appointment lives in the chat sidebar)', async () => {
    await goTo('/');

    expect(links().map((a) => a.textContent?.trim())).toEqual(['Chatbot', 'Messages']);
    expect(links().map((a) => a.getAttribute('href'))).toEqual(['/', '/messages']);
  });

  it('marks Chatbot as the current page on the home route', async () => {
    await goTo('/');

    expect(links()[0].getAttribute('aria-current')).toBe('page');
  });

  it('keeps Chatbot current when the URL carries a ?session= query param', async () => {
    await goTo('/?session=abc');

    expect(links()[0].getAttribute('aria-current')).toBe('page');
  });

  it('does not mark Chatbot as current on another page', async () => {
    await goTo('/appointments');

    expect(links()[0].getAttribute('aria-current')).toBeNull();
  });

  it('marks Messages as the current page on /messages and not Chatbot', async () => {
    await goTo('/messages');

    expect(links()[1].getAttribute('aria-current')).toBe('page');
    expect(links()[0].getAttribute('aria-current')).toBeNull();
  });

  it('shows no unread badge when there is nothing unread', async () => {
    await goTo('/');

    expect(fixture.nativeElement.querySelector('.badge')).toBeNull();
  });

  it('shows the live unread count on the Messages tab, updating as it changes, with accessible text', async () => {
    await goTo('/');

    unread.set(3);
    fixture.detectChanges();
    const badge = fixture.nativeElement.querySelector('.badge') as HTMLElement;
    expect(badge.textContent).toContain('3');
    expect(badge.querySelector('.visually-hidden')?.textContent?.trim()).toBe('unread');

    unread.set(7);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.badge').textContent).toContain('7');

    unread.set(0);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.badge')).toBeNull();
  });

  it('caps the badge at 99+', async () => {
    await goTo('/');

    unread.set(120);
    fixture.detectChanges();

    expect((fixture.nativeElement.querySelector('.badge') as HTMLElement).textContent).toContain('99+');
  });

  it('shows anything a page projects into the right-hand end', async () => {
    await goTo('/');

    expect(fixture.nativeElement.querySelector('.nav-end #extra')).not.toBeNull();
  });
});
