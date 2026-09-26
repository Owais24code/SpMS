import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { GuestSession } from '../../core/services/guest-session.service';

/**
 * /guest/sign-in — ask for a magic link by email.
 *
 * The answer is the same whether or not the address is on file: a different
 * answer would let anyone test who is a guest here.
 */
@Component({
  selector: 'app-guest-sign-in',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './guest.scss',
  template: `
    <main class="panel" id="main">
      <p class="panel__brand">AARFID SpMS</p>
      <h1 class="panel__title">Manage your visit</h1>
      <p class="panel__sub">Enter the email you booked with and we'll send you a one-time sign-in link.</p>

      @if (sent()) {
        <p class="panel__ok" role="status">If that address is on file, a sign-in link is on its way. It works once, for 15 minutes.</p>
      }
      @if (error()) {
        <p class="panel__error" role="alert">{{ error() }}</p>
      }

      <form (submit)="send($event)">
        <label class="field">
          <span>Email</span>
          <input type="email" name="email" autocomplete="email" required
                 [value]="email()" (input)="email.set($any($event.target).value)" />
        </label>
        <div class="actions">
          <button type="submit" class="btn btn--primary" [disabled]="busy() || !email().includes('@')">
            {{ busy() ? 'Sending…' : 'Send link' }}
          </button>
        </div>
      </form>
    </main>
  `,
})
export class GuestSignIn {
  private readonly guest = inject(GuestSession);
  protected readonly email = signal('');
  protected readonly busy = signal(false);
  protected readonly sent = signal(false);
  protected readonly error = signal<string | null>(null);

  protected async send(e: Event): Promise<void> {
    e.preventDefault();
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.guest.requestLink(this.email().trim());
      this.sent.set(true);
    } catch (err) {
      const p = err as { detail?: string | null; status?: number };
      this.error.set(p?.status === 0 ? 'We could not reach the spa right now. Try again shortly.' : p?.detail ?? 'That did not work.');
    } finally {
      this.busy.set(false);
    }
  }
}
