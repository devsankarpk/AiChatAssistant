import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AppComponent } from './app.component';
import { AuthService } from './core/services/auth.service';
import { DirectStateService } from './core/services/direct-state.service';

describe('AppComponent', () => {
  const signedIn = signal(false);
  let direct: jasmine.SpyObj<DirectStateService>;

  beforeEach(async () => {
    signedIn.set(false);
    direct = jasmine.createSpyObj<DirectStateService>('DirectStateService', ['start', 'stop']);
    await TestBed.configureTestingModule({
      imports: [AppComponent],
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: { isAuthenticated: signedIn } },
        { provide: DirectStateService, useValue: direct },
      ],
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(AppComponent);
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('keeps the live chat connection off while signed out', () => {
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();

    expect(direct.start).not.toHaveBeenCalled();
    expect(direct.stop).toHaveBeenCalled();
  });

  it('starts the live chat connection when someone signs in, and drops it when they sign out', () => {
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();

    signedIn.set(true);
    fixture.detectChanges();
    expect(direct.start).toHaveBeenCalledTimes(1);

    signedIn.set(false);
    fixture.detectChanges();
    expect(direct.stop).toHaveBeenCalledTimes(2); // once at start-up (signed out), once at sign-out
  });
});
