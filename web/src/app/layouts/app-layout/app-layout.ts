import { Component, ChangeDetectionStrategy, signal, inject, HostListener } from '@angular/core';
import { RouterOutlet, RouterLink, RouterLinkActive, Router, NavigationEnd } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { filter, map } from 'rxjs';
import { ThemeService } from '../../core/services/theme.service';
import { WORKSPACE_NAV } from '../../core/models/nav.model';

@Component({
  selector: 'app-app-layout',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './app-layout.html',
  styleUrl: './app-layout.scss',
})
export class AppLayout {
  private readonly router = inject(Router);

  protected readonly theme = inject(ThemeService);
  protected readonly groups = WORKSPACE_NAV;

  /** Collapsed rail on desktop; slide-over drawer below 1024px. */
  protected readonly collapsed = signal(this.readCollapsed());
  protected readonly drawerOpen = signal(false);

  /** Current page title, derived from the route so the top bar stays in step. */
  protected readonly current = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => this.labelFor(e.urlAfterRedirects)),
    ),
    { initialValue: this.labelFor(this.router.url) },
  );

  @HostListener('document:keydown.escape')
  protected onEscape(): void {
    if (this.drawerOpen()) this.drawerOpen.set(false);
  }

  protected toggleRail(): void {
    this.collapsed.update((v) => !v);
    try {
      localStorage.setItem('spms-rail', this.collapsed() ? '1' : '0');
    } catch {
      /* per-viewer convenience only */
    }
  }

  protected toggleDrawer(): void {
    this.drawerOpen.update((v) => !v);
  }

  protected closeDrawer(): void {
    this.drawerOpen.set(false);
  }

  private labelFor(url: string): string {
    const clean = url.split('?')[0].split('#')[0];
    for (const g of this.groups) {
      for (const i of g.items) {
        if (i.path === clean) return i.label;
      }
    }
    return 'Workspace';
  }

  private readCollapsed(): boolean {
    try {
      return localStorage.getItem('spms-rail') === '1';
    } catch {
      return false;
    }
  }
}
