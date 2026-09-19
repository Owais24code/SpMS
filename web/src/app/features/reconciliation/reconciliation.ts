import { Component, ChangeDetectionStrategy } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatCard } from '../../shared/components/stat-card/stat-card';
import { LEDGER } from '../../core/data/workspace-data';

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
      <button type="button" class="btn btn--secondary">Export batch</button>
      <button type="button" class="btn btn--primary">Resolve selected</button>
    </app-page-header>

    <div class="stack">
      <div class="grid grid--kpi">
        <app-stat-card label="Matched" value="$1,255.50" delta="4 of 6" trend="up" tone="positive" hint="today's batch" />
        <app-stat-card label="Unmatched" value="$96.00" delta="1" hint="folio side only" />
        <app-stat-card label="Ambiguous" value="$320.00" delta="1" trend="up" tone="negative" hint="gateway did not confirm" />
        <app-stat-card label="Resolved" value="$52.00" delta="1" tone="positive" hint="manual, with evidence" />
      </div>

      <div class="toolbar">
        <button type="button" class="chip" aria-pressed="true">Today</button>
        <button type="button" class="chip">All connectors</button>
        <button type="button" class="chip">Exceptions only</button>
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
                @for (t of ledger; track t.id) {
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
                        <button type="button" class="btn btn--ghost">Query original</button>
                      } @else if (t.state === 'unmatched') {
                        <button type="button" class="btn btn--ghost">Resolve</button>
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
          <span class="panel__title">Resolving txn-8814</span>
          <span class="badge badge--danger">Ambiguous</span>
        </div>
        <div class="panel__body stack">
          <p class="subtle">
            The gateway accepted the request but did not return an outcome. We queried
            <span class="numeric">idem-4a91c4</span> rather than sending it again — a retry could take $320.00 twice.
          </p>
          <div class="form-grid">
            <div>
              <label for="dispo">Disposition</label>
              <select id="dispo">
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
              <textarea id="ev" rows="3" placeholder="Reference the gateway record or ticket that supports this decision."></textarea>
            </div>
          </div>
          <p class="subtle">
            The original record is never edited. Your decision is attached as a linked adjustment.
          </p>
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
  protected readonly ledger = LEDGER;
}
