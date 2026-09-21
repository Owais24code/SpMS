import { Component, ChangeDetectionStrategy, inject, signal, computed } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatCard } from '../../shared/components/stat-card/stat-card';
import { StatePanel } from '../../shared/components/state-panel/state-panel';
import { WorkspaceStore } from '../../core/services/workspace-store';
import { ToastService } from '../../core/services/toast.service';
import { AuthService } from '../../core/services/auth.service';
import { SCOPES, API_ERROR } from '../../core/models/contract';

@Component({
  selector: 'app-reports',
  standalone: true,
  imports: [PageHeader, StatCard, StatePanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header
      eyebrow="Finance"
      title="Reports"
      subtitle="Every figure links to its definition, and exports carry the filters and definitions that produced them."
    >
      <button type="button" class="btn btn--secondary" (click)="definitions()">Manage definitions</button>
      <button type="button" class="btn btn--primary" (click)="exportReport()">Export</button>
    </app-page-header>

    <div class="stack">
      <div class="toolbar">
        <button type="button" class="chip" [attr.aria-pressed]="scopeFilter() === 'property'" (click)="scopeFilter.set('property')">Riverside Spa</button>
        <button type="button" class="chip" [attr.aria-pressed]="scopeFilter() === 'all'" (click)="scopeFilter.set('all')">All properties</button>
        <button type="button" class="chip" [attr.aria-pressed]="store.range() === '7'" (click)="store.range.set('7')">7 days</button>
        <button type="button" class="chip" [attr.aria-pressed]="store.range() === '30'" (click)="store.range.set('30')">30 days</button>
        <span class="row row--end subtle">Tie-out: matched at 12:19</span>
      </div>

      <section aria-labelledby="ops-h">
        <h2 class="sec" id="ops-h">Operational</h2>
        <div class="grid grid--kpi">
          <app-stat-card label="Appointments" value="497" delta="+8%" trend="up" tone="positive" hint="vs. last week" />
          <app-stat-card label="Utilisation" value="78%" delta="+4pt" trend="up" tone="positive" hint="therapist hours filled" />
          <app-stat-card label="On-time arrivals" value="91%" delta="-2pt" trend="down" tone="negative" hint="within 5 minutes" />
          <app-stat-card label="Conflicts raised" value="14" delta="3 hard" tone="negative" hint="hard are non-overridable" />
        </div>
      </section>

      <div class="grid grid--split">
        <div class="panel">
          <div class="panel__head">
            <span class="panel__title">Bookings by day</span>
            <span class="panel__hint">Property local</span>
          </div>
          <div class="panel__body">
            <div class="chart" role="img" aria-label="Bookings by day, rising from 52 on Monday to 95 on Saturday.">
              @for (v of bookings(); track $index) {
                <div class="chart__col">
                  <span class="chart__bar" [style.height.%]="pct(v)">
                    <span class="chart__value numeric">{{ v }}</span>
                  </span>
                  <span class="chart__label">{{ labels()[$index] }}</span>
                </div>
              }
            </div>
          </div>
        </div>

        <div class="panel">
          <div class="panel__head">
            <span class="panel__title">Service mix</span>
          </div>
          <div class="panel__body">
            <ul class="mix">
              @for (m of mix; track m.name) {
                <li>
                  <span class="mix__name">{{ m.name }}</span>
                  <div class="meter"><span [style.width.%]="m.pct" [style.background]="m.color"></span></div>
                  <span class="numeric subtle">{{ m.pct }}%</span>
                </li>
              }
            </ul>
          </div>
        </div>
      </div>

      <section aria-labelledby="fin-h">
        <h2 class="sec" id="fin-h">Financial</h2>
        @if (auth.has(SCOPES.commerce)) {
          <div class="grid grid--kpi">
            <app-stat-card label="Treatment revenue" value="$48,210" delta="+9%" trend="up" tone="positive" hint="tied out 12:19" />
            <app-stat-card label="Retail" value="$6,840" delta="+3%" trend="up" tone="positive" hint="tied out 12:19" />
            <app-stat-card label="Refunds" value="$1,120" delta="-14%" trend="down" tone="positive" hint="lower is better" />
            <app-stat-card label="Unreconciled" value="$416" tone="negative" hint="see Reconciliation" />
          </div>
        } @else {
          <app-state-panel
            state="denied"
            title="Financial figures need a finance role"
            body="You can see that this section exists and when it was last tied out. Sign in as Finance to see the values."
          />
        }
      </section>
    </div>
  `,
  styles: [`
    :host { display: block; }

    .sec {
      font-size: var(--text-lg);
      margin-bottom: var(--space-4);
    }

    .chart { display: flex; align-items: flex-end; gap: var(--space-3); height: 200px; }

    .chart__col {
      flex: 1;
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: var(--space-2);
      height: 100%;
      justify-content: flex-end;
    }

    .chart__bar {
      position: relative;
      width: 100%;
      max-width: 44px;
      border-radius: var(--radius-sm) var(--radius-sm) 0 0;
      background: var(--grad-accent);
    }

    .chart__value {
      position: absolute;
      top: -20px; inset-inline: 0;
      text-align: center;
      font-size: var(--text-xs);
      font-weight: var(--weight-bold);
      color: var(--fg-muted);
    }

    .chart__label { font-size: var(--text-xs); color: var(--fg-subtle); }

    .mix {
      list-style: none;
      display: flex;
      flex-direction: column;
      gap: var(--space-4);

      li { display: flex; align-items: center; gap: var(--space-3); }

      &__name { width: 110px; font-size: var(--text-sm); flex-shrink: 0; }

      .meter { flex: 1; }
    }
  `],
})
export class Reports {
  private readonly toast = inject(ToastService);
  protected readonly store = inject(WorkspaceStore);
  protected readonly auth = inject(AuthService);

  protected readonly scopeFilter = signal<'property' | 'all'>('property');

  /** Exposed so the template can gate the financial section on the real scope. */
  protected readonly SCOPES = SCOPES;

  protected readonly labels = computed(() => this.store.rangeLabels());
  protected readonly bookings = computed(() =>
    this.scopeFilter() === 'all'
      ? this.store.series().map((v) => Math.round(v * 2.4))
      : this.store.series());
  private readonly peakOf = computed(() => Math.max(...this.bookings()));

  protected exportReport(): void {
    if (!this.auth.has(SCOPES.commerce)) {
      this.toast.error('Export needs a finance role',
        'Your role can read operational figures but not export them.', API_ERROR.authorizationDenied.code);
      return;
    }
    this.toast.success('Export queued',
      'Watermarked, with the filters and metric definitions attached.');
  }

  protected definitions(): void {
    this.toast.info('Definitions', 'Utilisation = booked therapist minutes ÷ rostered minutes, excluding blocked rooms.');
  }

  protected readonly mix = [
    { name: 'Deep tissue',  pct: 34, color: 'var(--grad-data-6)' },
    { name: 'Aromatherapy', pct: 24, color: 'var(--grad-data-5)' },
    { name: 'Facial',       pct: 18, color: 'var(--grad-data-4)' },
    { name: 'Hot stone',    pct: 14, color: 'var(--grad-data-3)' },
    { name: 'Couples',      pct: 10, color: 'var(--grad-data-2)' },
  ];

  protected pct(v: number): number {
    return Math.round((v / this.peakOf()) * 100);
  }
}
