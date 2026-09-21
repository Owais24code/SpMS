import { Component, ChangeDetectionStrategy, inject } from '@angular/core';
import { RouterLink, ActivatedRoute } from '@angular/router';
import { AuthService } from '../../../core/services/auth.service';

@Component({
  selector: 'app-forbidden',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="fb">
      <span class="fb__icon" aria-hidden="true">
        <svg viewBox="0 0 24 24" focusable="false">
          <path d="M6 11V8a6 6 0 1 1 12 0v3M5 11h14v9H5z" fill="none" stroke="currentColor"
                stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" />
        </svg>
      </span>

      <h1 class="fb__title">This screen is not part of your role</h1>
      <p class="fb__copy">
        You are signed in as <strong>{{ auth.user()?.roleLabel }}</strong>. This area needs
        a permission your role does not hold, so it is hidden rather than shown empty.
      </p>

      @if (need()) {
        <p class="fb__need numeric">Requires: {{ need() }}</p>
      }

      <div class="row">
        <a class="btn btn--primary" routerLink="/app">Back to dashboard</a>
        <a class="btn btn--secondary" routerLink="/sign-in">Switch role</a>
      </div>

      <p class="fb__hint">If you need this access, a spa manager can request it for you.</p>
    </div>
  `,
  styles: [`
    :host { display: block; }

    .fb {
      max-width: 60ch;
      margin-inline: auto;
      text-align: center;
      padding-block: var(--space-16);
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: var(--space-4);
    }

    .fb__icon {
      display: grid; place-items: center;
      width: 64px; height: 64px;
      border-radius: var(--radius-full);
      background: var(--bg-muted);
      color: var(--fg-muted);
      svg { width: 30px; height: 30px; }
    }

    .fb__title { font-size: var(--text-2xl); }
    .fb__copy { color: var(--fg-muted); line-height: var(--leading-loose); }
    .fb__need { font-size: var(--text-xs); color: var(--fg-subtle); }
    .fb__hint { font-size: var(--text-xs); color: var(--fg-subtle); margin-top: var(--space-2); }
    .row { margin-top: var(--space-2); }
  `],
})
export class Forbidden {
  protected readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);

  protected need(): string {
    return this.route.snapshot.queryParamMap.get('need') ?? '';
  }
}
