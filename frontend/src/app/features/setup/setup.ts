import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatePanel } from '../../shared/components/state-panel/state-panel';
import { ReferenceApi } from '../../core/api/reference-api';
import { ToastService } from '../../core/services/toast.service';
import { AuthService } from '../../core/services/auth.service';
import { environment } from '../../../environments/environment';
import { money } from '../../core/models/commerce';
import type { ClosureDto, OfferingRowDto, RoomDto, SettingDto } from '../../core/models/reference';
import type { ApiProblem } from '../../core/models/api-problem';

type Tab = 'catalogue' | 'rooms' | 'settings';

/**
 * The property's setup against the API: the tenant catalogue and this
 * property's prices (§53.2 Catalog), rooms and their closures (CON-002), and
 * governed settings, each change proposed by one person and approved by
 * another (SEC-014). The server decides who may do what; buttons a role
 * cannot use answer with the reason.
 */
@Component({
  selector: 'app-setup',
  standalone: true,
  imports: [PageHeader, StatePanel, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header eyebrow="Finance & setup" title="Setup"
      subtitle="The menu and its prices, rooms and closures, and the policies every screen follows." />

    @if (!live) {
      <app-state-panel state="first-use" title="Setup runs against the API" body="Start the workspace with the API to change reference data." />
    } @else {
      <div class="tabs" role="tablist">
        <button type="button" role="tab" [attr.aria-selected]="tab() === 'catalogue'" (click)="tab.set('catalogue')">Catalogue</button>
        <button type="button" role="tab" [attr.aria-selected]="tab() === 'rooms'" (click)="tab.set('rooms')">Rooms</button>
        <button type="button" role="tab" [attr.aria-selected]="tab() === 'settings'" (click)="tab.set('settings')">Policies</button>
      </div>

      @switch (tab()) {
        @case ('catalogue') {
          <section class="panel">
            <div class="panel__head"><span class="panel__title">Services</span><span class="panel__hint">prices are this property's</span></div>
            <div class="panel__body stack">
              <div class="row">
                <input [(ngModel)]="svc.name" name="sn" aria-label="Service name" placeholder="Name" />
                <input [(ngModel)]="svc.code" name="sc" aria-label="Service code" placeholder="code-like-this" />
                <input type="number" [(ngModel)]="svc.duration" name="sd" aria-label="Duration in minutes" class="narrow" />
                <input type="number" [(ngModel)]="svc.price" name="sp" aria-label="Price in dollars" class="narrow" />
                <button type="button" class="btn btn--secondary" (click)="createService()">Draft service</button>
              </div>
              <table class="table" data-testid="services">
                <thead><tr><th scope="col">Service</th><th scope="col">Minutes</th><th scope="col" class="numeric">Price here</th><th scope="col">Status</th><th scope="col"></th></tr></thead>
                <tbody>
                  @for (o of offering(); track o.service.serviceId) {
                    <tr>
                      <td>{{ o.service.name }} <span class="subtle">{{ o.service.code }}</span></td>
                      <td>{{ o.service.durationMinutes }}</td>
                      <td class="numeric">
                        <input type="number" class="narrow" [ngModel]="o.effectivePriceMinor / 100" (ngModelChange)="prices[o.service.serviceId] = $event"
                               [name]="'price-' + o.service.serviceId" [attr.aria-label]="'Price of ' + o.service.name" />
                        <button type="button" class="btn btn--ghost" (click)="setPrice(o)">Set</button>
                      </td>
                      <td><span class="badge" [class.badge--ok]="o.service.status === 'Active'">{{ o.service.status }}</span>
                          @if (!o.offered) { <span class="subtle">not offered here</span> }</td>
                      <td class="row row--end">
                        @if (o.service.status === 'Draft' || o.service.status === 'Inactive') { <button type="button" class="btn btn--ghost" (click)="transition(o, 'Active')">Activate</button> }
                        @if (o.service.status === 'Active') { <button type="button" class="btn btn--ghost" (click)="transition(o, 'Inactive')">Withdraw</button> }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          </section>
        }
        @case ('rooms') {
          <section class="panel">
            <div class="panel__head"><span class="panel__title">Rooms</span></div>
            <div class="panel__body stack">
              <div class="row">
                <input [(ngModel)]="room.code" name="rc" aria-label="Room code" placeholder="T9" class="narrow" />
                <input [(ngModel)]="room.name" name="rn" aria-label="Room name" placeholder="Treatment 9" />
                <select [(ngModel)]="room.type" name="rt" aria-label="Room type"><option>TreatmentRoom</option><option>WetRoom</option><option>CoupleRoom</option><option>Chair</option></select>
                <button type="button" class="btn btn--secondary" (click)="createRoom()">Add room</button>
              </div>
              <table class="table" data-testid="rooms">
                <tbody>
                  @for (r of rooms(); track r.roomId) {
                    <tr>
                      <td>{{ r.code }}</td><td>{{ r.name }}</td><td>{{ r.resourceType }}</td>
                      <td><span class="badge" [class.badge--ok]="r.status === 'Active'" [class.badge--warn]="r.status === 'OutOfService'">{{ r.status }}</span></td>
                      <td class="row row--end">
                        <button type="button" class="btn btn--ghost" (click)="toggleRoom(r)">{{ r.status === 'Active' ? 'Take out of service' : 'Return to service' }}</button>
                        <button type="button" class="btn btn--ghost" (click)="closing.set(r)">Close for a time…</button>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
              @if (closing(); as r) {
                <div class="row callout">
                  <strong>Close {{ r.name }}</strong>
                  <input type="datetime-local" [(ngModel)]="closure.starts" name="cs" aria-label="Closed from" />
                  <input type="datetime-local" [(ngModel)]="closure.ends" name="ce" aria-label="Closed until" />
                  <input [(ngModel)]="closure.reason" name="cr" aria-label="Reason" placeholder="Maintenance" />
                  <button type="button" class="btn btn--primary" (click)="closeRoom(r)">Close room</button>
                  <button type="button" class="btn btn--ghost" (click)="closing.set(null)">Cancel</button>
                </div>
              }
              <h3 class="h-sub">Closures</h3>
              <ul class="list">
                @for (c of closures(); track c.maintenanceWindowId) {
                  <li class="list__row"><span>{{ roomName(c.roomId) }} · {{ local(c.startsUtc) }} – {{ local(c.endsUtc) }} · {{ c.reasonCode }}</span>
                    <span class="badge">{{ c.status }}</span>
                    @if (c.status === 'Planned' || c.status === 'Active') { <button type="button" class="btn btn--ghost" (click)="cancelClosure(c)">Reopen</button> }
                  </li>
                } @empty { <li class="subtle">No closures.</li> }
              </ul>
            </div>
          </section>
        }
        @case ('settings') {
          <section class="panel">
            <div class="panel__head"><span class="panel__title">Policies</span><span class="panel__hint">proposed by one person, approved by another</span></div>
            <div class="panel__body stack">
              <div class="form-grid">
                <label>Policy
                  <select id="pol-key" [(ngModel)]="proposal.key" name="pk" (ngModelChange)="proposal.value = template($event)">
                    @for (k of keys; track k) { <option [value]="k">{{ k }}</option> }
                  </select>
                </label>
                <label>Value (JSON) <input [(ngModel)]="proposal.value" name="pv" aria-label="Policy value" /></label>
                <label class="span-2">Why <input [(ngModel)]="proposal.reason" name="pr" aria-label="Reason for the change" /></label>
              </div>
              <button type="button" class="btn btn--secondary" (click)="propose()">Propose change</button>
              <table class="table" data-testid="settings">
                <tbody>
                  @for (s of settings(); track s.settingId) {
                    <tr>
                      <td>{{ s.settingKey }}</td><td class="numeric">{{ json(s.value) }}</td><td class="subtle">{{ s.reason }}</td>
                      <td><span class="badge" [class.badge--ok]="s.status === 'Active'" [class.badge--warn]="s.status === 'Proposed'">{{ s.status }}</span></td>
                      <td class="row row--end">
                        @if (s.status === 'Proposed') {
                          <button type="button" class="btn btn--ghost" [disabled]="s.proposedBy === me()" (click)="decide(s, true)">Approve</button>
                          <button type="button" class="btn btn--ghost" [disabled]="s.proposedBy === me()" (click)="decide(s, false)">Reject</button>
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          </section>
        }
      }
    }
  `,
  styles: [`
    :host { display: block; }
    .tabs { display: flex; gap: var(--space-2); margin-bottom: var(--space-3); }
    .tabs button { background: none; border: 0; padding: var(--space-2) var(--space-3); cursor: pointer; }
    .tabs [aria-selected="true"] { font-weight: var(--weight-bold, 600); text-decoration: underline; }
    .narrow { width: 6rem; }
    .list { list-style: none; margin: 0; padding: 0; }
    .list__row { display: flex; gap: var(--space-3); align-items: center; justify-content: space-between; padding: var(--space-2) 0; border-bottom: 1px solid var(--border, #e5e5e5); flex-wrap: wrap; }
    .callout { padding: var(--space-3); border-radius: var(--radius-md, 8px); background: var(--surface-2, #f6f6f6); }
    label { display: grid; gap: var(--space-1); font-size: var(--text-sm); }
    .h-sub { font-size: var(--text-sm); margin: 0; }
  `],
})
export class Setup implements OnInit {
  private readonly api = inject(ReferenceApi);
  private readonly toast = inject(ToastService);
  private readonly auth = inject(AuthService);

  protected readonly live = environment.useRealApi;
  protected readonly tab = signal<Tab>('catalogue');
  protected readonly offering = signal<readonly OfferingRowDto[]>([]);
  protected readonly rooms = signal<readonly RoomDto[]>([]);
  protected readonly closures = signal<readonly ClosureDto[]>([]);
  protected readonly settings = signal<readonly SettingDto[]>([]);
  protected readonly closing = signal<RoomDto | null>(null);
  protected readonly me = computed(() => this.auth.user()?.principalId ?? null);

  protected readonly keys = ['policy.deposit', 'policy.cancellation', 'messaging.quiet_hours', 'retention.messaging', 'property.operating_mode'];
  protected svc = { name: '', code: '', duration: 60, price: 100 };
  protected prices: Record<string, number> = {};
  protected room = { code: '', name: '', type: 'TreatmentRoom' };
  protected closure = { starts: '', ends: '', reason: 'Maintenance' };
  protected proposal = { key: 'policy.deposit', value: this.template('policy.deposit'), reason: '' };

  ngOnInit(): void { if (this.live) void this.load(); }

  async load(): Promise<void> {
    try {
      const [offering, rooms, closures, settings] = await Promise.all([this.api.offering(), this.api.rooms(), this.api.closures(), this.api.settings()]);
      this.offering.set(offering); this.rooms.set(rooms); this.closures.set(closures); this.settings.set(settings);
    } catch (e) { this.fail(e); }
  }

  protected template(key: string): string {
    return ({
      'policy.deposit': '{"percent":50,"minimumMinor":2000}',
      'policy.cancellation': '{"noticeHours":24,"feePercent":50}',
      'messaging.quiet_hours': '{"start":"21:00","end":"08:00"}',
      'retention.messaging': '{"days":180}',
      'property.operating_mode': '{"mode":"MarqueeIntegrated"}',
    } as Record<string, string>)[key] ?? '{}';
  }

  protected json(v: unknown): string { return JSON.stringify(v); }
  protected fmt = money;
  protected local(utc: string): string { return new Date(utc).toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }); }
  protected roomName(id: string): string { return this.rooms().find((r) => r.roomId === id)?.name ?? id.slice(-6); }

  protected async createService(): Promise<void> {
    await this.run(async () => {
      await this.api.createService({ name: this.svc.name, code: this.svc.code, durationMinutes: Number(this.svc.duration), basePriceMinor: Math.round(Number(this.svc.price) * 100) });
      this.toast.success('Service drafted', 'Activate it to put it on the menu.');
      this.svc = { name: '', code: '', duration: 60, price: 100 };
    });
  }

  protected async transition(o: OfferingRowDto, to: string): Promise<void> {
    await this.run(async () => { await this.api.transitionService(o.service.serviceId, o.service.rowVersion, to); this.toast.success(`${o.service.name}: ${to}`); });
  }

  protected async setPrice(o: OfferingRowDto): Promise<void> {
    const dollars = this.prices[o.service.serviceId];
    if (dollars === undefined) return;
    await this.run(async () => {
      await this.api.offer(o.service.serviceId, { priceMinor: Math.round(Number(dollars) * 100), currencyCode: o.service.currencyCode, status: 'Active' }, o.offering?.rowVersion);
      this.toast.success('Price set', `${o.service.name}: ${money(Math.round(Number(dollars) * 100))} here`);
    });
  }

  protected async createRoom(): Promise<void> {
    await this.run(async () => {
      await this.api.createRoom({ code: this.room.code, name: this.room.name, resourceType: this.room.type });
      this.toast.success('Room added', this.room.name);
      this.room = { code: '', name: '', type: 'TreatmentRoom' };
    });
  }

  protected async toggleRoom(r: RoomDto): Promise<void> {
    await this.run(async () => { await this.api.updateRoom(r.roomId, r.rowVersion, { status: r.status === 'Active' ? 'OutOfService' : 'Active' }); });
  }

  protected async closeRoom(r: RoomDto): Promise<void> {
    if (!this.closure.starts || !this.closure.ends) { this.toast.warn('Choose when'); return; }
    await this.run(async () => {
      await this.api.closeRoom({ resourceId: r.roomId, startsUtc: new Date(this.closure.starts).toISOString(), endsUtc: new Date(this.closure.ends).toISOString(), reasonCode: this.closure.reason || 'Maintenance' });
      this.toast.success('Room closed', 'Bookings in that window are refused.');
      this.closing.set(null);
    });
  }

  protected async cancelClosure(c: ClosureDto): Promise<void> {
    await this.run(async () => { await this.api.cancelClosure(c.maintenanceWindowId, c.rowVersion); });
  }

  protected async propose(): Promise<void> {
    let value: unknown;
    try { value = JSON.parse(this.proposal.value); } catch { this.toast.warn('That is not JSON', 'Write the value as a JSON object.'); return; }
    await this.run(async () => {
      // The operating mode is this property's own; every other policy here is the tenant's.
      await this.api.proposeSetting({ settingKey: this.proposal.key, value, reason: this.proposal.reason, propertyOnly: this.proposal.key === 'property.operating_mode' });
      this.toast.success('Change proposed', 'Someone else approves it before it applies.');
      this.proposal.reason = '';
    });
  }

  protected async decide(s: SettingDto, approve: boolean): Promise<void> {
    await this.run(async () => {
      const r = await this.api.decideSetting(s.settingId, s.rowVersion, approve, approve ? undefined : 'Rejected in setup');
      this.toast.success(approve ? `Approved: ${r.status}` : 'Rejected', s.settingKey);
    });
  }

  private async run(work: () => Promise<void>): Promise<void> {
    try { await work(); await this.load(); } catch (e) { this.fail(e); }
  }

  private fail(e: unknown): void {
    const p = e as ApiProblem;
    if (p?.code === 'AUTHORIZATION_DENIED') this.toast.warn('Not permitted', p.detail ?? p.title, p.code);
    else this.toast.error('That did not work', p?.detail ?? p?.title ?? String(e), p?.correlationId);
  }
}
