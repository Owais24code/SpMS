import { Component, ChangeDetectionStrategy, input, output, computed } from '@angular/core';

export type ScreenState =
  | 'loading' | 'empty' | 'first-use' | 'stale' | 'conflict'
  | 'denied' | 'timeout' | 'offline' | 'queued' | 'error';

interface StatePreset {
  readonly icon: string;
  readonly tone: 'neutral' | 'info' | 'warn' | 'danger';
  readonly title: string;
  readonly body: string;
  readonly action: string | null;
}

/**
 * The designed non-happy-path states, in one place.
 *
 * Every screen needs these and they must look identical everywhere, so they
 * live here rather than being re-invented per feature. Each preset names the
 * consequence and the next safe action, never just "something went wrong".
 */
@Component({
  selector: 'app-state-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="sp" [class]="'sp--' + preset().tone" [attr.role]="live()">
      @if (state() === 'loading') {
        <div class="sp__spinner" aria-hidden="true"><span></span><span></span><span></span></div>
      } @else {
        <span class="sp__icon" aria-hidden="true">
          <svg viewBox="0 0 24 24" focusable="false">
            <path [attr.d]="preset().icon" fill="none" stroke="currentColor" stroke-width="1.7"
                  stroke-linecap="round" stroke-linejoin="round" />
          </svg>
        </span>
      }

      <p class="sp__title">{{ title() || preset().title }}</p>
      <p class="sp__body">{{ body() || preset().body }}</p>

      <!-- Only offered when a caller has bound (action); an unwired button
           that looks clickable is worse than no button. -->
      @if (preset().action && actionBound()) {
        <button type="button" class="btn btn--secondary" (click)="action.emit()">
          {{ actionLabel() || preset().action }}
        </button>
      }
    </div>
  `,
  styles: [`
    :host { display: block; }

    .sp {
      display: flex;
      flex-direction: column;
      align-items: center;
      text-align: center;
      gap: var(--space-3);
      padding: var(--space-12) var(--space-6);
      border: 1px dashed var(--border-default);
      border-radius: var(--radius-lg);
      background: var(--bg-subtle);
    }

    .sp__icon {
      display: grid;
      place-items: center;
      width: 48px; height: 48px;
      border-radius: var(--radius-full);
      background: var(--bg-muted);
      color: var(--fg-muted);

      svg { width: 24px; height: 24px; }
    }

    .sp--info   .sp__icon { background: var(--status-info-bg);    color: var(--status-info-fg); }
    .sp--warn   .sp__icon { background: var(--status-warning-bg); color: var(--status-warning-fg); }
    .sp--danger .sp__icon { background: var(--status-danger-bg);  color: var(--status-danger-fg); }

    .sp__title { font-weight: var(--weight-bold); font-size: var(--text-base); }

    .sp__body {
      color: var(--fg-muted);
      font-size: var(--text-sm);
      max-width: 52ch;
      line-height: var(--leading-loose);
    }

    .sp__spinner {
      display: flex;
      gap: 6px;

      span {
        width: 9px; height: 9px;
        border-radius: 50%;
        background: var(--bg-accent);
        animation: bounce 1.1s var(--ease-inout) infinite;

        &:nth-child(2) { animation-delay: 0.14s; }
        &:nth-child(3) { animation-delay: 0.28s; }
      }
    }

    @keyframes bounce {
      0%, 70%, 100% { transform: translateY(0); opacity: 0.45; }
      35%           { transform: translateY(-7px); opacity: 1; }
    }
  `],
})
export class StatePanel {
  readonly state = input.required<ScreenState>();
  readonly title = input<string>('');
  readonly body = input<string>('');

  /** Set to true by a caller that binds (action). */
  readonly actionBound = input<boolean>(false);
  readonly actionLabel = input<string>('');

  readonly action = output<void>();

  /** Errors are announced; quiet states are not, to avoid chatter. */
  protected readonly live = computed(() =>
    ['denied', 'error', 'timeout', 'conflict'].includes(this.state()) ? 'alert' : 'status',
  );

  protected readonly preset = computed<StatePreset>(() => PRESETS[this.state()]);
}

const PRESETS: Record<ScreenState, StatePreset> = {
  loading: {
    icon: '', tone: 'neutral',
    title: 'Loading',
    body: 'Fetching the current state for this property.',
    action: null,
  },
  empty: {
    icon: 'M4 7h16v13H4zM4 7l2-3h12l2 3M9 12h6',
    tone: 'neutral',
    title: 'Nothing here yet',
    body: 'No records match the current filters. Widen the date range or clear a filter to see more.',
    action: 'Clear filters',
  },
  'first-use': {
    icon: 'M12 4v16M4 12h16',
    tone: 'info',
    title: 'Set this up',
    body: 'Once you add your first record it will appear here, along with the actions you can take on it.',
    action: 'Get started',
  },
  stale: {
    icon: 'M12 7v5l3 2M3.5 12a8.5 8.5 0 1 0 2.2-5.7M3.5 4v3.5H7',
    tone: 'warn',
    title: 'Someone else changed this',
    body: 'This record was updated while you were editing. Your changes are still here — review what changed before saving.',
    action: 'Compare changes',
  },
  conflict: {
    icon: 'M12 4 2.5 20h19zM12 10v4M12 17h.01',
    tone: 'warn',
    title: 'This booking clashes',
    body: 'Committing would double-book a resource. Review the alternatives before continuing.',
    action: 'Review conflict',
  },
  denied: {
    icon: 'M6 11V8a6 6 0 1 1 12 0v3M5 11h14v9H5z',
    tone: 'neutral',
    title: 'You do not have access to this',
    body: 'Your role can see that this record exists but not its contents. Ask a manager if you need access.',
    action: null,
  },
  timeout: {
    icon: 'M12 7v5l3 2M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18',
    tone: 'warn',
    title: 'This is taking longer than usual',
    body: 'A connected system has not responded. Nothing has been changed — you can retry safely.',
    action: 'Retry',
  },
  offline: {
    icon: 'M3 3l18 18M8.5 16.5a5 5 0 0 1 7 0M5 13a10 10 0 0 1 4-2.4M19 13a10 10 0 0 0-3-2.1M12 20h.01',
    tone: 'warn',
    title: 'Working offline',
    body: 'Changes are saved on this device and will sync when the connection returns. Nothing is lost.',
    action: null,
  },
  queued: {
    icon: 'M4 6h16M4 12h16M4 18h16M20 9l-3-3-3 3',
    tone: 'info',
    title: 'Waiting to sync',
    body: 'Your changes are queued in order and will be applied once. Retrying will not duplicate them.',
    action: null,
  },
  error: {
    icon: 'M12 4 2.5 20h19zM12 10v4M12 17h.01',
    tone: 'danger',
    title: 'That did not save',
    body: 'Your work is still on screen. Try again, or copy what you need before leaving this page.',
    action: 'Try again',
  },
};
