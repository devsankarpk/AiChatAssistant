import { Component, computed, input } from '@angular/core';

import { DirectMessageStatus } from '../../core/models/direct.models';

const LABELS: Record<DirectMessageStatus, string> = {
  sent: 'Sent',
  delivered: 'Delivered',
  read: 'Read',
};

/**
 * The delivery state of a message you sent, as a messaging app shows it:
 * one grey tick = sent, two grey ticks = delivered, two blue ticks = read.
 * Colour alone never carries the meaning: the icon has an accessible label, and sent differs from
 * delivered by shape (one tick or two).
 */
@Component({
  selector: 'app-ticks',
  template: `
    <svg
      class="ticks"
      [class.read]="status() === 'read'"
      [attr.data-status]="status()"
      viewBox="0 0 18 11"
      width="18"
      height="11"
      role="img"
      [attr.aria-label]="label()"
    >
      <title>{{ label() }}</title>
      <path d="M1 6l3.2 3.2L10.6 2" />
      @if (status() !== 'sent') {
        <path d="M6.6 8.4l.9.8L14 2" />
      }
    </svg>
  `,
  styles: `
    :host {
      display: inline-flex;
      vertical-align: middle;
    }

    .ticks {
      fill: none;
      stroke: var(--text-muted);
      stroke-width: 1.7;
      stroke-linecap: round;
      stroke-linejoin: round;
    }

    .ticks.read {
      stroke: var(--tick-read);
    }
  `,
})
export class TicksComponent {
  readonly status = input.required<DirectMessageStatus>();
  readonly label = computed(() => LABELS[this.status()]);
}
