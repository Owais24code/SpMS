import { Component, ChangeDetectionStrategy, inject, signal } from '@angular/core';
import { Router, ActivatedRoute } from '@angular/router';
import { AuthService, ROLE_PRESETS, type RoleKey } from '../../../core/services/auth.service';

@Component({
  selector: 'app-sign-in',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="wrap">
      <div class="art" aria-hidden="true">
        <span class="art__orb art__orb--a"></span>
        <span class="art__orb art__orb--b"></span>
        <div class="art__copy">
          <p class="art__brand">AARFID <span>SpMS</span></p>
          <p class="art__line">Every treatment, room and credential accounted for.</p>
        </div>
      </div>

      <main class="card" id="main">
        <h1 class="card__title">Sign in</h1>
        <p class="card__sub">
          Demo build — pick a role to see how the workspace changes. Real deployments
          authenticate against your identity provider.
        </p>

        <fieldset class="roles">
          <legend class="visually-hidden">Choose a role</legend>
          @for (r of roles; track r.key) {
            <label class="role" [class.is-picked]="picked() === r.key">
              <input
                type="radio" name="role" [value]="r.key"
                [checked]="picked() === r.key"
                (change)="picked.set(r.key)"
              />
              <span class="role__name">{{ r.roleLabel }}</span>
              <span class="role__who">{{ r.name }} · {{ r.property }}</span>
              <span class="role__scopes numeric">{{ r.scopes.length }} scopes</span>
            </label>
          }
        </fieldset>

        <button type="button" class="btn btn--primary btn--lg card__go" (click)="go()">
          Enter workspace
        </button>

        <p class="card__note">
          Signing in as Front desk hides finance and configuration screens entirely —
          that is the scope guard working, not a missing page.
        </p>
      </main>
    </div>
  `,
  styleUrl: './sign-in.scss',
})
export class SignIn {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly roles = (Object.keys(ROLE_PRESETS) as RoleKey[]).map((key) => ({
    key,
    ...ROLE_PRESETS[key],
  }));

  protected readonly picked = signal<RoleKey>('spa_manager');

  protected go(): void {
    this.auth.signIn(this.picked());
    const target = this.route.snapshot.queryParamMap.get('returnUrl') ?? '/app';
    this.router.navigateByUrl(target);
  }
}
