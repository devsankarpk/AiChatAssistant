import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { PasswordVisibilityDirective } from './password-visibility.directive';

@Component({
  imports: [PasswordVisibilityDirective],
  template: `
    <input appPasswordVisibility #pw="appPasswordVisibility" type="password" />
    <button type="button" (click)="pw.toggle()">toggle</button>
  `,
})
class HostComponent {}

describe('PasswordVisibilityDirective', () => {
  it('flips the input between password and text without touching its value', () => {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const input: HTMLInputElement = fixture.nativeElement.querySelector('input');
    const button: HTMLButtonElement = fixture.nativeElement.querySelector('button');
    input.value = 'secret-123';

    expect(input.type).toBe('password');

    button.click();
    fixture.detectChanges();
    expect(input.type).toBe('text');
    expect(input.value).toBe('secret-123');

    button.click();
    fixture.detectChanges();
    expect(input.type).toBe('password');
  });
});
