import { Component, ChangeDetectionStrategy, signal } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatePanel } from '../../shared/components/state-panel/state-panel';
import { DurationPipe } from '../../shared/pipes/duration.pipe';
import { APPOINTMENTS } from '../../core/data/workspace-data';

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
      <button type="button" class="btn btn--secondary">Add product</button>
      <button type="button" class="btn btn--primary">Rebook guest</button>
    </app-page-header>

    <div class="stack">
      <app-state-panel
        state="offline"
        title="No connection — 2 changes queued"
        body="Your timer and notes are saved on this device. They will sync in order when the connection returns."
      />

      <div class="grid grid--split">
        <div class="panel">
          <div class="panel__head">
            <span class="panel__title">In progress</span>
            <span class="badge badge--info">Guest 4821</span>
            <div class="panel__actions">
              <button type="button" class="btn btn--ghost" (click)="running.set(!running())">
                {{ running() ? 'Pause' : 'Resume' }}
              </button>
              <button type="button" class="btn btn--primary">Complete</button>
            </div>
          </div>

          <div class="panel__body stack">
            <div class="timer">
              <p class="timer__clock numeric">{{ running() ? '41:18' : '41:18' }}</p>
              <p class="subtle">of {{ 90 | duration }} — Deep tissue</p>
              <div class="meter"><span style="width: 46%"></span></div>
            </div>

            <div class="restrict">
              <span class="badge badge--warn">Acknowledge before starting</span>
              <p>Avoid deep pressure on the lower back. Shoulder work is fine.</p>
              <p class="subtle">Minimum-necessary summary. Full intake is not available on this device.</p>
            </div>

            <div>
              <label for="notes">Treatment notes</label>
              <textarea id="notes" rows="4" placeholder="Autosaved and encrypted. Locks when you mark the service complete."></textarea>
              <p class="subtle">Saved 8 seconds ago · queued</p>
            </div>
          </div>
        </div>

        <div class="panel">
          <div class="panel__head"><span class="panel__title">Rest of today</span></div>
          <div class="panel__body panel__body--flush">
            <ul class="timeline" style="padding: var(--space-5)">
              @for (a of upcoming; track a.id) {
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
export class Treatments {
  protected readonly running = signal(true);
  protected readonly upcoming = APPOINTMENTS.slice(0, 5);
}
