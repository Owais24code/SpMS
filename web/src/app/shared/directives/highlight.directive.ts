import { Directive, ElementRef, input, effect, inject } from '@angular/core';

/**
 * Wraps matches of a search term in <mark> without using innerHTML on
 * untrusted input — the element's own text is read, escaped, and rebuilt,
 * so a guest name containing markup cannot inject anything.
 */
@Directive({ selector: '[appHighlight]', standalone: true })
export class HighlightDirective {
  readonly appHighlight = input<string>('');

  private readonly el = inject(ElementRef<HTMLElement>);
  private original: string | null = null;

  constructor() {
    effect(() => {
      const term = this.appHighlight().trim();
      const node = this.el.nativeElement as HTMLElement;
      this.original ??= node.textContent ?? '';

      if (!term) {
        node.textContent = this.original;
        return;
      }

      const escaped = term.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      const parts = this.original.split(new RegExp(`(${escaped})`, 'ig'));

      node.textContent = '';
      for (const part of parts) {
        if (part.toLowerCase() === term.toLowerCase()) {
          const mark = document.createElement('mark');
          mark.textContent = part;
          node.appendChild(mark);
        } else if (part) {
          node.appendChild(document.createTextNode(part));
        }
      }
    });
  }
}
