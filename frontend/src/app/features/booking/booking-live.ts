import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatePanel } from '../../shared/components/state-panel/state-panel';
import { IdempotencyKeys, SchedulingApi } from '../../core/api/scheduling-api';
import { OperationsApi } from '../../core/api/operations-api';
import { GuestsApi } from '../../core/api/guests-api';
import { WorkspaceStore } from '../../core/services/workspace-store';
import { ToastService } from '../../core/services/toast.service';
import type { ApiProblem } from '../../core/models/api-problem';
import type { AppointmentDto, AvailabilitySlotDto } from '../../core/models/api';
import type { GuestSearchHitDto } from '../../core/models/guests';

/**
 * Desk booking against the API: find (or create) the guest, pick a service
 * and a day, choose one of the property's open slots, then book it, hold it
 * for ten minutes, or put the guest on the waitlist for that day. The server
 * runs every conflict rule on the create; a soft conflict comes back as a
 * reason prompt, which only a manager may answer.
 */
@Component({
  selector: 'app-booking-live',
  standalone: true,
  imports: [PageHeader, StatePanel, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header eyebrow="Guests" title="Availability and booking"
      subtitle="Times are the property's own, whatever time zone this screen is in." />

    <div class="grid grid--split">
      <div class="stack">
        <div class="panel">
          <div class="panel__head"><span class="panel__title">1 · Guest</span></div>
          <div class="panel__body stack">
            @if (guest(); as g) {
              <div class="row"><strong>{{ g.displayAlias }}</strong><span class="subtle">{{ g.publicQueueId }}</span>
                <button type="button" class="btn btn--ghost" (click)="guest.set(null)">Change</button></div>
            } @else {
              <div class="row">
                <input class="input" type="search" placeholder="Name, email, phone or queue id" aria-label="Find guest"
                  [ngModel]="q()" (ngModelChange)="q.set($event)" (keydown.enter)="find()" />
                <button type="button" class="btn btn--secondary" (click)="find()" [disabled]="q().trim().length < 2">Find</button>
              </div>
              @for (h of hits(); track h.guestId) {
                <button type="button" class="btn btn--ghost" (click)="guest.set(h)">{{ h.displayAlias }} · {{ h.publicQueueId }}</button>
              }
              <div class="row">
                <input class="input" placeholder="New guest: first name" aria-label="New guest first name" [(ngModel)]="walkIn.first" />
                <input class="input" placeholder="Last name" aria-label="New guest last name" [(ngModel)]="walkIn.last" />
                <input class="input" placeholder="Email (optional)" aria-label="New guest email" [(ngModel)]="walkIn.email" />
                <button type="button" class="btn btn--secondary" (click)="createGuest()" [disabled]="!walkIn.first">Create</button>
              </div>
            }
          </div>
        </div>

        <div class="panel">
          <div class="panel__head"><span class="panel__title">2 · Service and day</span></div>
          <div class="panel__body">
            <div class="form-grid">
              <div><label for="b-svc">Service</label>
                <select id="b-svc" [ngModel]="serviceId()" (ngModelChange)="serviceId.set($event); loadSlots()">
                  <option value="">Choose…</option>
                  @for (s of store.services(); track s.serviceId) { <option [value]="s.serviceId">{{ s.name }}</option> }
                </select></div>
              <div><label for="b-day">Day</label>
                <input id="b-day" type="date" class="input" [ngModel]="day()" (ngModelChange)="day.set($event); loadSlots()" /></div>
            </div>
          </div>
        </div>

        <div class="panel">
          <div class="panel__head"><span class="panel__title">3 · Time</span>
            @if (zone()) { <span class="panel__hint">{{ zone() }}</span> }</div>
          <div class="panel__body">
            @if (!serviceId()) {
              <app-state-panel state="first-use" title="Choose a service" body="Open times depend on how long the treatment is." />
            } @else if (slots().length === 0) {
              <app-state-panel state="empty" title="No times that day" body="The property is closed, or the day is full. Try another day or add the guest to the waitlist." />
            } @else {
              <div class="slots">
                @for (s of slots(); track s.startUtc) {
                  <button type="button" class="tslot" [class.is-picked]="picked()?.startUtc === s.startUtc" (click)="picked.set(s)"
                    [attr.aria-label]="s.startLocal.slice(11) + (s.open ? '' : ' (some rooms busy)')">
                    <span class="numeric">{{ s.startLocal.slice(11) }}</span>
                    <span class="tslot__who">{{ s.open ? 'Open' : 'Busy rooms: ' + s.busyRooms.length }}</span>
                  </button>
                }
              </div>
            }
          </div>
        </div>
      </div>

      <div class="stack">
        <div class="panel">
          <div class="panel__head"><span class="panel__title">Booking</span></div>
          <div class="panel__body stack">
            <dl class="dl">
              <dt>Guest</dt><dd>{{ guest()?.displayAlias ?? '—' }}</dd>
              <dt>Service</dt><dd>{{ serviceName() }}</dd>
              <dt>When</dt><dd class="numeric">{{ picked()?.startLocal ?? 'Pick a time' }}</dd>
            </dl>
            <div class="row">
              <button type="button" class="btn btn--primary" [disabled]="!ready() || busy()" (click)="book(false)">Book</button>
              <button type="button" class="btn btn--secondary" [disabled]="!ready() || busy()" (click)="book(true)">Hold 10 min</button>
              <button type="button" class="btn btn--ghost" [disabled]="!guest() || !serviceId() || busy()" (click)="waitlist()">Waitlist this day</button>
            </div>
          </div>
        </div>

        @if (booked(); as a) {
          <app-state-panel [state]="a.status === 'Held' ? 'queued' : 'first-use'"
            [title]="a.status === 'Held' ? 'Held until ' + time(a.holdExpiresUtc) : 'Booked · ' + (a.confirmationNumber ?? '')"
            [body]="a.serviceName + ' at ' + a.startLocal + '. The guest can complete their intake form from their sign-in link.'" />
        } @else if (problem(); as p) {
          <app-state-panel [state]="p.status === 409 ? 'conflict' : 'error'" [title]="p.title" [body]="p.detail ?? ''" />
        }
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }
    .slots { display: grid; grid-template-columns: repeat(auto-fill, minmax(96px, 1fr)); gap: var(--space-3); }
    .tslot { display: flex; flex-direction: column; align-items: center; gap: 2px; min-height: 56px; padding: var(--space-2);
      border: 1px solid var(--border-subtle); border-radius: var(--radius-md); background: var(--bg-canvas); color: var(--fg-default);
      font-size: var(--text-sm); font-weight: var(--weight-bold); cursor: pointer; }
    .tslot.is-picked { background: var(--grad-accent); color: #fff; border-color: transparent; }
    .tslot__who { font-size: var(--text-2xs); font-weight: var(--weight-regular); opacity: 0.75; }
    label { display: block; font-size: var(--text-sm); font-weight: var(--weight-bold); margin-bottom: var(--space-2); }
  `],
})
export class BookingLive implements OnInit {
  private readonly api = inject(SchedulingApi);
  private readonly ops = inject(OperationsApi);
  private readonly guests = inject(GuestsApi);
  private readonly keys = inject(IdempotencyKeys);
  private readonly toast = inject(ToastService);
  protected readonly store = inject(WorkspaceStore);

  protected readonly q = signal('');
  protected readonly hits = signal<readonly GuestSearchHitDto[]>([]);
  protected readonly guest = signal<GuestSearchHitDto | null>(null);
  protected readonly serviceId = signal('');
  protected readonly day = signal(this.store.boardDate());
  protected readonly slots = signal<readonly AvailabilitySlotDto[]>([]);
  protected readonly zone = signal<string | null>(null);
  protected readonly picked = signal<AvailabilitySlotDto | null>(null);
  protected readonly booked = signal<AppointmentDto | null>(null);
  protected readonly problem = signal<ApiProblem | null>(null);
  protected readonly busy = signal(false);
  protected walkIn = { first: '', last: '', email: '' };

  protected readonly ready = computed(() => !!this.guest() && !!this.serviceId() && !!this.picked());
  protected readonly serviceName = computed(() => this.store.services().find((s) => s.serviceId === this.serviceId())?.name ?? '—');

  ngOnInit(): void { void this.store.loadServices(); }

  protected time(utc: string | null | undefined): string {
    return utc ? new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' }).format(new Date(utc)) : '';
  }

  protected async find(): Promise<void> {
    try { this.hits.set(await this.guests.search(this.q().trim())); } catch (e) { this.fail(e); }
  }

  protected async createGuest(): Promise<void> {
    try {
      const r = await this.guests.create({ legalFirstName: this.walkIn.first, legalLastName: this.walkIn.last || undefined,
        email: this.walkIn.email || undefined, contactsVerifiedInPerson: true });
      const g = r.guest;
      this.guest.set({ guestId: g.guestId, displayAlias: g.displayAlias, preferredName: g.preferredName, legalLastName: g.legalLastName,
        publicQueueId: g.publicQueueId, contacts: g.contacts });
      this.walkIn = { first: '', last: '', email: '' };
      this.toast.success('Guest created', `${g.displayAlias} · ${g.publicQueueId}`);
    } catch (e) { this.fail(e); }
  }

  protected async loadSlots(): Promise<void> {
    this.picked.set(null);
    if (!this.serviceId() || !this.day()) { this.slots.set([]); return; }
    try {
      const a = await this.api.availability(this.day(), this.serviceId());
      this.slots.set(a.slots);
      this.zone.set(a.timeZone);
    } catch (e) { this.fail(e); }
  }

  protected async book(hold: boolean): Promise<void> {
    const g = this.guest(), s = this.picked();
    if (!g || !s) return;
    const identity = `book|${g.guestId}|${this.serviceId()}|${s.startUtc}|${hold}`;
    this.busy.set(true);
    this.problem.set(null);
    try {
      const a = await this.api.createAppointment({
        guestId: g.guestId, guestAlias: g.displayAlias ?? 'Guest', serviceId: this.serviceId(), startUtc: s.startUtc,
        source: 'Desk', holdMinutes: hold ? 10 : null,
      }, this.keys.keyFor(identity));
      this.keys.forget(identity);
      this.booked.set(a);
      this.toast.success(hold ? 'Slot held for 10 minutes' : 'Booked', `${a.serviceName} · ${a.startLocal}`);
      void this.store.loadBoard();
      void this.loadSlots();
    } catch (e) {
      this.keys.forget(identity);
      this.booked.set(null);
      this.problem.set(e as ApiProblem);
    } finally {
      this.busy.set(false);
    }
  }

  protected async waitlist(): Promise<void> {
    const g = this.guest();
    if (!g) return;
    const from = new Date(`${this.day()}T00:00:00`);
    try {
      await this.ops.addToWaitlist({ guestId: g.guestId, serviceId: this.serviceId() || null,
        earliestUtc: from.toISOString(), latestUtc: new Date(from.getTime() + 86400_000).toISOString() });
      this.toast.success('Added to the waitlist', `${g.displayAlias} will be offered the first slot that opens that day.`);
    } catch (e) { this.fail(e); }
  }

  private fail(e: unknown): void {
    const p = e as ApiProblem;
    this.toast.error('That did not work', p.detail ?? p.title, p.correlationId);
  }
}
