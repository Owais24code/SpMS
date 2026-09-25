import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink } from '@angular/router';
import { SITE_NAV } from '../../core/models/nav.model';

@Component({
  selector: 'app-not-found',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './not-found.html',
  styleUrl: './not-found.scss',
})
export class NotFound {
  protected readonly navItems = SITE_NAV.filter((i) => i.path !== '/');

  protected goBack(): void {
    history.length > 1 ? history.back() : (location.href = '/');
  }
}
