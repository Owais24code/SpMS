import { Component, ChangeDetectionStrategy, signal, input } from '@angular/core';

export interface FaqItem {
  readonly q: string;
  readonly a: string;
}

/**
 * Single-expand accordion.
 *
 * Uses native <button aria-expanded> + a region labelled by the button, so
 * screen readers announce state changes without any live-region plumbing.
 * Height animates via grid-template-rows, which handles unknown content
 * height without measuring — important for long localized answers.
 */
@Component({
  selector: 'app-faq-accordion',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './faq-accordion.html',
  styleUrl: './faq-accordion.scss',
})
export class FaqAccordion {
  readonly items = input.required<readonly FaqItem[]>();
  readonly idPrefix = input<string>('faq');

  protected readonly openIndex = signal<number | null>(0);

  protected toggle(i: number): void {
    this.openIndex.update((cur) => (cur === i ? null : i));
  }

  protected isOpen(i: number): boolean {
    return this.openIndex() === i;
  }
}
