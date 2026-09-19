import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink } from '@angular/router';
import { FaqAccordion } from '../../../shared/components/faq-accordion/faq-accordion';
import { CAPABILITIES, FAQ_ITEMS } from '../../../core/data/site-content';

@Component({
  selector: 'app-home',
  standalone: true,
  imports: [RouterLink, FaqAccordion],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './home.html',
  styleUrl: './home.scss',
})
export class Home {
  protected readonly capabilities = CAPABILITIES;
  protected readonly faqItems = FAQ_ITEMS;
}
