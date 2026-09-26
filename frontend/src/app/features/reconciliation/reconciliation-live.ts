import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatCard } from '../../shared/components/stat-card/stat-card';
import { StatePanel } from '../../shared/components/state-panel/state-panel';
import { CommerceApi } from '../../core/api/commerce-api';
import { ToastService } from '../../core/services/toast.service';
import { browserToday } from '../../core/services/board-time';
import { isAmbiguous, money } from '../../core/models/commerce';
import type { IntentDto, ReconciliationDto } from '../../core/models/commerce';
import type { ApiProblem } from '../../core/models/api-problem';

/**
 * Reconciliation against the API (COM-007, BR-014). The day's money by
 * tender in the property's own day; any payment the provider never
 * confirmed, queried against its original (never retried); and refunds
 * waiting for a second person to approve them.
 */
@Component({
  selector: 'app-reconciliation-live',
  standalone: true,
  imports: [PageHeader, StatCard, StatePanel, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header eyebrow="Finance" title="Reconciliation"
      subtitle="An unconfirmed payment is queried against its original. It is never retried blind.">
      <label class="inline">Day <input type="date" [ngModel]="date()" (ngModelChange)="date.set($event); load()" name="day" aria-label="Day" /></label>
      <button type="button" class="btn btn--primary" [disabled]="busy() || !day()?.ambiguous?.length" (click)="resolveAll()">Query all unconfirmed</button>
    </app-page-header>

    @if (problem(); as p) {
      <app-state-panel [state]="p.code === 'AUTHORIZATION_DENIED' ? 'denied' : 'error'" [body]="p.detail ?? p.title" />
    } @else if (day(); as d) {
      <div class="stack">
        <div class="grid grid--kpi">
          <app-stat-card label="Net taken" [value]="fmt(d.netMinor)" hint="sales less refunds" tone="positive" />
          <app-stat-card label="Card" [value]="fmt(card())" hint="approved sales" />
          <app-stat-card label="Unconfirmed" [value]="String(d.ambiguous.length)" [tone]="d.ambiguous.length ? 'negative' : 'positive'" hint="waiting for the provider" />
          <app-stat-card label="Refunds to approve" [value]="String(d.refundsAwaitingApproval.length)" hint="dual control" />
        </div>

        <div class="panel">
          <div class="panel__head"><span class="panel__title">By tender</span><span class="panel__hint numeric">{{ d.date }} · {{ d.timeZone }}</span></div>
          <div class="panel__body panel__body--flush">
            <table class="table" data-testid="tender-totals">
              <thead><tr><th scope="col">Tender</th><th scope="col">Type</th><th scope="col" class="numeric">Count</th><th scope="col" class="numeric">Amount</th></tr></thead>
              <tbody>
                @for (t of d.totals; track t.tenderCode + t.transactionType) {
                  <tr><td>{{ t.tenderCode }}</td><td>{{ t.transactionType }}</td><td class="numeric">{{ t.count }}</td><td class="numeric">{{ fmt(t.amountMinor) }}</td></tr>
                } @empty {
                  <tr><td colspan="4" class="subtle">Nothing taken on this day.</td></tr>
                }
              </tbody>
            </table>
          </div>
        </div>

        <div class="panel">
          <div class="panel__head"><span class="panel__title">Unconfirmed payments</span></div>
          <div class="panel__body panel__body--flush">
            <table class="table">
              <tbody>
                @for (i of d.ambiguous; track i.paymentIntentId) {
                  <tr>
                    <td>{{ i.purpose }}</td><td class="numeric">{{ fmt(i.amountMinor) }}</td><td class="numeric subtle">{{ i.createdUtc.slice(11, 16) }} UTC</td>
                    <td><button type="button" class="btn btn--ghost" [disabled]="busy()" (click)="query(i)">Query original</button></td>
                  </tr>
                } @empty {
                  <tr><td class="subtle">Every payment has an answer.</td></tr>
                }
              </tbody>
            </table>
          </div>
        </div>

        <div class="panel">
          <div class="panel__head"><span class="panel__title">Refunds awaiting approval</span></div>
          <div class="panel__body panel__body--flush">
            <table class="table" data-testid="refunds">
              <tbody>
                @for (r of d.refundsAwaitingApproval; track r.paymentIntentId) {
                  <tr>
                    <td>{{ r.reasonCode }}</td><td class="numeric">{{ fmt(r.amountMinor) }}</td>
                    <td class="row row--end">
                      <button type="button" class="btn btn--primary" [disabled]="busy()" (click)="approve(r)">Approve</button>
                      <button type="button" class="btn btn--ghost" [disabled]="busy()" (click)="reject(r)">Reject</button>
                    </td>
                  </tr>
                } @empty {
                  <tr><td class="subtle">No refunds waiting.</td></tr>
                }
              </tbody>
            </table>
          </div>
        </div>
      </div>
    } @else {
      <app-state-panel state="loading" />
    }
  `,
  styles: [`
    :host { display: block; }
    .inline { display: inline-flex; gap: var(--space-2); align-items: center; font-size: var(--text-sm); }
  `],
})
export class ReconciliationLive implements OnInit {
  private readonly api = inject(CommerceApi);
  private readonly toast = inject(ToastService);

  protected readonly date = signal(browserToday());
  protected readonly day = signal<ReconciliationDto | null>(null);
  protected readonly problem = signal<ApiProblem | null>(null);
  protected readonly busy = signal(false);
  protected readonly String = String;

  protected readonly card = computed(() =>
    (this.day()?.totals ?? []).filter((t) => t.tenderCode === 'Card' && t.transactionType === 'Sale').reduce((s, t) => s + t.amountMinor, 0));

  protected fmt(minor: number): string { return money(minor); }

  ngOnInit(): void { void this.load(); }

  async load(): Promise<void> {
    try {
      this.day.set(await this.api.reconciliation(this.date()));
      this.problem.set(null);
    } catch (e) { this.problem.set(e as ApiProblem); }
  }

  protected async query(i: IntentDto): Promise<void> {
    await this.run(async () => {
      const r = await this.api.resolve(i.paymentIntentId);
      if (isAmbiguous(r)) this.toast.warn('Still unconfirmed', 'The provider cannot say yet. Do not retry — query again later.', r.code);
      else this.toast.success(`The provider says: ${r.outcome}`, money(r.intent.amountMinor));
    });
  }

  protected async resolveAll(): Promise<void> {
    await this.run(async () => {
      const r = await this.api.resolveAllAmbiguous();
      this.toast.success('Queried the provider', `${r.settled} settled.`);
    });
  }

  protected async approve(r: IntentDto): Promise<void> {
    await this.run(async () => {
      await this.api.approveRefund(r.paymentIntentId, r.rowVersion);
      this.toast.success('Refund approved', money(r.amountMinor));
    });
  }

  protected async reject(r: IntentDto): Promise<void> {
    await this.run(async () => {
      await this.api.rejectRefund(r.paymentIntentId, r.rowVersion, 'Rejected at reconciliation');
      this.toast.info('Refund rejected', money(r.amountMinor));
    });
  }

  private async run(work: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    try { await work(); await this.load(); }
    catch (e) {
      const p = e as ApiProblem;
      this.toast.error(p.code === 'AUTHORIZATION_DENIED' ? 'Not permitted' : 'That did not work', p.detail ?? p.title, p.correlationId);
    } finally { this.busy.set(false); }
  }
}
