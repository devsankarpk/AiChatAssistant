import { Directive, signal } from '@angular/core';

/** Lets a password input be revealed: `<input appPasswordVisibility #pw="appPasswordVisibility">`
 * plus a button calling `pw.toggle()`. Only flips the input's `type`; form state is untouched. */
@Directive({
  selector: 'input[appPasswordVisibility]',
  exportAs: 'appPasswordVisibility',
  host: { '[attr.type]': 'visible() ? "text" : "password"' },
})
export class PasswordVisibilityDirective {
  readonly visible = signal(false);

  toggle(): void {
    this.visible.update((v) => !v);
  }
}
