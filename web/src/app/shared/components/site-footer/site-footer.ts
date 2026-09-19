import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink } from '@angular/router';
import { SITE_NAV } from '../../../core/models/nav.model';

@Component({
  selector: 'app-site-footer',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <footer class="ft">
      <div class="container ft__inner">
        <div class="ft__brand">
          <span class="ft__name">AARFID<span class="ft__product">SpMS</span></span>
          <p class="ft__tagline">
            Enhance the value of a credential while modernizing property experience.
          </p>
        </div>

        <nav class="ft__nav" aria-label="Footer">
          <ul>
            @for (item of navItems; track item.path) {
              <li><a [routerLink]="item.path">{{ item.label }}</a></li>
            }
          </ul>
        </nav>

        <div class="ft__contact">
          <a routerLink="/app">Workspace</a>
          <a href="tel:+17169923999" class="numeric">716-992-3999</a>
          <a href="https://aarfid.com" rel="noopener">aarfid.com</a>
        </div>
      </div>

      <div class="container ft__legal">
        <p>&copy; {{ year }} AARFID. All rights reserved.</p>
      </div>
    </footer>
  `,
  styleUrl: './site-footer.scss',
})
export class SiteFooter {
  protected readonly navItems = SITE_NAV;
  protected readonly year = new Date().getFullYear();
}
