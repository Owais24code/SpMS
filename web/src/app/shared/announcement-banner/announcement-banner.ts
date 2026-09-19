import { Component, ChangeDetectionStrategy, signal } from '@angular/core';

const STORAGE_KEY = 'spms-banner-dismissed';

@Component({
  selector: 'app-announcement-banner',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (visible()) {
      <div class="banner" role="region" aria-label="Announcement">
        <div class="banner__inner container">
          <span class="banner__tag">New</span>
          <p class="banner__text">
            SpMS R1 adds provider-tablet offline delivery and quiet guest notification.
          </p>
          <button
            type="button"
            class="banner__close"
            (click)="dismiss()"
            aria-label="Dismiss announcement"
          >
            <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
              <path d="M6 6l12 12M18 6L6 18" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" />
            </svg>
          </button>
        </div>
      </div>
    }
  `,
  styleUrl: './announcement-banner.scss',
})
export class AnnouncementBanner {
  protected readonly visible = signal(this.readInitial());

  protected dismiss(): void {
    this.visible.set(false);
    try {
      sessionStorage.setItem(STORAGE_KEY, '1');
    } catch {
      /* storage blocked — banner simply returns next load */
    }
  }

  private readInitial(): boolean {
    try {
      return sessionStorage.getItem(STORAGE_KEY) !== '1';
    } catch {
      return true;
    }
  }
}
