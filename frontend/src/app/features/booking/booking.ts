import { Component, ChangeDetectionStrategy, signal, inject, computed } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatePanel } from '../../shared/components/state-panel/state-panel';
import { ToastService } from '../../core/services/toast.service';
import { WorkspaceStore } from '../../core/services/workspace-store';
import { API_ERROR } from '../../core/models/contract';

@Component({
  selector: 'app-booking',
  standalone: true,
  imports: [PageHeader, StatePanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-page-header
      eyebrow="Guests"
      title="Availability and booking"
      subtitle="Times are shown in the property's timezone and stay correct across daylight-saving changes."
    >
      <button type="button" class="btn btn--secondary" (click)="waitlist()">Waitlist</button>
      <button type="button" class="btn btn--primary" (click)="hold()">Hold slot</button>
    </app-page-header>

    <div class="grid grid--split">
      <div class="stack">
        <div class="panel">
          <div class="panel__head"><span class="panel__title">Search availability</span></div>
          <div class="panel__body">
            <div class="form-grid">
              <div>
                <label for="loc">Location</label>
                <select id="loc"><option>Riverside Spa — Buffalo, NY</option><option>Lakeside Spa — Rochester, NY</option></select>
              </div>
              <div>
                <label for="svc">Service</label>
                <select id="svc"><option>Deep tissue 90</option><option>Aromatherapy 60</option><option>Couples 90</option></select>
              </div>
              <div>
                <label for="from">From</label>
                <input id="from" type="date" value="2026-09-19" />
              </div>
              <div>
                <label for="to">To</label>
                <input id="to" type="date" value="2026-09-26" />
              </div>
              <div>
                <label for="prov">Provider preference</label>
                <select id="prov"><option>Any provider</option><option>Lena Kovač</option><option>Priya Nair</option></select>
              </div>
              <div>
                <label for="acc">Accessibility needs</label>
                <select id="acc"><option>None recorded</option><option>Step-free room</option><option>Sign language support</option></select>
              </div>
            </div>
            <p class="subtle" style="margin-top: var(--space-4)">
              Accessibility preferences are used to find suitable rooms — never to reduce what you are offered.
            </p>
          </div>
        </div>

        <div class="panel">
          <div class="panel__head">
            <span class="panel__title">Available times</span>
            <span class="panel__hint">Deep tissue 90 · 1h 30m · prep 15m</span>
          </div>
          <div class="panel__body">
            <div class="slots">
              @for (s of slots; track s.time) {
                <button type="button" class="tslot" [class.is-picked]="picked() === s.time"
                        [disabled]="!s.open" (click)="picked.set(s.time)">
                  <span class="numeric">{{ s.time }}</span>
                  <span class="tslot__who">{{ s.open ? s.provider : 'Full' }}</span>
                </button>
              }
            </div>
          </div>
        </div>
      </div>

      <div class="stack">
        <div class="panel">
          <div class="panel__head"><span class="panel__title">Your booking</span></div>
          <div class="panel__body stack">
            <dl class="dl">
              <dt>Service</dt><dd>Deep tissue 90</dd>
              <dt>When</dt><dd class="numeric">{{ picked() || 'Pick a time' }}</dd>
              <dt>Hold</dt><dd class="numeric">{{ held() ? held() + ' — 10 min' : 'None' }}</dd>
              <dt>Provider</dt><dd>Any available</dd>
              <dt>Deposit</dt><dd class="numeric">$60.00</dd>
              <dt>Balance</dt><dd class="numeric">$180.00</dd>
            </dl>
            <button type="button" class="btn btn--primary btn--lg" [disabled]="!canPay()" (click)="pay()">
              @switch (payState()) {
                @case ('paying')    { Contacting gateway… }
                @case ('ambiguous') { Checking the original… }
                @case ('done')      { Booked }
                @default            { Continue to deposit }
              }
            </button>
            <p class="subtle">Card details are tokenized. We never see or store the full number.</p>
          </div>
        </div>

        @switch (payState()) {
          @case ('ambiguous') {
            <app-state-panel
              state="timeout"
              title="Payment outcome unknown"
              body="The gateway has not confirmed. We are checking the original request by its idempotency key rather than charging again — do not retry."
            />
          }
          @case ('done') {
            <app-state-panel
              state="queued"
              title="Charged once, confirmed"
              body="The original request settled. Querying rather than retrying is what stopped a double charge."
            />
          }
          @default {
            <app-state-panel
              state="first-use"
              title="Nothing booked yet"
              body="Pick a time and continue — the deposit step deliberately demonstrates the ambiguous-outcome path."
            />
          }
        }
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; }

    .slots { display: grid; grid-template-columns: repeat(auto-fill, minmax(112px, 1fr)); gap: var(--space-3); }

    .tslot {
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 2px;
      min-height: 60px;
      padding: var(--space-2);
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-md);
      background: var(--bg-canvas);
      color: var(--fg-default);
      font-size: var(--text-sm);
      font-weight: var(--weight-bold);
      cursor: pointer;
      transition: all var(--dur-fast) var(--ease-out);

      &:hover:not(:disabled) { border-color: var(--border-accent); background: var(--bg-subtle); transform: translateY(-1px); }

      &:disabled { opacity: 0.45; cursor: not-allowed; }

      &.is-picked { background: var(--grad-accent); color: #fff; border-color: transparent; }
    }

    .tslot__who { font-size: var(--text-2xs); font-weight: var(--weight-regular); opacity: 0.75; }

    label { display: block; font-size: var(--text-sm); font-weight: var(--weight-bold); margin-bottom: var(--space-2); }
  `],
})
export class Booking {
  private readonly toast = inject(ToastService);
  private readonly store = inject(WorkspaceStore);

  protected readonly picked = signal<string | null>(null);
  protected readonly held = signal<string | null>(null);
  protected readonly payState = signal<'idle' | 'paying' | 'ambiguous' | 'done'>('idle');

  protected readonly canPay = computed(() => !!this.picked() && this.payState() === 'idle');

  protected hold(): void {
    if (!this.picked()) { this.toast.warn('Pick a time first', 'Choose a slot to hold it for ten minutes.'); return; }
    this.held.set(this.picked());
    this.toast.success('Slot held for 10 minutes', `${this.picked()} — the hold releases automatically if you do not confirm.`);
  }

  protected waitlist(): void {
    this.toast.success('Added to the waitlist', 'We will message you if a slot opens, subject to your contact consent.');
  }

  /** Deliberately lands on the ambiguous outcome — the path that is hard to retrofit. */
  protected async pay(): Promise<void> {
    this.payState.set('paying');
    await new Promise((r) => setTimeout(r, 900));
    this.payState.set('ambiguous');
    this.toast.warn(
      'Payment outcome unknown',
      'The gateway accepted the request but has not confirmed. We are querying the original — do not pay again.',
      API_ERROR.paymentOutcomeAmbiguous.code,
    );
    await new Promise((r) => setTimeout(r, 1600));
    this.payState.set('done');
    this.store.createAppointment();
    this.toast.success('Confirmed — charged once', 'The original request settled. No second charge was made.');
  }

  protected readonly slots = [
    { time: '9:00am',  provider: 'Lena',  open: true },
    { time: '10:30am', provider: 'Priya', open: true },
    { time: '12:00pm', provider: '—',     open: false },
    { time: '1:30pm',  provider: 'Marco', open: true },
    { time: '3:00pm',  provider: 'Lena',  open: true },
    { time: '4:30pm',  provider: '—',     open: false },
    { time: '6:00pm',  provider: 'Priya', open: true },
    { time: '7:30pm',  provider: 'Marco', open: true },
  ];
}
