import { TestBed } from '@angular/core/testing';
import { ThemeService } from './theme.service';

describe('ThemeService', () => {
  beforeEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    TestBed.resetTestingModule();
  });

  afterEach(() => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
  });

  it('defaults to light and leaves <html> without a data-theme', () => {
    const service = TestBed.inject(ThemeService);

    expect(service.theme()).toBe('light');
    expect(document.documentElement.hasAttribute('data-theme')).toBeFalse();
  });

  it('toggles to dark, sets data-theme and remembers the choice', () => {
    const service = TestBed.inject(ThemeService);

    service.toggle();

    expect(service.theme()).toBe('dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(localStorage.getItem('aichat.theme')).toBe('dark');
  });

  it('restores a stored dark theme on startup', () => {
    localStorage.setItem('aichat.theme', 'dark');

    const service = TestBed.inject(ThemeService);

    expect(service.theme()).toBe('dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
  });

  it('still toggles when localStorage throws', () => {
    const service = TestBed.inject(ThemeService);
    spyOn(Storage.prototype, 'setItem').and.throwError('blocked');

    expect(() => service.toggle()).not.toThrow();
    expect(service.theme()).toBe('dark');
  });
});
