import { Component, ChangeDetectionStrategy, inject, signal } from '@angular/core';
import { Router, ActivatedRoute, RouterLink } from '@angular/router';
import { AuthService, ROLE_PRESETS, type RoleKey } from '../../../core/services/auth.service';

@Component({
  selector: 'app-sign-in',
  standalone: true,
  imports: [RouterLink],
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

        @if (auth.error(); as err) {
          <p class="card__error" role="alert">{{ err }}</p>
        }

        @if (auth.mode === 'entra') {
          <p class="card__sub">Sign in with your organisation's Microsoft account.</p>
          <button type="button" class="btn btn--primary btn--lg card__go" [disabled]="auth.busy()" (click)="entra()">
            {{ auth.busy() ? 'Redirecting…' : 'Sign in with Microsoft' }}
          </button>
        } @else if (auth.mode === 'local') {
          @if (registered()) {
            <p class="card__notice" role="status">Thanks. An administrator will approve your account; then you can sign in here.</p>
          }
          @if (auth.passwordChangeRequired()) {
            <p class="card__sub">You signed in with a temporary password. Choose your own password to continue.</p>
            <form class="form" (submit)="$event.preventDefault(); change()">
              <label>New password
                <input id="new-password" type="password" autocomplete="new-password" [value]="next()" (input)="next.set(value($event))" required />
              </label>
              <label>Repeat the new password
                <input id="repeat-password" type="password" autocomplete="new-password" [value]="repeat()" (input)="repeat.set(value($event))" required />
              </label>
              <p class="card__note">At least 10 characters.</p>
              <button type="submit" class="btn btn--primary btn--lg card__go" [disabled]="auth.busy() || !next() || next() !== repeat()">
                {{ auth.busy() ? 'Saving…' : 'Set password and continue' }}
              </button>
              @if (next() && repeat() && next() !== repeat()) { <p class="card__error">The two passwords are different.</p> }
            </form>
            <button type="button" class="linklike" (click)="auth.cancelPasswordChange()">Back to sign-in</button>
          } @else {
            <p class="card__sub">Sign in with your work email and password.</p>
            <form class="form" (submit)="$event.preventDefault(); password()">
              <label>Email
                <input id="email" type="email" autocomplete="username" [value]="email()" (input)="email.set(value($event))" required />
              </label>
              <label>Password
                <input id="password" type="password" autocomplete="current-password" [value]="secret()" (input)="secret.set(value($event))" required />
              </label>
              <button type="submit" class="btn btn--primary btn--lg card__go" [disabled]="auth.busy() || !email() || !secret()">
                {{ auth.busy() ? 'Signing in…' : 'Sign in' }}
              </button>
            </form>
            <p class="card__note">New here? <a routerLink="/register">Create an account</a>. Forgot your password? Ask an administrator to reset it.</p>
          }
        } @else {
          <p class="card__sub">
            @if (auth.mode === 'demo') {
              Development sign-in — each role is a seeded login. The API resolves it
              exactly as it resolves an Entra token: roles, properties and scopes come
              from the database.
            } @else {
              Offline demo — no API is running, so these presets stand in for the server.
            }
          </p>

          <fieldset class="roles" [disabled]="auth.busy()">
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
                <span class="role__scopes numeric">{{ auth.mode === 'demo' ? r.login : r.scopes.length + ' scopes' }}</span>
              </label>
            }
          </fieldset>

          <button type="button" class="btn btn--primary btn--lg card__go" [disabled]="auth.busy()" (click)="go()">
            {{ auth.busy() ? 'Signing in…' : 'Enter workspace' }}
          </button>

          <p class="card__note">
            Signing in as Front desk hides finance and configuration screens entirely —
            that is the scope guard working, not a missing page.
          </p>
        }
      </main>
    </div>
  `,
  styleUrl: './sign-in.scss',
})
export class SignIn {
  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly roles = (Object.keys(ROLE_PRESETS) as RoleKey[]).map((key) => ({
    key,
    ...ROLE_PRESETS[key],
  }));

  protected readonly picked = signal<RoleKey>('spa_manager');

  private get returnUrl(): string {
    const target = this.route.snapshot.queryParamMap.get('returnUrl') ?? '/app';
    return target.startsWith('/') && !target.startsWith('//') ? target : '/app';
  }

  protected async go(): Promise<void> {
    // A kiosk runs one screen, full-screen; everyone else goes where they were going.
    if (await this.auth.signIn(this.picked())) void this.router.navigateByUrl(this.picked() === 'kiosk' ? '/kiosk' : this.returnUrl);
  }

  protected readonly email = signal('');
  protected readonly secret = signal('');
  protected readonly next = signal('');
  protected readonly repeat = signal('');
  protected readonly registered = signal(this.route.snapshot.queryParamMap.get('registered') === '1');

  protected value(e: Event): string {
    return (e.target as HTMLInputElement).value;
  }

  protected async password(): Promise<void> {
    const result = await this.auth.signInWithPassword(this.email().trim(), this.secret());
    if (result === 'signed-in') void this.router.navigateByUrl(this.returnUrl);
  }

  protected async change(): Promise<void> {
    if (await this.auth.changePassword(this.secret(), this.next())) void this.router.navigateByUrl(this.returnUrl);
    this.next.set('');
    this.repeat.set('');
  }

  protected entra(): void {
    void this.auth.signInWithEntra(this.returnUrl);
  }
}
