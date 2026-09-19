import { Component, ChangeDetectionStrategy } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { DEVICES } from '../../core/data/workspace-data';

@Component({
  selector: 'app-devices',
  standalone: true,
  imports: [PageHeader],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header
      eyebrow="Operations"
      title="Devices and quiet notification"
      subtitle="Pagers carry an anonymous token, never a guest name, so a device left on a counter reveals nothing."
    >
      <button type="button" class="btn btn--secondary">Mark returned</button>
      <button type="button" class="btn btn--primary">Assign device</button>
    </app-page-header>

    <div class="stack">
      <div class="toolbar">
        <button type="button" class="chip" aria-pressed="true">All</button>
        <button type="button" class="chip">Pagers</button>
        <button type="button" class="chip">Lockers</button>
        <button type="button" class="chip">Low battery</button>
        <span class="row row--end subtle">1 device out of service</span>
      </div>

      <div class="grid grid--cards">
        @for (d of devices; track d.id) {
          <article class="dev" [class.is-down]="!d.online">
            <header class="dev__head">
              <span class="dev__id numeric">{{ d.id }}</span>
              <span class="badge"
                    [class.badge--ok]="d.state === 'available'"
                    [class.badge--info]="d.state === 'assigned'"
                    [class.badge--warn]="d.state === 'cleaning'"
                    [class.badge--danger]="d.state === 'out-of-service'">{{ d.state }}</span>
            </header>

            <p class="dev__kind">{{ d.kind }}</p>

            <div class="dev__battery">
              <div class="meter" [class.meter--warn]="d.battery < 50" [class.meter--danger]="d.battery < 20">
                <span [style.width.%]="d.battery"></span>
              </div>
              <span class="subtle numeric">{{ d.battery }}%</span>
            </div>

            <footer class="dev__foot">
              <span class="subtle">{{ d.assignedToken ? 'Token ' + d.assignedToken : 'Unassigned' }}</span>
              <span class="badge" [class.badge--ok]="d.online" [class.badge--danger]="!d.online">
                {{ d.online ? 'Online' : 'Offline' }}
              </span>
            </footer>
          </article>
        }
      </div>

      <div class="panel">
        <div class="panel__head"><span class="panel__title">Alert and escalation</span></div>
        <div class="panel__body">
          <div class="form-grid">
            <div>
              <label for="pattern">Alert pattern</label>
              <select id="pattern"><option>Haptic + soft light</option><option>Haptic only</option><option>Light only</option></select>
            </div>
            <div>
              <label for="escal">Escalate after</label>
              <select id="escal"><option>90 seconds</option><option>2 minutes</option><option>5 minutes</option></select>
            </div>
            <div>
              <label for="fallback">If unacknowledged</label>
              <select id="fallback"><option>Notify front desk discreetly</option><option>Notify duty manager</option></select>
            </div>
          </div>
          <p class="subtle" style="margin-top: var(--space-4)">
            Guest preference overrides the default pattern. Audible alerts are never used in treatment areas.
          </p>
        </div>
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }

    .dev {
      padding: var(--space-5);
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-lg);
      background: var(--grad-surface);
      display: flex;
      flex-direction: column;
      gap: var(--space-3);
      transition: border-color var(--dur-base) var(--ease-out), transform var(--dur-base) var(--ease-out);

      &:hover { border-color: var(--border-accent); transform: translateY(-2px); }
      &.is-down { opacity: 0.72; }
    }

    .dev__head { display: flex; align-items: center; gap: var(--space-3); }
    .dev__id { font-weight: var(--weight-bold); font-size: var(--text-base); }
    .dev__kind { font-size: var(--text-xs); color: var(--fg-subtle); margin-top: calc(var(--space-3) * -1); }
    .dev__battery { display: flex; align-items: center; gap: var(--space-3); .meter { flex: 1; } }
    .dev__foot { display: flex; align-items: center; justify-content: space-between; gap: var(--space-2); }

    label { display: block; font-size: var(--text-sm); font-weight: var(--weight-bold); margin-bottom: var(--space-2); }
  `],
})
export class Devices {
  protected readonly devices = DEVICES;
}
