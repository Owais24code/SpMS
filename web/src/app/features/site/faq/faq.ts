import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink } from '@angular/router';
import { FaqAccordion } from '../../../shared/components/faq-accordion/faq-accordion';
import { FAQ_ITEMS } from '../../../core/data/site-content';

@Component({
  selector: 'app-faq',
  standalone: true,
  imports: [RouterLink, FaqAccordion],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="lead">
      <div class="container">
        <p class="eyebrow">FAQ</p>
        <h1 class="lead__title">Questions we get asked</h1>
        <p class="lead__copy">
          Specifics rather than generalities. If yours is not here, ask us directly —
          we would rather answer it properly than guess at it in a brochure.
        </p>
      </div>
    </section>

    <section class="section">
      <div class="container faq-wrap">
        <app-faq-accordion [items]="items" idPrefix="page-faq" />

        <aside class="ask">
          <h2 class="ask__title">Still unsure?</h2>
          <p class="ask__copy">Tell us about your property and we will answer against your setup.</p>
          <a class="btn btn--primary" routerLink="/contact">Ask us directly</a>
        </aside>
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

    .faq-wrap {
      display: grid;
      grid-template-columns: 1.6fr 0.9fr;
      gap: clamp(2rem, 5vw, 4rem);
      align-items: start;

      @media (max-width: 900px) { grid-template-columns: 1fr; }
    }

    .ask {
      padding: var(--space-6);
      border-radius: var(--radius-lg);
      background: var(--grad-brand);
      color: #fff;
      position: sticky;
      top: calc(var(--header-h) + var(--space-5));

      &__title { font-size: var(--text-lg); }
      &__copy { margin: var(--space-3) 0 var(--space-5); font-size: var(--text-sm); opacity: 0.86; line-height: var(--leading-loose); }
    }
  `],
})
export class Faq {
  protected readonly items = FAQ_ITEMS;
}
