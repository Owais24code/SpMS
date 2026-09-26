import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { OpsApi } from '../../core/api/ops-api';
import type { KioskBookingDto } from '../../core/models/ops';
import type { ApiProblem } from '../../core/models/api-problem';

type Step = 'find' | 'confirm' | 'done';

/**
 * The lobby kiosk: a guest finds today's booking with their confirmation
 * number and last name and checks themselves in. It shows one booking at a
 * time, never a list, and returns to the start on its own so the next guest
 * never sees the last one.
 */
@Component({
  selector: 'app-kiosk',
  standalone: true,
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="kiosk" aria-live="polite">
      <h1>Welcome</h1>
      @switch (step()) {
        @case ('find') {
          <p>Check in for today's treatment.</p>
          <form (ngSubmit)="find()" class="stack">
            <label>Confirmation number <input [(ngModel)]="confirmation" name="cn" autocomplete="off" autocapitalize="characters" /></label>
            <label>Last name <input [(ngModel)]="lastName" name="ln" autocomplete="off" /></label>
            @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
            <button type="submit" class="big" [disabled]="busy() || !confirmation.trim() || !lastName.trim()">Find my booking</button>
          </form>
        }
        @case ('confirm') {
          @if (booking(); as b) {
            <p class="lead">{{ b.greeting ? 'Hello ' + b.greeting + '.' : 'Hello.' }}</p>
            <p>{{ b.serviceName }} at {{ time(b.startUtc) }}</p>
            @if (b.canCheckIn) {
              <button type="button" class="big" [disabled]="busy()" (click)="checkIn(b)">Check me in</button>
            } @else {
              <p>This booking is {{ b.status }}. Please see the front desk.</p>
            }
            @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
            <button type="button" class="link" (click)="reset()">Not you? Start again</button>
          }
        }
        @case ('done') {
          <p class="lead" data-testid="kiosk-done">{{ message() }}</p>
        }
      }
    </main>
  `,
  styles: [`
    :host { display: grid; place-items: center; min-height: 100vh; background: var(--surface, #fff); }
    .kiosk { width: min(560px, 92vw); text-align: center; display: grid; gap: var(--space-4, 16px); }
    h1 { font-size: 2.5rem; margin: 0; }
    label { display: grid; gap: 8px; text-align: left; font-size: 1.25rem; }
    input { font-size: 1.5rem; padding: 12px; }
    .big { font-size: 1.5rem; padding: 16px 24px; }
    .lead { font-size: 1.75rem; }
    .error { color: var(--danger, #b00020); }
    .link { background: none; border: 0; text-decoration: underline; cursor: pointer; font-size: 1rem; }
    .stack { display: grid; gap: 16px; }
  `],
})
export class Kiosk {
  private readonly api = inject(OpsApi);
  protected readonly step = signal<Step>('find');
  protected readonly booking = signal<KioskBookingDto | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly message = signal('');
  protected readonly busy = signal(false);
  protected confirmation = '';
  protected lastName = '';
  private timer: ReturnType<typeof setTimeout> | null = null;

  protected time(utc: string): string { return new Date(utc).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' }); }

  protected async find(): Promise<void> {
    this.error.set(null); this.busy.set(true);
    try {
      this.booking.set(await this.api.kioskLookup(this.confirmation.trim(), this.lastName.trim()));
      this.step.set('confirm');
      this.idle(60_000);
    } catch (e) { this.error.set((e as ApiProblem).detail ?? 'Please see the front desk.'); }
    finally { this.busy.set(false); }
  }

  protected async checkIn(b: KioskBookingDto): Promise<void> {
    this.error.set(null); this.busy.set(true);
    try {
      const r = await this.api.kioskCheckIn(b.appointmentId, this.confirmation.trim(), this.lastName.trim());
      this.message.set(r.message);
      this.step.set('done');
      this.idle(8_000);
    } catch (e) { this.error.set((e as ApiProblem).detail ?? 'Please see the front desk.'); }
    finally { this.busy.set(false); }
  }

  /** Back to the start after a while, so the screen never keeps a guest's booking. */
  private idle(ms: number): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => this.reset(), ms);
  }

  protected reset(): void {
    if (this.timer) clearTimeout(this.timer);
    this.confirmation = ''; this.lastName = '';
    this.booking.set(null); this.error.set(null); this.step.set('find');
  }
}
