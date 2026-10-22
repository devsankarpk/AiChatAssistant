import { ComponentFixture, TestBed } from '@angular/core/testing';

import { DirectMessageStatus } from '../../core/models/direct.models';
import { TicksComponent } from './ticks.component';

describe('TicksComponent', () => {
  let fixture: ComponentFixture<TicksComponent>;

  function render(status: DirectMessageStatus): SVGElement {
    fixture = TestBed.createComponent(TicksComponent);
    fixture.componentRef.setInput('status', status);
    fixture.detectChanges();
    return fixture.nativeElement.querySelector('svg.ticks');
  }

  beforeEach(() => TestBed.configureTestingModule({ imports: [TicksComponent] }).compileComponents());

  it('shows ONE grey tick for a message that is only sent', () => {
    const svg = render('sent');

    expect(svg.querySelectorAll('path').length).toBe(1);
    expect(svg.classList.contains('read')).toBeFalse();
    expect(svg.getAttribute('aria-label')).toBe('Sent');
  });

  it('shows TWO grey ticks for a delivered message', () => {
    const svg = render('delivered');

    expect(svg.querySelectorAll('path').length).toBe(2);
    expect(svg.classList.contains('read')).toBeFalse();
    expect(svg.getAttribute('aria-label')).toBe('Delivered');
  });

  it('shows TWO BLUE ticks for a read message', () => {
    const svg = render('read');

    expect(svg.querySelectorAll('path').length).toBe(2);
    expect(svg.classList.contains('read')).toBeTrue(); // the class that switches the stroke to --tick-read
    expect(svg.getAttribute('aria-label')).toBe('Read');
  });

  it('is exposed to assistive tech as an image with a text label, not by colour alone', () => {
    const svg = render('delivered');

    expect(svg.getAttribute('role')).toBe('img');
    expect(svg.querySelector('title')?.textContent).toBe('Delivered');
  });

  it('updates when the status moves forward', () => {
    const svg = render('sent');
    expect(svg.getAttribute('data-status')).toBe('sent');

    fixture.componentRef.setInput('status', 'read');
    fixture.detectChanges();

    expect(svg.getAttribute('data-status')).toBe('read');
    expect(svg.querySelectorAll('path').length).toBe(2);
  });
});
