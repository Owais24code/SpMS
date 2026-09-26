import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { environment } from '../../../environments/environment';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatePanel } from '../../shared/components/state-panel/state-panel';
import { CommerceApi, newIdempotencyKey } from '../../core/api/commerce-api';
import { OperationsApi } from '../../core/api/operations-api';
import { ToastService } from '../../core/services/toast.service';
import { browserToday } from '../../core/services/board-time';
import { isAmbiguous, money } from '../../core/models/commerce';
import type { OrderDto, OrderSummaryDto, PaymentResponse, Tender, TransactionDto } from '../../core/models/commerce';
import type { ArrivalDto } from '../../core/models/operations';
import type { ApiProblem } from '../../core/models/api-problem';

/** The development provider's test tokens. A real provider's hosted card field produces the token instead. */
const TEST_CARDS = [
  { token: 'tok_approve', label: 'Test card — approves' },
  { token: 'tok_decline', label: 'Test card — declines' },
  { token: 'tok_timeout', label: 'Test card — provider does not answer' },
] as const;

/**
 * The till (COM-001..006). Deposits on today's bookings, the cart built from a
 * guest's treatments, tips and comps, payment by card or cash, and refunds.
 *
 * Every money click keeps its Idempotency-Key until it has an answer, so a
 * double click or a retry after a dropped connection is the same payment. An
 * unconfirmed card payment is shown as "waiting for the provider" with one
 * action — ask about the original — and the Pay button stays closed until it
 * is known, because paying again is how a guest gets charged twice.
 */
