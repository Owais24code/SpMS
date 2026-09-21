import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-platform',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="lead">
      <div class="container">
        <p class="eyebrow">Platform</p>
        <h1 class="lead__title">One authoritative record, four surfaces</h1>
        <p class="lead__copy">
          Front desk, provider, housekeeping and guest all read the same state. What each
          one sees is decided by their role, their relationship to the record, and the
          purpose they are acting for — not by which screen they happen to be on.
        </p>
      </div>
    </section>

    <section class="section">
      <div class="container">
        <ul class="pillars">
          @for (p of pillars; track p.title) {
            <li class="pillar">
              <span class="pillar__num numeric">{{ $index + 1 }}</span>
              <div>
                <h2 class="pillar__title">{{ p.title }}</h2>
                <p class="pillar__copy">{{ p.copy }}</p>
              </div>
            </li>
          }
        </ul>
      </div>
    </section>

    <section class="section section--alt">
      <div class="container">
        <h2 class="sec-title">Surfaces</h2>
        <div class="surfaces">
          @for (s of surfaces; track s.name) {
            <article class="surface">
              <h3>{{ s.name }}</h3>
              <p class="surface__who">{{ s.who }}</p>
              <p class="surface__copy">{{ s.copy }}</p>
            </article>
          }
        </div>
      </div>
    </section>

    <section class="section">
      <div class="container cta">
        <h2 class="sec-title">See it against your property</h2>
        <div class="row">
          <a class="btn btn--primary btn--lg" routerLink="/contact">Book a demo</a>
          <a class="btn btn--secondary btn--lg" routerLink="/app">Open the workspace</a>
        </div>
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

    .section--alt { background: var(--bg-subtle); }
    .sec-title { font-size: clamp(1.5rem, 3vw, 2rem); margin-bottom: var(--space-8); }

    .pillars { list-style: none; display: flex; flex-direction: column; gap: var(--space-8); }

    .pillar {
      display: flex;
      gap: var(--space-5);
      padding-bottom: var(--space-8);
      border-bottom: 1px solid var(--border-subtle);

      &:last-child { border-bottom: 0; padding-bottom: 0; }

      &__num {
        flex-shrink: 0;
        display: grid;
        place-items: center;
        width: 44px; height: 44px;
        border-radius: var(--radius-full);
        background: var(--grad-accent);
        color: #fff;
        font-weight: var(--weight-bold);
      }

      &__title { font-size: var(--text-xl); }
      &__copy { margin-top: var(--space-3); color: var(--fg-muted); line-height: var(--leading-loose); max-width: 72ch; }
    }

    .surfaces { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: var(--space-5); }

    .surface {
      padding: var(--space-6);
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-lg);
      background: var(--grad-surface);
      transition: border-color var(--dur-base) var(--ease-out), transform var(--dur-base) var(--ease-out);

      &:hover { border-color: var(--border-accent); transform: translateY(-3px); }

      h3 { font-size: var(--text-lg); }
      &__who { margin-top: var(--space-1); font-size: var(--text-xs); color: var(--fg-accent); font-weight: var(--weight-bold); }
      &__copy { margin-top: var(--space-3); font-size: var(--text-sm); color: var(--fg-muted); line-height: var(--leading-loose); }
    }

    .cta { text-align: center; .row { justify-content: center; margin-top: var(--space-6); } }
  `],
})
export class Platform {
  protected readonly pillars = [
    {
      title: 'Exactly one owner per capability',
      copy: 'Guest identity, payment capture and room inventory each have one declared owning system per property, effective from a stated moment. Changes are proposed, preflight-checked and approved by a second person before they take effect.',
    },
    {
      title: 'Conflicts that tell you what happens next',
      copy: 'A soft conflict can be overridden with a recorded reason. A hard one — the same room twice — cannot be overridden by anyone, so the interface shows it as blocked rather than offering a button that would fail.',
    },
    {
      title: 'Data served by purpose, not by screen',
      copy: 'Health, employment and screening fields are excluded from the payload unless the caller has the scope, the relationship and the stated purpose. Hiding a column in the interface is not the same thing, and we do not rely on it.',
    },
    {
      title: 'Work that survives a bad moment',
      copy: 'Every consequential edit carries the version it was based on. When someone else got there first, you keep what you typed and see what changed — rather than losing the lot to a refresh.',
    },
  ];

  protected readonly surfaces = [
    { name: 'Staff desktop',  who: 'Scheduler, front desk', copy: 'Board, arrivals, inventory and reports. The board is operated from the keyboard throughout.' },
    { name: 'Provider tablet', who: 'Therapists',            copy: 'Installable, works offline, queues status changes in order and never double-posts.' },
    { name: 'Guest web',       who: 'Members and visitors',  copy: 'Booking, changes and cancellation with the policy effects shown before confirming.' },
    { name: 'Kiosk',           who: 'Self-service arrival',  copy: 'Locked-down profile that purges abandoned data rather than preserving it.' },
  ];
}
