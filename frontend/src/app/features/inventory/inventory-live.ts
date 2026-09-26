import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { ReferenceApi, newKey } from '../../core/api/reference-api';
import { ToastService } from '../../core/services/toast.service';
import { STOCK_STATES } from '../../core/models/reference';
import type { BalanceDto, CountDto, ItemDto, LaundryBatchDto, LocationDto } from '../../core/models/reference';
import type { ApiProblem } from '../../core/models/api-problem';

type Action = 'Receipt' | 'Issue' | 'Adjustment' | 'Transfer' | 'StateChange';

/**
 * Stock against the API. Balances are the ledger's projection; every button
 * here posts ledger entries. A movement keeps its Idempotency-Key until it
 * has an answer, so a double click is one movement. Counts are approved by
 * someone other than the counter; linen goes out Soiled and comes back Clean.
 */
@Component({
  selector: 'app-inventory-live',
  standalone: true,
  imports: [PageHeader, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header eyebrow="Operations" title="Inventory"
      subtitle="Every movement is a ledger entry; stock never goes below zero by an ordinary movement.">
      <label class="inline"><input type="checkbox" [ngModel]="lowOnly()" (ngModelChange)="lowOnly.set($event); load()" name="low" /> Low stock only</label>
    </app-page-header>

    <section class="panel">
      <div class="panel__head"><span class="panel__title">Balances</span><span class="panel__hint">{{ balances().length }} rows</span></div>
      <div class="panel__body panel__body--flush">
        <table class="table" data-testid="balances">
          <thead><tr><th scope="col">Item</th><th scope="col">Location</th><th scope="col">State</th><th scope="col" class="numeric">On hand</th><th scope="col"></th></tr></thead>
          <tbody>
            @for (b of balances(); track b.variantId + b.locationId + b.stockState) {
              <tr>
                <td>{{ b.itemName }} <span class="subtle">{{ b.variantCode }}</span></td>
                <td>{{ b.locationName }}</td><td>{{ b.stockState }}</td>
                <td class="numeric">{{ qty(b.onHand) }}</td>
                <td>@if (isLow(b)) { <span class="badge badge--warn">Reorder</span> }</td>
              </tr>
            } @empty { <tr><td colspan="5" class="subtle">No stock recorded.</td></tr> }
          </tbody>
        </table>
      </div>
    </section>

    <div class="grid grid--halves">
      <section class="panel">
        <div class="panel__head"><span class="panel__title">Move stock</span></div>
        <div class="panel__body stack">
          <div class="form-grid">
            <label>Action
              <select id="mv-action" [(ngModel)]="action" name="act">
                <option value="Receipt">Receive</option><option value="Issue">Issue</option><option value="Adjustment">Adjust (±)</option>
                <option value="Transfer">Transfer</option><option value="StateChange">Change state</option>
              </select>
            </label>
            <label>Item
              <select id="mv-item" [(ngModel)]="variantId" name="var">
                @for (v of variants(); track v.variantId) { <option [value]="v.variantId">{{ v.itemName }} · {{ v.variantCode }}</option> }
              </select>
            </label>
            <label>{{ action === 'Transfer' ? 'From' : 'Location' }}
              <select id="mv-loc" [(ngModel)]="locationId" name="loc">
                @for (l of locations(); track l.locationId) { <option [value]="l.locationId">{{ l.locationName }}</option> }
              </select>
            </label>
            @if (action === 'Transfer') {
              <label>To
                <select [(ngModel)]="toLocationId" name="to">
                  @for (l of locations(); track l.locationId) { <option [value]="l.locationId">{{ l.locationName }}</option> }
                </select>
              </label>
            }
            <label>{{ action === 'StateChange' ? 'From state' : 'State' }}
              <select id="mv-state" [(ngModel)]="state" name="st">@for (s of states; track s) { <option [value]="s">{{ s }}</option> }</select>
            </label>
            @if (action === 'StateChange') {
              <label>To state <select [(ngModel)]="toState" name="ts">@for (s of states; track s) { <option [value]="s">{{ s }}</option> }</select></label>
            }
            <label>Quantity <input id="mv-qty" type="number" step="1" [(ngModel)]="quantity" name="qty" aria-label="Quantity" /></label>
            @if (action === 'Adjustment') { <label>Reason <input [(ngModel)]="reason" name="rs" placeholder="Damage" /></label> }
          </div>
          <button type="button" class="btn btn--primary" data-testid="post-movement" [disabled]="busy()" (click)="post()">Post</button>
        </div>
      </section>

      <section class="panel">
        <div class="panel__head"><span class="panel__title">Counts</span></div>
        <div class="panel__body stack">
          <button type="button" class="btn btn--secondary" [disabled]="busy()" (click)="openCount()">Count the selected item here</button>
          <ul class="list" data-testid="counts">
            @for (c of counts(); track c.stockCountId) {
              <li class="list__row">
                <span>{{ variantName(c.variantId) }} · {{ locationName(c.locationId) }}</span>
                <span class="subtle numeric">expected {{ qty(c.expectedQuantity) }}{{ c.variance !== null ? ' · variance ' + qty(c.variance) : '' }}</span>
                <span class="badge">{{ c.status }}</span>
                @if (c.status === 'Open') {
                  <input type="number" min="0" [(ngModel)]="countInputs[c.stockCountId]" [name]="'cq' + c.stockCountId" aria-label="Counted quantity" class="narrow" />
                  <button type="button" class="btn btn--ghost" (click)="record(c)">Record</button>
                } @else if (c.status === 'Counted' || c.status === 'Recounted') {
                  <button type="button" class="btn btn--ghost" (click)="approve(c)">Approve</button>
                }
              </li>
            } @empty { <li class="subtle">No counts open.</li> }
          </ul>
        </div>
      </section>
    </div>

    <section class="panel">
      <div class="panel__head"><span class="panel__title">Laundry</span></div>
      <div class="panel__body stack">
        <p class="subtle">Sends the Soiled quantity of the selected item at the selected location; it comes back Clean.</p>
        <button type="button" class="btn btn--secondary" [disabled]="busy()" (click)="dispatch()">Send selected item to laundry</button>
        <ul class="list">
          @for (b of laundry(); track b.laundryBatchId) {
            <li class="list__row"><span>{{ locationName(b.dispatchLocationId) }} · {{ b.dispatchedUtc?.slice(0, 16)?.replace('T', ' ') }}</span>
              <span class="badge">{{ b.status }}</span>
              @if (b.status === 'Dispatched' || b.status === 'PartiallyReceived') { <button type="button" class="btn btn--ghost" (click)="receive(b)">Receive all</button> }
            </li>
          }
        </ul>
      </div>
    </section>
  `,
  styles: [`
    :host { display: block; }
    .grid--halves { display: grid; gap: var(--space-4); grid-template-columns: 1fr 1fr; margin: var(--space-4) 0; }
    @media (max-width: 900px) { .grid--halves { grid-template-columns: 1fr; } }
    .list { list-style: none; margin: 0; padding: 0; }
    .list__row { display: flex; gap: var(--space-3); align-items: center; justify-content: space-between; padding: var(--space-2) 0; border-bottom: 1px solid var(--border, #e5e5e5); flex-wrap: wrap; }
    .inline { display: inline-flex; gap: var(--space-2); align-items: center; }
    label { display: grid; gap: var(--space-1); font-size: var(--text-sm); }
    .narrow { width: 5rem; }
  `],
})
export class InventoryLive implements OnInit {
  private readonly api = inject(ReferenceApi);
  private readonly toast = inject(ToastService);

  protected readonly states = STOCK_STATES;
  protected readonly items = signal<readonly ItemDto[]>([]);
  protected readonly locations = signal<readonly LocationDto[]>([]);
  protected readonly balances = signal<readonly BalanceDto[]>([]);
  protected readonly counts = signal<readonly CountDto[]>([]);
  protected readonly laundry = signal<readonly LaundryBatchDto[]>([]);
  protected readonly lowOnly = signal(false);
  protected readonly busy = signal(false);
  protected readonly variants = computed(() => this.api.variantsOf(this.items()));

  protected action: Action = 'Receipt';
  protected variantId = '';
  protected locationId = '';
  protected toLocationId = '';
  protected state = 'Saleable';
  protected toState = 'Soiled';
  protected quantity: number | null = null;
  protected reason = '';
  protected countInputs: Record<string, number | null> = {};
  /** The movement in flight keeps its key until it has an answer. */
  private attempt: { key: string; body: string } | null = null;

  ngOnInit(): void { void this.load(); }

  async load(): Promise<void> {
    try {
      const [items, locations, balances, counts, laundry] = await Promise.all([
        this.api.items(), this.api.locations(), this.api.balances({ low: this.lowOnly() }), this.api.counts(true), this.api.laundry(),
      ]);
      this.items.set(items); this.locations.set(locations); this.balances.set(balances); this.counts.set(counts); this.laundry.set(laundry);
      this.variantId ||= this.variants()[0]?.variantId ?? '';
      this.locationId ||= locations[0]?.locationId ?? '';
      this.toLocationId ||= locations[1]?.locationId ?? locations[0]?.locationId ?? '';
    } catch (e) { this.fail(e); }
  }

  protected qty(n: number): string { return Number(n).toLocaleString(undefined, { maximumFractionDigits: 2 }); }
  protected variantName(id: string): string { const v = this.variants().find((x) => x.variantId === id); return v ? `${v.itemName} ${v.variantCode}` : id.slice(-6); }
  protected locationName(id: string): string { return this.locations().find((l) => l.locationId === id)?.locationName ?? id.slice(-6); }
  protected isLow(b: BalanceDto): boolean {
    if (b.stockState !== 'Saleable' && b.stockState !== 'Clean') return false;
    const usable = this.balances().filter((x) => x.variantId === b.variantId && (x.stockState === 'Saleable' || x.stockState === 'Clean'))
      .reduce((s, x) => s + Number(x.onHand), 0);
    return usable <= Number(b.reorderPoint);
  }

  private keyFor(body: unknown): string {
    const json = JSON.stringify(body);
    if (this.attempt?.body !== json) this.attempt = { key: newKey(), body: json };
    return this.attempt.key;
  }

  protected async post(): Promise<void> {
    const q = Number(this.quantity);
    if (!this.variantId || !this.locationId || !q) { this.toast.warn('Choose an item, a place and a quantity'); return; }
    await this.run(async () => {
      if (this.action === 'Transfer') {
        const body = { variantId: this.variantId, fromLocationId: this.locationId, toLocationId: this.toLocationId, quantity: q, stockState: this.state };
        await this.api.transfer(this.keyFor(body), body);
      } else if (this.action === 'StateChange') {
        const body = { variantId: this.variantId, locationId: this.locationId, fromState: this.state, toState: this.toState, quantity: q };
        await this.api.changeState(this.keyFor(body), body);
      } else {
        const body = { variantId: this.variantId, locationId: this.locationId, movementType: this.action, quantity: q, stockState: this.state,
          reasonCode: this.action === 'Adjustment' ? this.reason || 'Adjustment' : undefined };
        await this.api.move(this.keyFor(body), body);
      }
      this.attempt = null;
      this.toast.success('Posted', `${this.action} of ${q}`);
      this.quantity = null;
      await this.load();
    });
  }

  protected async openCount(): Promise<void> {
    await this.run(async () => { await this.api.openCount(this.variantId, this.locationId, this.state); await this.load(); });
  }

  protected async record(c: CountDto): Promise<void> {
    const q = this.countInputs[c.stockCountId];
    if (q === null || q === undefined) return;
    await this.run(async () => { await this.api.recordCount(c.stockCountId, c.rowVersion, Number(q), false); await this.load(); });
  }

  protected async approve(c: CountDto): Promise<void> {
    await this.run(async () => {
      await this.api.approveCount(c.stockCountId, c.rowVersion);
      this.toast.success('Count approved', 'The variance is posted to the ledger.');
      await this.load();
    });
  }

  protected async dispatch(): Promise<void> {
    const soiled = this.balances().find((b) => b.variantId === this.variantId && b.locationId === this.locationId && b.stockState === 'Soiled');
    if (!soiled || Number(soiled.onHand) <= 0) { this.toast.warn('Nothing soiled here', 'Change Clean to Soiled first.'); return; }
    await this.run(async () => {
      const body = { dispatchLocationId: this.locationId, lines: [{ variantId: this.variantId, quantity: Number(soiled.onHand) }] };
      await this.api.dispatchLaundry(this.keyFor(body), body);
      this.attempt = null;
      this.toast.success('Sent to laundry', `${this.qty(soiled.onHand)} ${this.variantName(this.variantId)}`);
      await this.load();
    });
  }

  protected async receive(b: LaundryBatchDto): Promise<void> {
    await this.run(async () => {
      const away = await this.api.balances({ locationId: b.dispatchLocationId });
      const lines = away.filter((x) => x.stockState === 'InLaundry' && Number(x.onHand) > 0).map((x) => ({ variantId: x.variantId, quantity: Number(x.onHand) }));
      await this.api.receiveLaundry(b.laundryBatchId, b.rowVersion, lines);
      this.toast.success('Laundry received');
      await this.load();
    });
  }

  private async run(work: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    try { await work(); } catch (e) { this.fail(e); } finally { this.busy.set(false); }
  }

  private fail(e: unknown): void {
    const p = e as ApiProblem;
    if (p?.code === 'AUTHORIZATION_DENIED') this.toast.warn('Not permitted', p.detail ?? p.title, p.code);
    else this.toast.error('That did not work', p?.detail ?? p?.title ?? String(e), p?.correlationId);
  }
}
