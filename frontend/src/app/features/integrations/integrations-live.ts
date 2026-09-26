import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { OpsApi } from '../../core/api/ops-api';
import { ToastService } from '../../core/services/toast.service';
import { AuthService } from '../../core/services/auth.service';
import type { MappingDto, OutboxHealthDto, OwnershipDto } from '../../core/models/ops';
import type { ApiProblem } from '../../core/models/api-problem';

/**
 * Integrations against the API (DEC-001/002, MCI-004). Which system owns each
 * capability at this property — the switch into Marquee mode — proposed by
 * one administrator and approved by another; the outbox's health with a
 * replay for what failed; and the external id mappings.
 */
@Component({
  selector: 'app-integrations-live',
  standalone: true,
  imports: [PageHeader, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header eyebrow="Finance & setup" title="Integrations" subtitle="One owner per capability at a time. SpMS never guesses who takes the money." />

    <section class="panel">
      <div class="panel__head"><span class="panel__title">Capability ownership</span></div>
      <div class="panel__body stack">
        <div class="row">
          <select [(ngModel)]="capability" name="c" aria-label="Capability">@for (c of capabilities; track c) { <option [value]="c">{{ c }}</option> }</select>
          <select [(ngModel)]="owner" name="o" aria-label="Owner">@for (o of owners; track o) { <option [value]="o">{{ o }}</option> }</select>
          <button type="button" class="btn btn--secondary" (click)="propose()">Propose</button>
        </div>
        <table class="table" data-testid="ownership">
          <tbody>
            @for (o of ownership(); track o.ownershipId) {
              <tr>
                <td>{{ o.capabilityCode }}</td><td>{{ o.ownerSystem }}</td>
                <td class="subtle numeric">{{ o.effectiveFromUtc.slice(0, 16).replace('T', ' ') }}{{ o.effectiveToUtc ? ' → ' + o.effectiveToUtc.slice(0, 16).replace('T', ' ') : '' }}</td>
                <td><span class="badge" [class.badge--ok]="o.status === 'Active'" [class.badge--warn]="o.status === 'Proposed'">{{ o.status }}</span></td>
                <td class="row row--end">
                  @if (o.status === 'Proposed') {
                    <button type="button" class="btn btn--ghost" [disabled]="o.proposedBy === me()" (click)="decide(o, true)">Approve</button>
                    <button type="button" class="btn btn--ghost" [disabled]="o.proposedBy === me()" (click)="decide(o, false)">Reject</button>
                  }
                </td>
              </tr>
            } @empty { <tr><td class="subtle">No explicit decisions: a standalone property owns its own payment.</td></tr> }
          </tbody>
        </table>
      </div>
    </section>

    <div class="grid grid--halves">
      <section class="panel">
        <div class="panel__head"><span class="panel__title">Outbox</span>
          @if (outbox(); as h) { <span class="badge" [class.badge--ok]="h.failing === 0" [class.badge--danger]="h.failing > 0">{{ h.pending }} pending · {{ h.failing }} failing</span> }
        </div>
        <div class="panel__body">
          <ul class="list">
            @for (p of outbox()?.problems ?? []; track p.eventId) {
              <li class="list__row"><span>{{ p.eventType }} · {{ p.attemptCount }} tries</span><span class="subtle">{{ p.lastError }}</span>
                <button type="button" class="btn btn--ghost" (click)="replay(p.eventId)">Replay</button></li>
            } @empty { <li class="subtle">Nothing failing.</li> }
          </ul>
        </div>
      </section>
      <section class="panel">
        <div class="panel__head"><span class="panel__title">Mappings</span></div>
        <div class="panel__body">
          <ul class="list">
            @for (m of mappings(); track m.mappingId) {
              <li class="list__row"><span>{{ m.entityType }}</span><span class="numeric">{{ m.sourceSystem }} {{ m.sourceKey }}</span><span class="badge">{{ m.status }}</span></li>
            } @empty { <li class="subtle">No external ids yet.</li> }
          </ul>
        </div>
      </section>
    </div>
  `,
  styles: [`
    :host { display: block; }
    .grid--halves { display: grid; gap: var(--space-4); grid-template-columns: 1fr 1fr; margin-top: var(--space-4); }
    @media (max-width: 1000px) { .grid--halves { grid-template-columns: 1fr; } }
    .list { list-style: none; margin: 0; padding: 0; }
    .list__row { display: flex; gap: var(--space-3); justify-content: space-between; align-items: center; padding: var(--space-2) 0; border-bottom: 1px solid var(--border, #e5e5e5); }
  `],
})
export class IntegrationsLive implements OnInit {
  private readonly api = inject(OpsApi);
  private readonly toast = inject(ToastService);
  private readonly auth = inject(AuthService);

  protected readonly capabilities = ['Payment', 'Catalog', 'Guest', 'Inventory', 'Folio'];
  protected readonly owners = ['Spa', 'Marquee', 'Pms', 'Pos', 'External'];
  protected readonly ownership = signal<readonly OwnershipDto[]>([]);
  protected readonly outbox = signal<OutboxHealthDto | null>(null);
  protected readonly mappings = signal<readonly MappingDto[]>([]);
  protected readonly me = computed(() => this.auth.user()?.principalId ?? null);
  protected capability = 'Payment';
  protected owner = 'Marquee';

  ngOnInit(): void { void this.load(); }

  async load(): Promise<void> {
    try { this.ownership.set(await this.api.ownership()); } catch (e) { this.fail(e); }
    try { this.outbox.set(await this.api.outbox()); this.mappings.set(await this.api.mappings()); } catch { /* not every role sees these */ }
  }

  protected async propose(): Promise<void> {
    await this.run(async () => { await this.api.proposeOwnership(this.capability, this.owner); this.toast.success('Change proposed', 'Another administrator approves it.'); });
  }
  protected async decide(o: OwnershipDto, approve: boolean): Promise<void> {
    await this.run(async () => { await this.api.decideOwnership(o.ownershipId, o.rowVersion, approve); this.toast.success(approve ? `${o.ownerSystem} now owns ${o.capabilityCode}` : 'Rejected'); });
  }
  protected async replay(id: string): Promise<void> { await this.run(async () => { await this.api.replay(id); this.toast.success('Event requeued'); }); }

  private async run(work: () => Promise<void>): Promise<void> {
    try { await work(); await this.load(); } catch (e) { this.fail(e); }
  }
  private fail(e: unknown): void {
    const p = e as ApiProblem;
    if (p?.code === 'AUTHORIZATION_DENIED') this.toast.warn('Not permitted', p.detail ?? p.title, p.code);
    else this.toast.error('That did not work', p?.detail ?? p?.title ?? String(e), p?.correlationId);
  }
}
