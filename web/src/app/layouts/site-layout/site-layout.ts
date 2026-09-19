import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { SiteHeader } from '../../shared/components/site-header/site-header';
import { SiteFooter } from '../../shared/components/site-footer/site-footer';
import { AnnouncementBanner } from '../../shared/components/announcement-banner/announcement-banner';
import { FloatingContact } from '../../shared/components/floating-contact/floating-contact';

@Component({
  selector: 'app-site-layout',
  standalone: true,
  imports: [RouterOutlet, SiteHeader, SiteFooter, AnnouncementBanner, FloatingContact],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-announcement-banner />
    <app-site-header />
    <main id="main" tabindex="-1"><router-outlet /></main>
    <app-site-footer />
    <app-floating-contact />
  `,
  styles: [`
    :host { display: flex; flex-direction: column; min-height: 100vh; min-height: 100dvh; }
    main { flex: 1; outline: none; }
  `],
})
export class SiteLayout {}
