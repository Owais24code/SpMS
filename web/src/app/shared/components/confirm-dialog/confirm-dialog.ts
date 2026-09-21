import { Component, ChangeDetectionStrategy, inject, signal, computed, HostListener } from '@angular/core';
import { ConfirmService } from '../../../core/services/confirm.service';
import { FocusTrapDirective } from '../../directives/focus-trap.directive';

@Component({
  selector: 'app-confirm-dialog',
  standalone: true,
  imports: [FocusTrapDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (confirm.request(); as r) {
      <div class="scrim" (click)="cancel()" aria-hidden="true"></div>

      <div
        class="dlg"
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="confirm-title"
        aria-describedby="confirm-body"
        [appFocusTrap]="true"
      >
        <h2 class="dlg__title" id="confirm-title">{{ r.title }}</h2>
        <p class="dlg__body" id="confirm-body">{{ r.consequence }}</p>

        @if (r.typeToConfirm) {
          <label class="dlg__label" for="confirm-type">
            Type <strong>{{ r.typeToConfirm }}</strong> to continue
          </label>
          <input
            id="confirm-type" class="input" autocomplete="off"
            [value]="typed()" (input)="typed.set($any($event.target).value)"
          />
        }

        <div class="dlg__actions">
          <button type="button" class="btn btn--ghost" (click)="cancel()">
            {{ r.cancelLabel ?? 'Cancel' }}
          </button>
          <button
            type="button"
            class="btn"
            [class.btn--primary]="r.tone !== 'danger'"
            [class.btn--danger]="r.tone === 'danger'"
            [disabled]="!ready()"
            (click)="ok()"
          >{{ r.confirmLabel }}</button>
        </div>
      </div>
    }
  `,
  styles: [`
    :host { display: contents; }

    .scrim { position: fixed; inset: 0; z-index: 190; background: var(--scrim); }

    .dlg {
      position: fixed;
      z-index: 195;
      inset-inline: 50%;
      top: 50%;
      transform: translate(-50%, -50%);
      width: min(92vw, 440px);
      padding: var(--space-6);
      border-radius: var(--radius-lg);
      background: var(--bg-canvas);
      border: 1px solid var(--border-subtle);
      box-shadow: var(--shadow-xl);
      animation: pop var(--dur-base) var(--ease-out);

      &__title { font-size: var(--text-lg); }
      &__body  { margin-top: var(--space-3); color: var(--fg-muted); font-size: var(--text-sm); line-height: var(--leading-loose); }
      &__label { display: block; margin-top: var(--space-5); font-size: var(--text-sm); margin-bottom: var(--space-2); }
      &__actions { margin-top: var(--space-6); display: flex; justify-content: flex-end; gap: var(--space-2); }

      .input { width: 100%; }
    }

    .btn--danger {
      background: var(--status-danger-br);
      color: #fff;
      border-color: var(--status-danger-br);
      &:hover { filter: brightness(1.08); }
    }

    @keyframes pop {
      from { opacity: 0; transform: translate(-50%, -46%) scale(0.97); }
      to   { opacity: 1; transform: translate(-50%, -50%) scale(1); }
    }
  `],
})
export class ConfirmDialog {
  protected readonly confirm = inject(ConfirmService);
  protected readonly typed = signal('');

  protected readonly ready = computed(() => {
    const r = this.confirm.request();
    if (!r) return false;
    return !r.typeToConfirm || this.typed().trim() === r.typeToConfirm;
  });

  @HostListener('document:keydown.escape')
  protected onEscape(): void {
    if (this.confirm.request()) this.cancel();
  }

  protected ok(): void { this.typed.set(''); this.confirm.settle(true); }
  protected cancel(): void { this.typed.set(''); this.confirm.settle(false); }
}
