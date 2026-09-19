import { Component, ChangeDetectionStrategy } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { OWNERS } from '../../core/data/workspace-data';

@Component({
  selector: 'app-integrations',
  standalone: true,
  imports: [PageHeader],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header
      eyebrow="Setup"
      title="Integrations and ownership"
      subtitle="Exactly one system owns each capability at any moment. Changes take effect at a declared time and need a second approver."
    >
      <button type="button" class="btn btn--secondary">Run preflight</button>
      <button type="button" class="btn btn--primary">Propose change</button>
    </app-page-header>

    <div class="stack">
      <div class="panel">
        <div class="panel__head"><span class="panel__title">Capability owners</span></div>
        <div class="panel__body panel__body--flush">
          <div class="table-wrap">
            <table class="table">
              <thead>
                <tr>
                  <th scope="col">Capability</th>
                  <th scope="col">Owner</th>
                  <th scope="col">Effective</th>
                  <th scope="col">Dependencies</th>
                  <th scope="col">State</th>
                </tr>
              </thead>
              <tbody>
                @for (o of owners; track o.capability) {
                  <tr>
                    <td>{{ o.capability }}</td>
                    <td>{{ o.owner }}</td>
                    <td class="numeric">{{ o.effective }}</td>
                    <td class="numeric">{{ o.dependencies }}</td>
                    <td>
                      <span class="badge"
                            [class.badge--ok]="o.state === 'active'"
                            [class.badge--info]="o.state === 'pending'"
                            [class.badge--warn]="o.state === 'awaiting-approval'"
                            [class.badge--neutral]="o.state === 'rolled-back'">{{ o.state }}</span>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </div>
      </div>

      <div class="grid grid--split">
        <div class="panel">
          <div class="panel__head">
            <span class="panel__title">Preflight — credential issue</span>
            <span class="badge badge--warn">1 failure</span>
          </div>
          <div class="panel__body">
            <ul class="checks">
              <li class="ok"><span>Field mappings complete</span><span class="badge badge--ok">Pass</span></li>
              <li class="ok"><span>Queues drained</span><span class="badge badge--ok">Pass</span></li>
              <li class="ok"><span>Reconciliation window agreed</span><span class="badge badge--ok">Pass</span></li>
              <li class="bad"><span>Outage readiness runbook</span><span class="badge badge--danger">Fail</span></li>
            </ul>
            <p class="subtle" style="margin-top: var(--space-4)">
              Activation is blocked while any check fails. Fix the runbook and re-run.
            </p>
          </div>
        </div>

        <div class="panel">
          <div class="panel__head"><span class="panel__title">Approval</span></div>
          <div class="panel__body stack">
            <ul class="timeline">
              <li class="is-done">
                <p class="timeline__when numeric">16 Sep, 10:22</p>
                <p class="timeline__what">Proposed by Owais K.</p>
                <p class="timeline__note">Effective 01 Oct 2026, 02:00 property local</p>
              </li>
              <li class="is-active">
                <p class="timeline__what">Awaiting a second approver</p>
                <p class="timeline__note">You proposed this change, so you cannot approve it.</p>
              </li>
              <li>
                <p class="timeline__what">Activation</p>
                <p class="timeline__note">Rollback stays available for 14 days after cutover.</p>
              </li>
            </ul>
            <button type="button" class="btn btn--secondary" disabled>Approve</button>
          </div>
        </div>
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }

    .checks {
      list-style: none;
      display: flex;
      flex-direction: column;

      li {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: var(--space-3);
        padding: var(--space-3) 0;
        border-bottom: 1px solid var(--border-subtle);
        font-size: var(--text-sm);

        &:last-child { border-bottom: 0; }
        &.bad > span:first-child { font-weight: var(--weight-bold); }
      }
    }
  `],
})
export class Integrations {
  protected readonly owners = OWNERS;
}
