import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { AuthService } from '../core/services/auth.service';
import { DirectStateService } from '../core/services/direct-state.service';
import { AppointmentsComponent } from './appointments.component';

describe('AppointmentsComponent', () => {
  let fixture: ComponentFixture<AppointmentsComponent>;
  let auth: jasmine.SpyObj<AuthService>;
  let router: Router;

  beforeEach(async () => {
    auth = jasmine.createSpyObj<AuthService>('AuthService', ['logout']);
    await TestBed.configureTestingModule({
      imports: [AppointmentsComponent],
      providers: [provideRouter([]), { provide: AuthService, useValue: auth }, { provide: DirectStateService, useValue: { unreadTotal: signal(0) } }],
    }).compileComponents();

    router = TestBed.inject(Router);
    fixture = TestBed.createComponent(AppointmentsComponent);
    fixture.detectChanges();
  });

  it('says booking is coming soon and offers a way back to the chatbot', () => {
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('h1')?.textContent).toContain('coming soon');
    expect(el.querySelector('a.btn-primary')?.getAttribute('href')).toBe('/');
  });

  it('shows the top nav', () => {
    expect(fixture.nativeElement.querySelector('app-top-nav nav')).not.toBeNull();
  });

  it('logs out and returns to the login page', () => {
    const navigate = spyOn(router, 'navigateByUrl').and.resolveTo(true);
    const logout = Array.from<HTMLButtonElement>(fixture.nativeElement.querySelectorAll('button')).find(
      (b) => b.textContent?.trim() === 'Log out',
    );

    logout!.click();

    expect(auth.logout).toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith('/login');
  });
});
