import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CAPABILITIES } from '../../../core/data/site-content';

@Component({
  selector: 'app-modules',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="lead">
      <div class="container">
        <p class="eyebrow">Modules</p>
        <h1 class="lead__title">What SpMS covers</h1>
        <p class="lead__copy">
          Each module is a working screen in the product, not a roadmap item.
          Open the workspace to walk any of them with realistic data.
        </p>
        <div class="row" style="margin-top: var(--space-6)">
          <a class="btn btn--primary" routerLink="/app">Open the workspace</a>
          <a class="btn btn--secondary" routerLink="/contact">Talk to us</a>
        </div>
      </div>
    </section>

    <section class="section">
      <div class="container">
        <ul class="mods">
          @for (c of capabilities; track c.title) {
            <li class="mod">
              <span class="mod__icon" aria-hidden="true">
                <svg viewBox="0 0 24 24" focusable="false">
                  <path [attr.d]="c.icon" fill="none" stroke="currentColor" stroke-width="1.7"
                        stroke-linecap="round" stroke-linejoin="round" />
                </svg>
              </span>
              <h2 class="mod__title">{{ c.title }}</h2>
              <p class="mod__copy">{{ c.copy }}</p>
              <a class="mod__link" [routerLink]="linkFor(c.title)">
                Open
                <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
                  <path d="M5 12h14M13 6l6 6-6 6" fill="none" stroke="currentColor"
                        stroke-width="2" stroke-linecap="round" stroke-linejoin="round" />
                </svg>
              </a>
            </li>
          }
        </ul>
      </div>
    </section>
  `,
  styles: [`
    :host { display: block; }

    .lead {
      padding-block: clamp(3rem, 7vw, 5rem);
      background: var(--grad-page);
      border-bottom: 1px solid var(--border-subtle);
    }

    .lead__title { margin-top: var(--space-3); font-size: clamp(2rem, 5vw, 3rem); letter-spacing: -0.02em; }
    .lead__copy { margin-top: var(--space-5); max-width: 62ch; color: var(--fg-muted); line-height: var(--leading-loose); font-size: var(--text-lg); }

    .mods { list-style: none; display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); gap: var(--space-5); }

    .mod {
      display: flex;
      flex-direction: column;
      padding: var(--space-6);
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-lg);
      background: var(--grad-surface);
      transition: border-color var(--dur-base) var(--ease-out), transform var(--dur-base) var(--ease-out), box-shadow var(--dur-base) var(--ease-out);

      &:hover {
        border-color: var(--border-accent);
        transform: translateY(-3px);
        box-shadow: var(--shadow-md);
        .mod__icon { background: var(--grad-accent); color: #fff; }
      }

      &__icon {
        display: inline-grid;
        place-items: center;
        width: 44px; height: 44px;
        border-radius: var(--radius-md);
        background: var(--bg-muted);
        color: var(--fg-accent);
        transition: background var(--dur-base) var(--ease-out), color var(--dur-base) var(--ease-out);

        svg { width: 22px; height: 22px; }
      }

      &__title { margin-top: var(--space-4); font-size: var(--text-lg); }
      &__copy { margin-top: var(--space-3); font-size: var(--text-sm); color: var(--fg-muted); line-height: var(--leading-loose); flex: 1; }

      &__link {
        margin-top: var(--space-5);
        display: inline-flex;
        align-items: center;
        gap: var(--space-2);
        align-self: flex-start;
        color: var(--fg-link);
        font-size: var(--text-sm);
        font-weight: var(--weight-bold);
        text-decoration: none;

        svg { width: 16px; height: 16px; transition: transform var(--dur-fast) var(--ease-out); }
        &:hover svg { transform: translateX(3px); }
      }
    }
  `],
})
export class Modules {
  protected readonly capabilities = CAPABILITIES;

  private readonly map: Record<string, string> = {
    'Scheduling board': '/app/schedule',
    'Guest booking': '/app/booking',
    'Arrival and check-in': '/app/check-in',
    'Provider tablet': '/app/treatments',
    'Inventory and readiness': '/app/inventory',
    'Quiet notification': '/app/devices',
  };

  protected linkFor(title: string): string {
    return this.map[title] ?? '/app';
  }
}
