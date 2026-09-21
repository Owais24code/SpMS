import { Component, ChangeDetectionStrategy, inject, computed } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { WorkspaceStore } from '../../core/services/workspace-store';
import { ToastService } from '../../core/services/toast.service';
import { ConfirmService } from '../../core/services/confirm.service';

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
      <button type="button" class="btn btn--secondary" [disabled]="preflight() === 'running'" (click)="runPreflight()">
        {{ preflight() === 'running' ? 'Running…' : 'Run preflight' }}
      </button>
      <button type="button" class="btn btn--primary" (click)="propose()">Propose change</button>
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
                @for (o of owners(); track o.capability) {
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
            @switch (preflight()) {
              @case ('running') { <span class="badge badge--info">Running</span> }
              @case ('pass')    { <span class="badge badge--ok">All passed</span> }
              @case ('fail')    { <span class="badge badge--warn">1 failure</span> }
              @default          { <span class="badge badge--neutral">Not run</span> }
            }
          </div>
          <div class="panel__body">
            @if (preflight() === 'idle') {
              <p class="subtle">Preflight has not run for this change yet.</p>
            } @else {
              <ul class="checks">
                @for (c of checks(); track c.label) {
                  <li [class.ok]="c.ok" [class.bad]="!c.ok">
                    <span>{{ c.label }}</span>
                    <span class="badge" [class.badge--ok]="c.ok" [class.badge--danger]="!c.ok">
                      {{ c.ok ? 'Pass' : 'Fail' }}
                    </span>
                  </li>
                }
              </ul>
            }
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
            <button type="button" class="btn btn--secondary" (click)="selfApprove()">Approve</button>
            <p class="subtle">Try it — the refusal is the feature.</p>
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
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  protected readonly store = inject(WorkspaceStore);

  protected readonly owners = computed(() => this.store.owners());
  protected readonly preflight = computed(() => this.store.preflight());

  protected readonly checks = computed(() => {
    const failed = this.preflight() === 'fail';
    return [
      { label: 'Field mappings complete',    ok: true },
      { label: 'Queues drained',             ok: true },
      { label: 'Reconciliation window agreed', ok: true },
      { label: 'Outage readiness runbook',   ok: !failed },
    ];
  });

  protected async runPreflight(): Promise<void> {
    const outcome = await this.store.runPreflight();
    if (outcome === 'pass') {
      this.toast.success('Preflight passed', 'Activation is unblocked, pending a second approver.');
    } else {
      this.toast.warn('Preflight failed', 'The outage readiness runbook is missing. Activation stays blocked.', 'SPMS-CFG-002');
    }
  }

  protected async propose(): Promise<void> {
    const ok = await this.confirm.ask({
      title: 'Propose a new owner for Linen ledger?',
      consequence: 'The change takes effect at the timestamp you set, not immediately. You will not be able to approve it yourself.',
      confirmLabel: 'Propose change',
    });
    if (!ok) return;
    this.store.proposeChange('Linen ledger');
    this.toast.success('Change proposed', 'Waiting on a second approver — four-eyes is enforced.');
  }

  /** Always refused: the proposer cannot self-approve. */
  protected selfApprove(): void {
    this.toast.error(
      'You proposed this change',
      'A second approver must sign it off. This is enforced server-side, not just hidden here.',
      'SPMS-CFG-003',
    );
  }
}
