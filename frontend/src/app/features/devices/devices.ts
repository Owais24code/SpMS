import { Component, ChangeDetectionStrategy, inject, signal, computed } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { WorkspaceStore } from '../../core/services/workspace-store';
import { ToastService } from '../../core/services/toast.service';

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
      <button type="button" class="btn btn--secondary" (click)="returnAll()">Return all</button>
      <button type="button" class="btn btn--primary" (click)="assignNext()">Assign device</button>
    </app-page-header>

    <div class="stack">
      <div class="toolbar">
        <button type="button" class="chip" [attr.aria-pressed]="filter() === 'all'" (click)="filter.set('all')">All</button>
        <button type="button" class="chip" [attr.aria-pressed]="filter() === 'Pager'" (click)="filter.set('Pager')">Pagers</button>
        <button type="button" class="chip" [attr.aria-pressed]="filter() === 'Locker'" (click)="filter.set('Locker')">Lockers</button>
        <button type="button" class="chip" [attr.aria-pressed]="filter() === 'low'" (click)="filter.set('low')">Low battery</button>
        <span class="row row--end subtle numeric">{{ outOfService() }} out of service</span>
      </div>

      <div class="grid grid--cards">
        @for (d of devices(); track d.id) {
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
              <button type="button" class="btn btn--ghost" (click)="act(d.id, d.state)">{{ label(d.state) }}</button>
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
          <div class="row" style="margin-top: var(--space-4)">
            <button type="button" class="btn btn--primary" (click)="saveAlerts()">Save alert policy</button>
            <span class="subtle">Guest preference overrides the default. Audible alerts are never used in treatment areas.</span>
          </div>
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
  private readonly toast = inject(ToastService);
  protected readonly store = inject(WorkspaceStore);

  protected readonly filter = signal<'all' | 'Pager' | 'Locker' | 'low'>('all');

  protected readonly devices = computed(() => {
    const f = this.filter();
    return this.store.devices().filter((d) =>
      f === 'all' ? true : f === 'low' ? d.battery < 50 : d.kind === f);
  });

  protected readonly outOfService = computed(() =>
    this.store.devices().filter((d) => d.state === 'out-of-service').length);

  protected act(id: string, state: string): void {
    if (state === 'available') { this.store.assignDevice(id); this.toast.success('Device assigned', `${id} paired to an anonymous token. No guest name is written to it.`); }
    else if (state === 'assigned') { this.store.returnDevice(id); this.toast.success('Device returned', `${id} moved to cleaning.`); }
    else { this.store.restoreDevice(id); this.toast.success('Back in service', id); }
  }

  protected label(state: string): string {
    return state === 'available' ? 'Assign' : state === 'assigned' ? 'Return' : 'Mark available';
  }

  protected assignNext(): void {
    const free = this.store.devices().find((d) => d.state === 'available');
    if (!free) { this.toast.warn('Nothing free', 'Every device is assigned, cleaning or out of service.'); return; }
    this.act(free.id, 'available');
  }

  protected returnAll(): void {
    const assigned = this.store.devices().filter((d) => d.state === 'assigned');
    if (!assigned.length) { this.toast.info('Nothing to return', 'No device is currently assigned.'); return; }
    assigned.forEach((d) => this.store.returnDevice(d.id));
    this.toast.success(`${assigned.length} devices returned`, 'All moved to cleaning.');
  }

  protected saveAlerts(): void {
    this.toast.success('Alert policy saved', 'Applies to new assignments. Guest preference still overrides the default pattern.');
  }
}