@Component({
  selector: 'app-checkout',
  standalone: true,
  imports: [PageHeader, StatePanel, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header eyebrow="Today" title="Checkout"
      subtitle="Deposits, the guest's bill, tips and payment. Card details never reach SpMS — only the provider's token.">
      <button type="button" class="btn btn--secondary" (click)="load()">Refresh</button>
    </app-page-header>

    @if (!live) {
      <app-state-panel state="first-use" title="Checkout runs against the API"
        body="Start the workspace with the API to take deposits and payments." />
    } @else {
      <div class="grid grid--split">
        <section class="panel" aria-labelledby="arrivals-h">
          <div class="panel__head">
            <span class="panel__title" id="arrivals-h">Today's guests</span>
            <span class="panel__hint numeric">{{ date() }}</span>
          </div>
          <div class="panel__body panel__body--flush">
            @if (arrivals().length === 0) {
              <p class="subtle pad">No bookings today.</p>
            }
            <ul class="list" data-testid="checkout-arrivals">
              @for (a of arrivals(); track a.appointmentId) {
                <li class="list__row">
                  <div>
                    <strong>{{ a.guestAlias }}</strong> · {{ a.serviceName }}
                    <div class="subtle numeric">{{ a.startLocal.slice(11) }} · {{ a.status }}</div>
                  </div>
                  <div class="row row--end">
                    <span class="badge" [class.badge--ok]="a.deposit === 'Settled'" [class.badge--warn]="a.deposit === 'Pending'">
                      {{ depositLabel(a) }}
                    </span>
                    @if (a.deposit === 'Pending' && (a.status === 'Confirmed' || a.status === 'Held')) {
                      <button type="button" class="btn btn--ghost" [disabled]="busy()" (click)="takeDeposit(a)">Take deposit</button>
                    }
                    <button type="button" class="btn btn--ghost" [disabled]="busy()" (click)="openFor(a)">Open bill</button>
                  </div>
                </li>
              }
            </ul>
          </div>
          <div class="panel__head">
            <span class="panel__title">Bills today</span>
          </div>
          <div class="panel__body panel__body--flush">
            <ul class="list">
              @for (o of orders(); track o.orderId) {
                <li class="list__row">
                  <button type="button" class="linklike" (click)="open(o.orderId)">{{ o.orderNumber ?? 'Cart' }}</button>
                  <span class="numeric">{{ fmt(o.totalMinor, o.currencyCode) }}</span>
                  <span class="badge" [class.badge--ok]="o.status === 'Paid'">{{ o.status }}</span>
                </li>
              }
            </ul>
          </div>
        </section>

        <section class="panel" aria-labelledby="bill-h">
          <div class="panel__head">
            <span class="panel__title" id="bill-h">{{ order()?.orderNumber ?? (order() ? 'Cart' : 'No bill open') }}</span>
            @if (order(); as o) {
              <span class="badge" data-testid="order-status" [class.badge--ok]="o.status === 'Paid'" [class.badge--warn]="o.status === 'PartiallyPaid'">{{ o.status }}</span>
            }
          </div>
          @if (order(); as o) {
            <div class="panel__body stack">
              @if (o.status === 'Delegated') {
                <p class="subtle">{{ o.ownerSystem }} owns payment at this property. The bill is settled there.</p>
              }
              <table class="table">
                <thead><tr><th scope="col">Item</th><th scope="col" class="numeric">Amount</th><th scope="col" class="numeric">Tax</th><th scope="col"></th></tr></thead>
                <tbody>
                  @for (l of o.lines; track l.orderLineId) {
                    <tr>
                      <td>{{ l.description }} <span class="subtle">{{ l.lineKind === 'Service' ? '' : l.lineKind }}</span></td>
                      <td class="numeric">{{ fmt(l.netMinor, o.currencyCode) }}</td>
                      <td class="numeric">{{ fmt(l.taxMinor, o.currencyCode) }}</td>
                      <td>@if (o.status === 'Draft') { <button type="button" class="btn btn--ghost" (click)="removeLine(l.orderLineId)" [attr.aria-label]="'Remove ' + l.description">×</button> }</td>
                    </tr>
                  }
                </tbody>
              </table>
              <dl class="totals numeric">
                <dt>Subtotal</dt><dd>{{ fmt(o.subtotalMinor, o.currencyCode) }}</dd>
                <dt>Tax</dt><dd>{{ fmt(o.taxTotalMinor, o.currencyCode) }}</dd>
                <dt>Tips</dt><dd>{{ fmt(o.tipTotalMinor, o.currencyCode) }}</dd>
                <dt><strong>Total</strong></dt><dd data-testid="order-total"><strong>{{ fmt(o.totalMinor, o.currencyCode) }}</strong></dd>
                <dt>Paid</dt><dd>{{ fmt(o.paidMinor, o.currencyCode) }}</dd>
                @if (o.refundedMinor > 0) { <dt>Refunded</dt><dd>{{ fmt(o.refundedMinor, o.currencyCode) }}</dd> }
                <dt><strong>Balance</strong></dt><dd data-testid="order-balance"><strong>{{ fmt(o.balanceMinor, o.currencyCode) }}</strong></dd>
              </dl>

              @if (o.status === 'Draft') {
                <div class="row">
                  <label class="inline">Tip <input type="number" min="0" step="1" [(ngModel)]="tipDollars" name="tip" aria-label="Tip in dollars" /></label>
                  <button type="button" class="btn btn--ghost" [disabled]="busy() || !tipDollars" (click)="addTip()">Add tip</button>
                  <label class="inline">Comp <input type="number" min="0" step="1" [(ngModel)]="compDollars" name="comp" aria-label="Comp in dollars" /></label>
                  <button type="button" class="btn btn--ghost" [disabled]="busy() || !compDollars" (click)="addComp()">Comp (manager)</button>
                </div>
                <button type="button" class="btn btn--primary" [disabled]="busy() || o.lines.length === 0" (click)="place()">Place order</button>
              }

              @if (pending(); as p) {
                <div class="callout callout--warn" role="status" data-testid="payment-pending">
                  <strong>Waiting for the provider.</strong> The card was sent but the provider has not said whether it was approved.
                  Do not take the payment again — ask about this one.
                  <div class="row"><button type="button" class="btn btn--secondary" [disabled]="busy()" (click)="query(p)">Ask the provider</button></div>
                </div>
              }

              @if ((o.status === 'Open' || o.status === 'PartiallyPaid') && !pending()) {
                <div class="row">
                  <label class="inline">Tender
                    <select [(ngModel)]="tender" name="tender" aria-label="Tender">
                      <option value="Card">Card</option><option value="Cash">Cash</option>
                    </select>
                  </label>
                  @if (tender === 'Card') {
                    <label class="inline">Card
                      <select [(ngModel)]="card" name="card" aria-label="Card">
                        @for (c of cards; track c.token) { <option [value]="c.token">{{ c.label }}</option> }
                      </select>
                    </label>
                  }
                  <button type="button" class="btn btn--primary" data-testid="pay" [disabled]="busy()" (click)="pay()">
                    Take {{ fmt(o.balanceMinor, o.currencyCode) }}
                  </button>
                </div>
              }

              @if (o.transactions.length > 0) {
                <h3 class="h-sub">Payments</h3>
                <ul class="list">
                  @for (t of o.transactions; track t.paymentTransactionId) {
                    <li class="list__row">
                      <span>{{ t.transactionType }} · {{ t.tenderCode }} {{ t.cardLast4 ? '•••• ' + t.cardLast4 : '' }}</span>
                      <span class="numeric">{{ fmt(t.amountMinor, t.currencyCode) }}</span>
                      <span class="badge" [class.badge--ok]="t.outcome === 'Approved'" [class.badge--danger]="t.outcome === 'Declined' || t.outcome === 'Error'">{{ t.outcome }}</span>
                      @if (t.transactionType === 'Sale' && t.outcome === 'Approved') {
                        <button type="button" class="btn btn--ghost" (click)="startRefund(t)">Refund…</button>
                      }
                    </li>
                  }
                </ul>
              }

              @if (refunding(); as t) {
                <div class="stack callout">
                  <strong>Refund against {{ t.tenderCode }} {{ fmt(t.amountMinor, t.currencyCode) }}</strong>
                  <p class="subtle">A refund is requested here and approved by someone else in finance.</p>
                  <div class="row">
                    <label class="inline">Amount <input type="number" min="1" step="1" [(ngModel)]="refundDollars" name="refund" aria-label="Refund in dollars" /></label>
                    <label class="inline">Reason
                      <select [(ngModel)]="refundReason" name="reason" aria-label="Refund reason">
                        <option value="ServiceIssue">Service issue</option>
                        <option value="Overcharge">Overcharge</option>
                        <option value="GoodwillGesture">Goodwill</option>
                      </select>
                    </label>
                    <button type="button" class="btn btn--primary" [disabled]="busy() || !refundDollars" (click)="requestRefund(t)">Request refund</button>
                    <button type="button" class="btn btn--ghost" (click)="refunding.set(null)">Cancel</button>
                  </div>
                </div>
              }

              @if (o.receiptNumber) {
                <p class="subtle">Receipt <span class="numeric" data-testid="receipt">{{ o.receiptNumber }}</span></p>
              }
            </div>
          } @else {
            <div class="panel__body"><p class="subtle">Open a guest's bill from the list.</p></div>
          }
        </section>
      </div>
    }
  `,
  styles: [`
    :host { display: block; }
    .grid--split { display: grid; gap: var(--space-4); grid-template-columns: minmax(0, 1fr) minmax(0, 1.2fr); }
    @media (max-width: 900px) { .grid--split { grid-template-columns: 1fr; } }
    .list { list-style: none; margin: 0; padding: 0; }
    .list__row { display: flex; gap: var(--space-3); align-items: center; justify-content: space-between; padding: var(--space-3) var(--space-4); border-bottom: 1px solid var(--border, #e5e5e5); flex-wrap: wrap; }
    .pad { padding: var(--space-4); }
    .totals { display: grid; grid-template-columns: 1fr auto; gap: var(--space-1) var(--space-4); margin: 0; }
    .totals dd { margin: 0; text-align: right; }
    .inline { display: inline-flex; gap: var(--space-2); align-items: center; font-size: var(--text-sm); }
    .inline input { width: 6rem; }
    .callout { padding: var(--space-3); border-radius: var(--radius-md, 8px); background: var(--surface-2, #f6f6f6); }
    .callout--warn { background: var(--warn-bg, #fff6e0); }
    .linklike { background: none; border: 0; padding: 0; color: var(--accent, inherit); text-decoration: underline; cursor: pointer; }
    .h-sub { font-size: var(--text-sm); margin: 0; }
  `],
})
export class Checkout implements OnInit {
  private readonly commerce = inject(CommerceApi);
  private readonly ops = inject(OperationsApi);
  private readonly toast = inject(ToastService);

  protected readonly live = environment.useRealApi;
  protected readonly cards = TEST_CARDS;
  protected readonly date = signal(browserToday());
  protected readonly arrivals = signal<readonly ArrivalDto[]>([]);
  protected readonly orders = signal<readonly OrderSummaryDto[]>([]);
  protected readonly order = signal<OrderDto | null>(null);
  protected readonly busy = signal(false);
  /** The intent the provider has not confirmed, for the open bill. */
  protected readonly pending = signal<string | null>(null);
  protected readonly refunding = signal<TransactionDto | null>(null);

  protected tender: Tender = 'Card';
  protected card: string = TEST_CARDS[0].token;
  protected tipDollars: number | null = null;
  protected compDollars: number | null = null;
  protected refundDollars: number | null = null;
  protected refundReason = 'ServiceIssue';

  /** The key for the payment attempt in flight; kept until that attempt has an answer. */
  private attemptKey: string | null = null;

  protected readonly fmt = money;

  protected readonly depositLabel = (a: ArrivalDto) =>
    ({ Settled: 'Deposit paid', Pending: 'Deposit due', NotRequired: 'No deposit', NotTracked: '—' } as const)[a.deposit];

  readonly hasOrder = computed(() => this.order() !== null);

  ngOnInit(): void {
    if (this.live) void this.load();
  }

  async load(): Promise<void> {
    try {
      const [arrivals, orders] = await Promise.all([this.ops.arrivals(this.date()), this.commerce.orders({ date: this.date() })]);
      this.arrivals.set(arrivals.items);
      this.orders.set(orders);
    } catch (e) { this.fail(e); }
  }

  protected async open(orderId: string): Promise<void> {
    await this.run(async () => { this.setOrder(await this.commerce.order(orderId)); });
  }

  protected async openFor(a: ArrivalDto): Promise<void> {
    await this.run(async () => {
      this.setOrder(await this.commerce.createOrder({ appointmentIds: [a.appointmentId] }));
      await this.load();
    });
  }

  protected async takeDeposit(a: ArrivalDto): Promise<void> {
    const key = newIdempotencyKey();
    await this.run(async () => {
      const r = await this.commerce.deposit(a.appointmentId, key, this.tender === 'Cash' ? { tenderCode: 'Cash' } : { tenderCode: 'Card', paymentMethodToken: this.card });
      this.announce(r, `Deposit for ${a.guestAlias}`);
      await this.load();
    });
  }

  protected async addTip(): Promise<void> {
    const o = this.order(); if (!o || !this.tipDollars) return;
    await this.run(async () => {
      this.setOrder(await this.commerce.addLine(o.orderId, { lineKind: 'Tip', description: 'Gratuity', unitPriceMinor: Math.round(this.tipDollars! * 100) }));
      this.tipDollars = null;
    });
  }

  protected async addComp(): Promise<void> {
    const o = this.order(); if (!o || !this.compDollars) return;
    await this.run(async () => {
      this.setOrder(await this.commerce.addLine(o.orderId, { lineKind: 'Discount', description: 'Manager comp', unitPriceMinor: Math.round(this.compDollars! * 100) }));
      this.compDollars = null;
    });
  }

  protected async removeLine(lineId: string): Promise<void> {
    const o = this.order(); if (!o) return;
    await this.run(async () => { this.setOrder(await this.commerce.removeLine(o.orderId, lineId)); });
  }

  protected async place(): Promise<void> {
    const o = this.order(); if (!o) return;
    await this.run(async () => {
      const placed = await this.commerce.place(o.orderId, o.rowVersion);
      this.setOrder(placed);
      this.toast.success(`Order ${placed.orderNumber} placed`, placed.paidMinor > 0 ? `Deposit of ${money(placed.paidMinor)} applied.` : undefined);
      await this.load();
    });
  }

  protected async pay(): Promise<void> {
    const o = this.order(); if (!o) return;
    this.attemptKey ??= newIdempotencyKey();
    const key = this.attemptKey;
    await this.run(async () => {
      const r = await this.commerce.pay(o.orderId, key, this.tender === 'Cash'
        ? { tenderCode: 'Cash', amountMinor: o.balanceMinor }
        : { tenderCode: 'Card', amountMinor: o.balanceMinor, paymentMethodToken: this.card });
      this.attemptKey = null;
      this.announce(r, 'Payment');
      await this.refresh();
    });
  }

  protected async query(intentId: string): Promise<void> {
    await this.run(async () => {
      const r = await this.commerce.resolve(intentId);
      this.announce(r, 'The provider');
      await this.refresh();
    });
  }

  protected startRefund(t: TransactionDto): void {
    this.refunding.set(t);
    this.refundDollars = null;
  }

  protected async requestRefund(t: TransactionDto): Promise<void> {
    if (!this.refundDollars) return;
    await this.run(async () => {
      await this.commerce.requestRefund(t.paymentTransactionId, Math.round(this.refundDollars! * 100), this.refundReason);
      this.toast.success('Refund requested', 'Finance approves it before the money goes back.');
      this.refunding.set(null);
    });
  }

  private announce(r: PaymentResponse, what: string): void {
    if (isAmbiguous(r)) {
      this.pending.set(r.payment_intent_id);
      this.toast.warn('Waiting for the provider', 'Do not take it again. Ask the provider about this payment.', r.code);
      return;
    }
    this.pending.set(null);
    if (r.outcome === 'Approved') this.toast.success(`${what} approved`, money(r.intent.amountMinor, r.intent.currencyCode));
    else this.toast.warn(`${what} ${r.outcome.toLowerCase()}`, r.message ?? 'Try another card or tender.', r.outcome);
  }

  private setOrder(o: OrderDto): void {
    if (this.order()?.orderId !== o.orderId) { this.pending.set(null); this.attemptKey = null; this.refunding.set(null); }
    this.order.set(o);
  }

  private async refresh(): Promise<void> {
    const o = this.order();
    if (o) this.order.set(await this.commerce.order(o.orderId));
    await this.load();
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
