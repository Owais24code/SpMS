import { Component, ChangeDetectionStrategy, input } from '@angular/core';

@Component({
  selector: 'app-page-header',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="ph">
      <div class="ph__text">
        @if (eyebrow()) { <p class="eyebrow">{{ eyebrow() }}</p> }
        <h1 class="ph__title">{{ title() }}</h1>
        @if (subtitle()) { <p class="ph__sub">{{ subtitle() }}</p> }
      </div>
      <div class="ph__actions"><ng-content /></div>
    </header>
  `,
  styles: [`
    :host { display: block; }

    .ph {
      display: flex;
      align-items: flex-end;
      gap: var(--space-5);
      flex-wrap: wrap;
      padding-bottom: var(--space-5);
      margin-bottom: var(--space-5);
      border-bottom: 1px solid var(--border-subtle);
    }

    .ph__title { font-size: var(--text-2xl); letter-spacing: -0.01em; margin-top: var(--space-1); }

    .ph__sub {
      margin-top: var(--space-2);
      color: var(--fg-muted);
      font-size: var(--text-sm);
      max-width: 68ch;
      line-height: var(--leading-snug);
    }

    .ph__actions { margin-inline-start: auto; display: flex; gap: var(--space-2); flex-wrap: wrap; }
  `],
})
export class PageHeader {
  readonly title = input.required<string>();
  readonly eyebrow = input<string>('');
  readonly subtitle = input<string>('');
}
