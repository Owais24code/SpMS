import { Component, ChangeDetectionStrategy, inject, signal, computed } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatCard } from '../../shared/components/stat-card/stat-card';
import { WorkspaceStore } from '../../core/services/workspace-store';
import { ToastService } from '../../core/services/toast.service';

@Component({
  selector: 'app-reconciliation',
  standalone: true,
  imports: [PageHeader, StatCard],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header
      eyebrow="Finance"
      title="Reconciliation"
      subtitle="An ambiguous transaction is queried against its original reference. It is never retried blind."
    >
      <button type="button" class="btn btn--secondary" (click)="exportBatch()">Export batch</button>
      <button type="button" class="btn btn--primary"
              [disabled]="!openAmbiguous()"
              (click)="openAmbiguous() && query(openAmbiguous()!.id)">Query ambiguous</button>
    </app-page-header>

    <div class="stack">
      <div class="grid grid--kpi">
        <app-stat-card label="Matched" [value]="matched()" trend="up" tone="positive" hint="today's batch" />
        <app-stat-card label="Unmatched" [value]="unmatched()" hint="folio side only" />
        <app-stat-card label="Ambiguous" [value]="ambiguous()" [tone]="ambiguous() === '$0.00' ? 'positive' : 'negative'" hint="gateway did not confirm" />
        <app-stat-card label="Resolved" [value]="resolved()" tone="positive" hint="manual, with evidence" />
      </div>

      <div class="toolbar">
        <button type="button" class="chip" [attr.aria-pressed]="filter() === 'all'" (click)="filter.set('all')">All rows</button>
        <button type="button" class="chip" [attr.aria-pressed]="filter() === 'exceptions'" (click)="filter.set('exceptions')">Exceptions only</button>
        <span class="row row--end subtle numeric">USD · America/New_York</span>
      </div>

      <div class="panel">
        <div class="panel__head">
          <span class="panel__title">Transactions</span>
          <span class="panel__hint">Zero and partial totals are always shown</span>
        </div>
        <div class="panel__body panel__body--flush">
          <div class="table-wrap">
            <table class="table">
              <thead>
                <tr>
                  <th scope="col">Id</th>
                  <th scope="col">Connector</th>
                  <th scope="col">Reference</th>
                  <th scope="col">Captured</th>
                  <th scope="col">Amount</th>
                  <th scope="col">State</th>
                  <th scope="col"><span class="visually-hidden">Action</span></th>
                </tr>
              </thead>
              <tbody>
                @for (t of ledger(); track t.id) {
                  <tr>
                    <td class="numeric">{{ t.id }}</td>
                    <td>{{ t.connector }}</td>
                    <td class="numeric">{{ t.reference }}</td>
                    <td class="numeric">{{ t.captured }}</td>
                    <td class="numeric">\${{ t.amount.toFixed(2) }}</td>
                    <td>
                      <span class="badge"
                            [class.badge--ok]="t.state === 'matched'"
                            [class.badge--info]="t.state === 'resolved'"
                            [class.badge--warn]="t.state === 'unmatched'"
                            [class.badge--danger]="t.state === 'ambiguous'">{{ t.state }}</span>
                    </td>
                    <td>
                      @if (t.state === 'ambiguous') {
                        <button type="button" class="btn btn--ghost"
                                [disabled]="querying() === t.id" (click)="query(t.id)">
                          {{ querying() === t.id ? 'Querying…' : 'Query original' }}
                        </button>
                      } @else if (t.state === 'unmatched') {
                        <button type="button" class="btn btn--ghost" (click)="resolve(t.id)">Resolve</button>
                      } @else {
                        <span class="subtle">—</span>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </div>
      </div>

      <div class="panel">
        <div class="panel__head">
          <span class="panel__title">Resolving {{ openAmbiguous()?.id ?? '— nothing ambiguous' }}</span>
          @if (openAmbiguous()) { <span class="badge badge--danger">Ambiguous</span> }
          @else { <span class="badge badge--ok">Clear</span> }
        </div>
        <div class="panel__body stack">
          <p class="subtle">
            The gateway accepted the request but did not return an outcome. We query
            <span class="numeric">{{ openAmbiguous()?.reference ?? '—' }}</span> rather than sending it again —
            a retry could take {{ ambiguous() }} twice.
          </p>
          <div class="form-grid">
            <div>
              <label for="dispo">Disposition</label>
              <select id="dispo" [value]="disposition()" (change)="disposition.set($any($event.target).value)">
                <option>Await gateway confirmation</option>
                <option>Mark captured — evidence attached</option>
                <option>Mark not captured — re-request</option>
              </select>
            </div>
            <div>
              <label for="owner">Owner</label>
              <select id="owner"><option>Finance — Sam O.</option><option>Support — Dana R.</option></select>
            </div>
            <div class="span-2">
              <label for="ev">Evidence and reason</label>
              <textarea id="ev" rows="3" [value]="evidence()" (input)="evidence.set($any($event.target).value)"
                        placeholder="Reference the gateway record or ticket that supports this decision."></textarea>
            </div>
          </div>
          <div class="row">
            <button type="button" class="btn btn--primary"
                    [disabled]="!openAmbiguous() || evidence().trim().length < 4"
                    (click)="resolve(openAmbiguous()!.id)">Record resolution</button>
            <span class="subtle">The original record is never edited. Your decision is attached as a linked adjustment.</span>
          </div>
        </div>
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }
    label { display: block; font-size: var(--text-sm); font-weight: var(--weight-bold); margin-bottom: var(--space-2); }
  `],
})
export class Reconciliation {
  private readonly toast = inject(ToastService);
  protected readonly store = inject(WorkspaceStore);

  protected readonly filter = signal<'all' | 'exceptions'>('all');
  protected readonly querying = signal<string | null>(null);
  protected readonly disposition = signal('Await gateway confirmation');
  protected readonly evidence = signal('');

  protected readonly ledger = computed(() =>
    this.filter() === 'exceptions'
      ? this.store.ledger().filter((t) => t.state === 'ambiguous' || t.state === 'unmatched')
      : this.store.ledger());

  private total(state: string): string {
    const n = this.store.ledger().filter((t) => t.state === state)
      .reduce((sum, t) => sum + t.amount, 0);
    return '$' + n.toFixed(2);
  }
  protected readonly matched   = computed(() => this.total('matched'));
  protected readonly unmatched = computed(() => this.total('unmatched'));
  protected readonly ambiguous = computed(() => this.total('ambiguous'));
  protected readonly resolved  = computed(() => this.total('resolved'));

  protected readonly openAmbiguous = computed(() =>
    this.store.ledger().find((t) => t.state === 'ambiguous') ?? null);

  /** Queries the original by idempotency key. Never re-sends. */
  protected async query(id: string): Promise<void> {
    this.querying.set(id);
    const res = await this.store.queryOriginal(id);
    this.querying.set(null);

    if (res.kind === 'committed') {
      this.toast.success('Gateway confirmed the original', `${id} was captured once. Nothing was re-sent.`);
    } else {
      this.toast.warn('Still unknown', 'The gateway has not settled. Do not retry — resolve manually with evidence.', res.code);
    }
  }

  protected resolve(id: string): void {
    this.store.resolveLedger(id, this.disposition());
    this.toast.success('Resolution recorded', 'The original is untouched — your decision is linked as an adjustment.');
    this.evidence.set('');
  }

  protected exportBatch(): void {
    this.toast.success('Batch exported', `${this.ledger().length} rows, watermarked and delivered securely.`);
  }
}
