import { Component, ChangeDetectionStrategy, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../../../core/services/auth.service';

/**
 * Sign-up for local accounts. The account does nothing until an
 * administrator approves it and gives it a role (Staff → sign-ups), so this
 * page can be public without letting anyone in.
 */
@Component({
  selector: 'app-register',
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
        <h1 class="card__title">Create an account</h1>
        @if (auth.mode !== 'local') {
          <p class="card__sub">Accounts here come from your organisation's sign-in. <a routerLink="/sign-in">Go to sign-in</a>.</p>
        } @else {
          @if (auth.error(); as err) { <p class="card__error" role="alert">{{ err }}</p> }
          <p class="card__sub">An administrator approves every new account and gives it a role before it can be used.</p>
          <form class="form" (submit)="$event.preventDefault(); submit()">
            <label>Your name
              <input id="reg-name" autocomplete="name" [value]="name()" (input)="name.set(value($event))" required maxlength="80" />
            </label>
            <label>Work email
              <input id="reg-email" type="email" autocomplete="email" [value]="email()" (input)="email.set(value($event))" required />
            </label>
            <label>Password
              <input id="reg-password" type="password" autocomplete="new-password" [value]="password()" (input)="password.set(value($event))" required />
            </label>
            <label>Repeat the password
              <input id="reg-repeat" type="password" autocomplete="new-password" [value]="repeat()" (input)="repeat.set(value($event))" required />
            </label>
            <p class="card__note">At least 10 characters, and not containing your email name.</p>
            @if (password() && repeat() && password() !== repeat()) { <p class="card__error">The two passwords are different.</p> }
            <button type="submit" class="btn btn--primary btn--lg card__go"
              [disabled]="auth.busy() || !name().trim() || !email().trim() || !password() || password() !== repeat()">
              {{ auth.busy() ? 'Sending…' : 'Create account' }}
            </button>
          </form>
          <p class="card__note">Already have an account? <a routerLink="/sign-in">Sign in</a>.</p>
        }
      </main>
    </div>
  `,
  styleUrl: '../sign-in/sign-in.scss',
})
export class Register {
  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly name = signal('');
  protected readonly email = signal('');
  protected readonly password = signal('');
  protected readonly repeat = signal('');

  protected value(e: Event): string {
    return (e.target as HTMLInputElement).value;
  }

  protected async submit(): Promise<void> {
    if (await this.auth.register(this.email().trim(), this.name().trim(), this.password()))
      void this.router.navigate(['/sign-in'], { queryParams: { registered: '1' } });
  }
}
