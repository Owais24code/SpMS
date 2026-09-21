import { Component, ChangeDetectionStrategy, signal, inject, computed, OnDestroy } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatePanel } from '../../shared/components/state-panel/state-panel';
import { DurationPipe } from '../../shared/pipes/duration.pipe';
import { WorkspaceStore } from '../../core/services/workspace-store';
import { ToastService } from '../../core/services/toast.service';
import { ConfirmService } from '../../core/services/confirm.service';

@Component({
  selector: 'app-treatments',
  standalone: true,
  imports: [PageHeader, StatePanel, DurationPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header
      eyebrow="Provider"
      title="Treatment delivery"
      subtitle="Works offline. Start, pause and complete are idempotent, so a reconnect can never double-post."
    >
      <button type="button" class="btn btn--secondary" (click)="addProduct()">Add product</button>
      <button type="button" class="btn btn--primary" (click)="rebook()">Rebook guest</button>
    </app-page-header>

    <div class="stack">
      @if (!online()) {
        <app-state-panel
          state="offline"
          [title]="'No connection — ' + queued() + ' change' + (queued() === 1 ? '' : 's') + ' queued'"
          body="Your timer and notes are saved on this device. They will sync in order when the connection returns."
        />
        <div class="row" style="justify-content:center">
          <button type="button" class="btn btn--secondary" (click)="reconnect()">Simulate reconnect</button>
        </div>
      } @else {
        <app-state-panel
          state="queued"
          title="Synced"
          body="Everything queued on this device has been applied in order."
        />
      }

      <div class="grid grid--split">
        <div class="panel">
          <div class="panel__head">
            <span class="panel__title">In progress</span>
            <span class="badge badge--info">Guest 4821</span>
            <div class="panel__actions">
              <button type="button" class="btn btn--ghost" [disabled]="locked()" (click)="toggleTimer()">
                {{ running() ? 'Pause' : 'Resume' }}
              </button>
              <button type="button" class="btn btn--primary" [disabled]="locked()" (click)="complete()">
                {{ locked() ? 'Completed' : 'Complete' }}
              </button>
            </div>
          </div>

          <div class="panel__body stack">
            <div class="timer">
              <p class="timer__clock numeric">{{ clock() }}</p>
              <p class="subtle">of {{ 90 | duration }} — Deep tissue{{ running() ? '' : locked() ? ' — locked' : ' — paused' }}</p>
              <div class="meter"><span [style.width.%]="progress()"></span></div>
            </div>

            <div class="restrict">
              @if (acknowledged()) {
                <span class="badge badge--ok">Acknowledged</span>
              } @else {
                <button type="button" class="badge badge--warn" (click)="acknowledge()">
                  Acknowledge before starting
                </button>
              }
              <p>Avoid deep pressure on the lower back. Shoulder work is fine.</p>
              <p class="subtle">Minimum-necessary summary. Full intake is not available on this device.</p>
            </div>

            <div>
              <label for="notes">Treatment notes</label>
              <textarea
                id="notes" rows="4"
                [disabled]="locked()"
                [value]="notes()"
                (input)="notes.set($any($event.target).value)"
                placeholder="Autosaved and encrypted. Locks when you mark the service complete."
              ></textarea>
              <p class="subtle">
                {{ locked() ? 'Locked on completion — amendments only.' : 'Autosaved · ' + queued() + ' queued' }}
              </p>
            </div>
          </div>
        </div>

        <div class="panel">
          <div class="panel__head"><span class="panel__title">Rest of today</span></div>
          <div class="panel__body panel__body--flush">
            <ul class="timeline" style="padding: var(--space-5)">
              @for (a of upcoming(); track a.id) {
                <li [class.is-active]="a.state === 'in-progress'" [class.is-done]="a.state === 'complete'">
                  <p class="timeline__when numeric">{{ a.start }} · {{ a.durationMin | duration }}</p>
                  <p class="timeline__what">{{ a.service }}</p>
                  <p class="timeline__note">{{ a.guestAlias }} · {{ a.room }}</p>
                </li>
              }
            </ul>
          </div>
        </div>
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }

    .timer {
      text-align: center;
      padding: var(--space-6);
      border-radius: var(--radius-lg);
      background: var(--grad-brand);
      color: #fff;

      .subtle { color: rgb(255 255 255 / 0.72); margin-bottom: var(--space-4); }
      .meter { background: rgb(255 255 255 / 0.2); }
      .meter span { background: #fff; }
    }

    .timer__clock { font-size: var(--text-5xl); font-weight: var(--weight-bold); line-height: 1; }

    .restrict {
      padding: var(--space-4);
      border-radius: var(--radius-md);
      border: 1px solid var(--status-warning-br);
      background: var(--status-warning-bg);
      color: var(--status-warning-fg);
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      font-size: var(--text-sm);
      line-height: var(--leading-loose);

      .subtle { color: inherit; opacity: 0.8; }
    }

    label { display: block; font-size: var(--text-sm); font-weight: var(--weight-bold); margin-bottom: var(--space-2); }

    textarea {
      width: 100%;
      padding: var(--space-3);
      border: 1px solid var(--border-default);
      border-radius: var(--radius-md);
      background: var(--bg-canvas);
      color: var(--fg-default);
      font-size: var(--text-sm);
      resize: vertical;
      margin-bottom: var(--space-2);

      &:focus { outline: none; border-color: var(--border-accent); box-shadow: 0 0 0 3px var(--focus-halo); }
    }
  `],
})
export class Treatments implements OnDestroy {
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  protected readonly store = inject(WorkspaceStore);

  protected readonly running = signal(true);
  protected readonly elapsed = signal(2478);          // seconds
  protected readonly locked = signal(false);
  protected readonly online = signal(false);
  protected readonly queued = signal(2);
  protected readonly notes = signal('');
  protected readonly acknowledged = signal(false);

  protected readonly upcoming = computed(() => this.store.appointments().slice(0, 5));

  private readonly ticker = setInterval(() => {
    if (this.running() && !this.locked()) this.elapsed.update((s) => s + 1);
  }, 1000);

  ngOnDestroy(): void { clearInterval(this.ticker); }

  protected readonly clock = computed(() => {
    const m = Math.floor(this.elapsed() / 60);
    const s = this.elapsed() % 60;
    return `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
  });

  protected readonly progress = computed(() =>
    Math.min(100, Math.round((this.elapsed() / (90 * 60)) * 100)));

  protected toggleTimer(): void {
    if (this.locked()) return;
    this.running.update((r) => !r);
    this.queued.update((q) => q + 1);
    this.toast.info(this.running() ? 'Timer resumed' : 'Timer paused',
      'Queued on this device — it will sync in order, once.');
  }

  protected async complete(): Promise<void> {
    if (!this.acknowledged()) {
      this.toast.warn('Acknowledge the restriction first',
        'The minimum-necessary summary must be acknowledged before a service can be completed.');
      return;
    }
    const ok = await this.confirm.ask({
      title: 'Complete this treatment?',
      consequence: 'The notes lock when you complete. After that they can only be amended, never rewritten, and the amendment is attributed to you.',
      confirmLabel: 'Complete and lock',
    });
    if (!ok) return;

    this.running.set(false);
    this.locked.set(true);
    this.queued.update((q) => q + 1);
    this.toast.success('Treatment complete', 'Notes locked. Queued for sync.');
  }

  protected acknowledge(): void {
    this.acknowledged.set(true);
    this.toast.success('Restriction acknowledged', 'Recorded against your name and this appointment.');
  }

  protected reconnect(): void {
    this.online.set(true);
    const n = this.queued();
    this.queued.set(0);
    this.toast.success('Back online', `${n} queued change${n === 1 ? '' : 's'} applied in order. Nothing duplicated.`);
  }

  protected addProduct(): void {
    this.toast.success('Add-on recorded', 'Arnica balm 50ml — price revalidated against the live catalogue.');
  }

  protected rebook(): void {
    const a = this.store.createAppointment();
    this.toast.success('Rebooked', `${a.service} on the same day next week. Retry is safe — no duplicate created.`);
  }
}
