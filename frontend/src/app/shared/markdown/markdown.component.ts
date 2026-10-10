import { Component, ViewEncapsulation, computed, input } from '@angular/core';
import { Marked } from 'marked';

function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

// The text is LLM output, i.e. untrusted. Two layers: (1) here, raw HTML is shown as text and images
// are never loaded (a remote image URL is a data-exfiltration channel for a prompt-injected
// answer); (2) Angular's own sanitizer still runs on [innerHTML] below and strips anything
// dangerous that slips through (script, on* handlers, javascript: URLs).
const markdown = new Marked({
  gfm: true,
  renderer: {
    html: ({ text }) => escapeHtml(text),
    image: ({ text }) => escapeHtml(text),
  },
});

@Component({
  selector: 'app-markdown',
  template: `<div class="md" [innerHTML]="html()" (click)="openLinksSafely($event)"></div>`,
  styleUrl: './markdown.component.scss',
  // The rendered HTML isn't part of this template, so scoped styles can't reach it.
  encapsulation: ViewEncapsulation.None,
})
export class MarkdownComponent {
  readonly source = input.required<string>();

  readonly html = computed(() => markdown.parse(this.source().trim(), { async: false }));

  // Links open in a new tab so following one never throws away the conversation. Done here rather
  // than with target="_blank" in the markup because the sanitizer's attribute allow-list is not
  // something to rely on.
  openLinksSafely(event: MouseEvent): void {
    const link = (event.target as HTMLElement).closest('a');
    const href = link?.getAttribute('href') ?? '';
    if (link && /^(https?:|mailto:)/i.test(href)) {
      event.preventDefault();
      window.open(href, '_blank', 'noopener,noreferrer');
    }
  }
}
