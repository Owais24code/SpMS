import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { GuestSession } from '../../core/services/guest-session.service';

/**
 * /g/:token — where a magic link lands.
 *
 * Redeems the link once, then replaces the URL so the spent token is not left
 * in the address bar or the history (and not re-sent by a refresh, which
 * would only show "no longer valid").
 */
@Component({
  selector: 'app-guest-landing',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './guest.scss',
  template: `
    <main class="panel" id="main">
      <p class="panel__brand">AARFID SpMS</p>
      @if (failed()) {
        <h1 class="panel__title">This link has expired</h1>
        <p class="panel__sub">{{ failed() }}</p>
        <div class="actions">
          <a class="btn btn--primary" routerLink="/guest/sign-in">Send me a new link</a>
        </div>
      } @else {
        <h1 class="panel__title">Signing you in…</h1>
        <p class="panel__sub" role="status">One moment while we check your link.</p>
      }
    </main>
  `,
})
export class GuestLanding implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly guest = inject(GuestSession);

  protected readonly failed = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    const token = this.route.snapshot.paramMap.get('token') ?? '';
    try {
      await this.guest.redeem(token);
      void this.router.navigateByUrl('/guest', { replaceUrl: true });
    } catch (err) {
      const e = err as { detail?: string | null };
      this.failed.set(e?.detail ?? 'Links work once and only for a short time. Ask for a new one below.');
      void this.router.navigate([], { replaceUrl: true, state: {} });
    }
  }
}
