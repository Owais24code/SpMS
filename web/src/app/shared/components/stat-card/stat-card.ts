import { Component, ChangeDetectionStrategy, input } from '@angular/core';

@Component({
  selector: 'app-stat-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <article class="sc">
      <p class="sc__label">{{ label() }}</p>
      <p class="sc__value numeric">{{ value() }}</p>
      <p class="sc__foot">
        @if (delta()) {
          <span class="sc__delta" [class]="'is-' + tone()">
            <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
              <path [attr.d]="trend() === 'down' ? 'M12 5v14M6 13l6 6 6-6' : 'M12 19V5M6 11l6-6 6 6'"
                    fill="none" stroke="currentColor" stroke-width="2.2"
                    stroke-linecap="round" stroke-linejoin="round" />
            </svg>
            {{ delta() }}
          </span>
        }
        <span class="sc__hint">{{ hint() }}</span>
      </p>
    </article>
  `,
  styles: [`
    :host { display: block; }

    .sc {
      position: relative;
      padding: var(--space-5);
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-lg);
      background: var(--grad-surface);
      overflow: hidden;
      transition: border-color var(--dur-base) var(--ease-out), box-shadow var(--dur-base) var(--ease-out);

      /* gradient hairline along the top edge */
      &::before {
        content: '';
        position: absolute;
        inset: 0 0 auto 0;
        height: 3px;
        background: var(--grad-accent);
        opacity: 0.85;
      }

      &:hover { border-color: var(--border-accent); box-shadow: var(--shadow-md); }
    }

    .sc__label { font-size: var(--text-xs); color: var(--fg-subtle); }

    .sc__value {
      margin-top: var(--space-2);
      font-size: var(--text-3xl);
      font-weight: var(--weight-bold);
      line-height: 1;
      letter-spacing: -0.02em;
    }

    .sc__foot {
      margin-top: var(--space-3);
      display: flex;
      align-items: center;
      gap: var(--space-2);
      flex-wrap: wrap;
    }

    .sc__delta {
      display: inline-flex;
      align-items: center;
      gap: 3px;
      font-size: var(--text-xs);
      font-weight: var(--weight-bold);
      padding: 2px var(--space-2);
      border-radius: var(--radius-full);
      background: var(--bg-muted);
      color: var(--fg-muted);

      svg { width: 12px; height: 12px; }

      /* Colour states whether the movement is good or bad — which is not
         the same as whether the number went up. A rising conflict count
         points up and is bad. */
      &.is-positive { background: var(--status-success-bg); color: var(--status-success-fg); }
      &.is-negative { background: var(--status-warning-bg); color: var(--status-warning-fg); }
      &.is-neutral  { background: var(--bg-muted);          color: var(--fg-muted); }
    }

    .sc__hint { font-size: var(--text-xs); color: var(--fg-subtle); }
  `],
})
export class StatCard {
  readonly label = input.required<string>();
  readonly value = input.required<string>();
  readonly delta = input<string>('');
  readonly hint = input<string>('');
  /** Arrow direction only. */
  readonly trend = input<'up' | 'down' | 'flat'>('flat');

  /** Whether that movement is good news. Independent of direction. */
  readonly tone = input<'positive' | 'negative' | 'neutral'>('neutral');
}
