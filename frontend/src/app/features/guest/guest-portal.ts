import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { GuestSession, type GuestMeDto } from '../../core/services/guest-session.service';

/**
 * /guest — the signed-in guest's own page. Everything shown comes from
 * GET /guest/me, which answers only for the guest the session was issued to;
 * contact values arrive masked.
 */
@Component({
  selector: 'app-guest-portal',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './guest.scss',
  template: `
    <main class="panel" id="main">
      <p class="panel__brand">AARFID SpMS</p>
      @if (me(); as g) {
        <h1 class="panel__title">Hello{{ g.preferredName ? ', ' + g.preferredName : '' }}</h1>
        <p class="panel__sub">You're signed in until {{ expires() }}.</p>

        <h2 class="panel__sub"><strong>How we reach you</strong></h2>
        <ul class="list">
          @for (c of g.contacts; track c.displayHint) {
            <li><span>{{ c.contactType }}</span><span>{{ c.displayHint }}{{ c.verified ? '' : ' (unverified)' }}</span></li>
          } @empty {
            <li>No contact details on file.</li>
          }
        </ul>
        <div class="actions">
          <button type="button" class="btn" (click)="signOut()">Sign out</button>
        </div>
      } @else if (error()) {
        <h1 class="panel__title">Your session has ended</h1>
        <p class="panel__sub">{{ error() }}</p>
        <div class="actions"><a class="btn btn--primary" routerLink="/guest/sign-in">Send me a new link</a></div>
      } @else {
        <p class="panel__sub" role="status">Loading…</p>
      }
    </main>
  `,
})
export class GuestPortal implements OnInit {
  private readonly guest = inject(GuestSession);
  private readonly router = inject(Router);
  protected readonly me = signal<GuestMeDto | null>(null);
  protected readonly error = signal<string | null>(null);

  protected expires(): string {
    const s = this.guest.session();
    return s ? new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' }).format(new Date(s.expiresUtc)) : '';
  }

  async ngOnInit(): Promise<void> {
    if (!this.guest.active()) {
      this.error.set('Sign-in links last a short time. Ask for a new one to continue.');
      return;
    }
    try {
      this.me.set(await this.guest.me());
    } catch (err) {
      this.error.set((err as { detail?: string | null })?.detail ?? 'We could not load your details.');
    }
  }

  protected signOut(): void {
    this.guest.end();
    void this.router.navigateByUrl('/guest/sign-in');
  }
}
