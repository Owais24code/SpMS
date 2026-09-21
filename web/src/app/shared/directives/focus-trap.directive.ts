import { Directive, ElementRef, inject, effect, input, afterNextRender, Injector } from '@angular/core';

const FOCUSABLE = [
  'a[href]', 'button:not([disabled])', 'input:not([disabled])',
  'select:not([disabled])', 'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(',');

/**
 * Keeps keyboard focus inside a dialog while it is open, and puts it back
 * where it came from on close.
 *
 * A dialog that sets aria-modal but leaves focus outside is worse than no
 * dialog at all: a screen-reader user is told they are in a modal while
 * their focus is still on the page behind it.
 */
@Directive({ selector: '[appFocusTrap]', standalone: true })
export class FocusTrapDirective {
  /** The dialog traps focus while this is true. */
  readonly appFocusTrap = input<boolean>(true);

  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly injector = inject(Injector);
  private returnTo: HTMLElement | null = null;

  constructor() {
    effect((onCleanup) => {
      if (!this.appFocusTrap()) return;

      const el = this.host.nativeElement as HTMLElement;
      this.returnTo = document.activeElement as HTMLElement | null;

      // Wait for the dialog's content to exist before reaching for it.
      afterNextRender(() => this.focusFirst(el), { injector: this.injector });

      const onKeydown = (e: KeyboardEvent) => {
        if (e.key !== 'Tab') return;
        const items = Array.from(el.querySelectorAll<HTMLElement>(FOCUSABLE))
          .filter((n) => n.offsetParent !== null || n === document.activeElement);
        if (items.length === 0) return;

        const first = items[0];
        const last = items[items.length - 1];

        if (e.shiftKey && document.activeElement === first) {
          e.preventDefault();
          last.focus();
        } else if (!e.shiftKey && document.activeElement === last) {
          e.preventDefault();
          first.focus();
        }
      };

      el.addEventListener('keydown', onKeydown);

      onCleanup(() => {
        el.removeEventListener('keydown', onKeydown);
        this.returnTo?.focus?.();
        this.returnTo = null;
      });
    });
  }

  private focusFirst(el: HTMLElement): void {
    const target =
      el.querySelector<HTMLElement>('[autofocus]') ??
      el.querySelector<HTMLElement>(FOCUSABLE) ??
      el;
    if (!el.hasAttribute('tabindex') && target === el) el.setAttribute('tabindex', '-1');
    target.focus();
  }
}
