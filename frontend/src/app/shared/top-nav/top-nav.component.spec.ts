import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

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

  const links = () => Array.from<HTMLAnchorElement>(fixture.nativeElement.querySelectorAll('nav a'));

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        provideRouter([
          { path: '', component: BlankComponent },
          { path: 'appointments', component: BlankComponent },
        ]),
      ],
    }).compileComponents();

    router = TestBed.inject(Router);
    fixture = TestBed.createComponent(HostComponent);
  });

  async function goTo(url: string): Promise<void> {
    await router.navigateByUrl(url);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('offers a Chatbot tab pointing at the home route (Book appointment lives in the chat sidebar)', async () => {
    await goTo('/');

    expect(links().map((a) => a.textContent?.trim())).toEqual(['Chatbot']);
    expect(links().map((a) => a.getAttribute('href'))).toEqual(['/']);
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

  it('shows anything a page projects into the right-hand end', async () => {
    await goTo('/');

    expect(fixture.nativeElement.querySelector('.nav-end #extra')).not.toBeNull();
  });
});
