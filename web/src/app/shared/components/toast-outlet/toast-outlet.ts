import { Component, ChangeDetectionStrategy, inject } from '@angular/core';
import { ToastService } from '../../../core/services/toast.service';

@Component({
  selector: 'app-toast-outlet',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <!-- Polite region: confirmations should not interrupt a screen reader
         mid-sentence. Errors carry their own alert role below. -->
    <div class="toasts" aria-live="polite" aria-relevant="additions">
      @for (t of toasts.toasts(); track t.id) {
        <div class="toast" [class]="'toast--' + t.tone" [attr.role]="t.tone === 'danger' ? 'alert' : null">
          <span class="toast__icon" aria-hidden="true">
            <svg viewBox="0 0 24 24" focusable="false">
              <path [attr.d]="icon(t.tone)" fill="none" stroke="currentColor" stroke-width="2"
                    stroke-linecap="round" stroke-linejoin="round" />
            </svg>
          </span>

          <div class="toast__text">
            <p class="toast__title">{{ t.title }}</p>
            @if (t.body) { <p class="toast__body">{{ t.body }}</p> }
            @if (t.code) { <p class="toast__code numeric">{{ t.code }}</p> }
          </div>

          @if (t.undo) {
            <button type="button" class="toast__undo" (click)="t.undo!(); toasts.dismiss(t.id)">Undo</button>
          }

          <button type="button" class="toast__x" (click)="toasts.dismiss(t.id)" aria-label="Dismiss">
            <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
              <path d="M6 6l12 12M18 6L6 18" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" />
            </svg>
          </button>
        </div>
      }
    </div>
  `,
  styles: [`
    :host { display: contents; }

    .toasts {
      position: fixed;
      z-index: 200;
      bottom: var(--space-5);
      inset-inline-start: 50%;
      transform: translateX(-50%);
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      width: min(94vw, 460px);
      pointer-events: none;
    }

    .toast {
      pointer-events: auto;
      display: flex;
      align-items: flex-start;
      gap: var(--space-3);
      padding: var(--space-4);
      border-radius: var(--radius-md);
      border: 1px solid var(--border-subtle);
      background: var(--bg-canvas);
      box-shadow: var(--shadow-lg);
      animation: toast-in var(--dur-base) var(--ease-out);

      &--success { border-color: var(--status-success-br); .toast__icon { color: var(--status-success-fg); background: var(--status-success-bg); } }
      &--info    { border-color: var(--status-info-br);    .toast__icon { color: var(--status-info-fg);    background: var(--status-info-bg); } }
      &--warning { border-color: var(--status-warning-br); .toast__icon { color: var(--status-warning-fg); background: var(--status-warning-bg); } }
      &--danger  { border-color: var(--status-danger-br);  .toast__icon { color: var(--status-danger-fg);  background: var(--status-danger-bg); } }

      &__icon {
        display: grid; place-items: center;
        width: 28px; height: 28px; flex-shrink: 0;
        border-radius: var(--radius-full);
        svg { width: 15px; height: 15px; }
      }

      &__text { flex: 1; min-width: 0; }
      &__title { font-weight: var(--weight-bold); font-size: var(--text-sm); }
      &__body  { margin-top: 2px; font-size: var(--text-sm); color: var(--fg-muted); line-height: var(--leading-snug); }
      &__code  { margin-top: var(--space-2); font-size: var(--text-2xs); color: var(--fg-subtle); }

      &__undo {
        flex-shrink: 0;
        border: 0; background: none; padding: var(--space-1) var(--space-2);
        color: var(--fg-link); font: inherit; font-size: var(--text-sm);
        font-weight: var(--weight-bold); cursor: pointer; text-decoration: underline;
      }

      &__x {
        flex-shrink: 0;
        display: grid; place-items: center;
        width: 26px; height: 26px;
        border: 0; border-radius: var(--radius-sm);
        background: transparent; color: var(--fg-subtle); cursor: pointer;
        svg { width: 13px; height: 13px; fill: none; }
        &:hover { background: var(--bg-muted); color: var(--fg-default); }
      }
    }

    @keyframes toast-in {
      from { opacity: 0; transform: translateY(12px); }
      to   { opacity: 1; transform: translateY(0); }
    }
  `],
})
export class ToastOutlet {
  protected readonly toasts = inject(ToastService);

  protected icon(tone: string): string {
    return tone === 'success' ? 'M5 12.5 10 17l9-10'
      : tone === 'danger' || tone === 'warning' ? 'M12 4 2.5 20h19zM12 10v4M12 17h.01'
      : 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18M12 11v5M12 8h.01';
  }
}
