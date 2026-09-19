import { Component, ChangeDetectionStrategy, signal } from '@angular/core';
import { PageHeader } from '../../shared/components/page-header/page-header';
import { StatePanel } from '../../shared/components/state-panel/state-panel';

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
      <button type="button" class="btn btn--secondary">Waitlist</button>
      <button type="button" class="btn btn--primary">Hold slot</button>
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
              <dt>Provider</dt><dd>Any available</dd>
              <dt>Deposit</dt><dd class="numeric">$60.00</dd>
              <dt>Balance</dt><dd class="numeric">$180.00</dd>
            </dl>
            <button type="button" class="btn btn--primary btn--lg" [disabled]="!picked()">Continue to deposit</button>
            <p class="subtle">Card details are tokenized. We never see or store the full number.</p>
          </div>
        </div>

        <app-state-panel
          state="timeout"
          title="Payment outcome unknown"
          body="The gateway has not confirmed. We are checking the original request rather than charging again — do not retry."
        />
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
  protected readonly picked = signal<string | null>(null);

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
