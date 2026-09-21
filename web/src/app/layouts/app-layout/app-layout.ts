import { Component, ChangeDetectionStrategy, signal, inject, computed, HostListener } from '@angular/core';
import { RouterOutlet, RouterLink, RouterLinkActive, Router, NavigationEnd } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { filter, map } from 'rxjs';
import { ThemeService } from '../../core/services/theme.service';
import { AuthService } from '../../core/services/auth.service';
import { WorkspaceStore } from '../../core/services/workspace-store';
import { ToastService } from '../../core/services/toast.service';
import { WORKSPACE_NAV, type NavItem } from '../../core/models/nav.model';
import { FocusTrapDirective } from '../../shared/directives/focus-trap.directive';

@Component({
  selector: 'app-app-layout',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, FocusTrapDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './app-layout.html',
  styleUrl: './app-layout.scss',
})
export class AppLayout {
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  protected readonly theme = inject(ThemeService);
  protected readonly auth = inject(AuthService);
  protected readonly store = inject(WorkspaceStore);

  protected readonly collapsed = signal(this.readCollapsed());
  protected readonly drawerOpen = signal(false);
  protected readonly accountOpen = signal(false);
  protected readonly query = signal('');

  /** Only the nav entries this role can actually reach. */
  protected readonly groups = computed(() =>
    WORKSPACE_NAV
      .map((g) => ({ ...g, items: g.items.filter((i) => this.canSee(i)) }))
      .filter((g) => g.items.length > 0),
  );

  /** Badges come from the store, so they fall as work gets done. */
  protected badgeFor(item: NavItem): number | null {
    if (item.path === '/app/schedule') return this.store.openConflicts() || null;
    if (item.path === '/app/check-in') return this.store.pendingArrivals() || null;
    return null;
  }

  protected readonly current = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => this.labelFor(e.urlAfterRedirects)),
    ),
    { initialValue: this.labelFor(this.router.url) },
  );

  /** Type-ahead over the navigation — Enter jumps to the first match. */
  protected readonly matches = computed(() => {
    const q = this.query().trim().toLowerCase();
    if (q.length < 2) return [];
    return WORKSPACE_NAV
      .flatMap((g) => g.items)
      .filter((i) => this.canSee(i) && i.label.toLowerCase().includes(q))
      .slice(0, 6);
  });

  @HostListener('document:keydown.escape')
  protected onEscape(): void {
    if (this.accountOpen()) { this.accountOpen.set(false); return; }
    if (this.drawerOpen()) this.closeDrawer();
    if (this.query()) this.query.set('');
  }

  protected goToFirstMatch(): void {
    const first = this.matches()[0];
    if (first) {
      this.router.navigateByUrl(first.path);
      this.query.set('');
    }
  }

  protected toggleRail(): void {
    this.collapsed.update((v) => !v);
    try { localStorage.setItem('spms-rail', this.collapsed() ? '1' : '0'); } catch { /* ignore */ }
  }

  protected toggleDrawer(): void {
    this.drawerOpen.update((v) => !v);
    document.body.style.overflow = this.drawerOpen() ? 'hidden' : '';
  }

  protected closeDrawer(): void {
    this.drawerOpen.set(false);
    document.body.style.overflow = '';
  }

  protected signOut(): void {
    this.auth.signOut();
    this.accountOpen.set(false);
    this.toast.info('Signed out', 'Your demo data is still here if you sign back in.');
    this.router.navigateByUrl('/sign-in');
  }

  protected resetDemo(): void {
    this.store.reset();
    this.accountOpen.set(false);
    this.toast.success('Demo data reset', 'Every screen is back to its starting state.');
  }

  private canSee(item: NavItem): boolean {
    const need: Record<string, string[]> = {
      '/app/schedule': ['spa.schedule'],
      '/app/check-in': ['frontdesk'],
      '/app/treatments': ['provider'],
      '/app/messaging': ['messaging.admin'],
      '/app/inventory': ['inventory', 'housekeeping'],
      '/app/devices': ['device', 'device.assign'],
      '/app/staff': ['staff.read'],
      '/app/reconciliation': ['reconcile'],
      '/app/reports': ['reports'],
      '/app/integrations': ['config.propose'],
    };
    const scopes = need[item.path];
    return !scopes || this.auth.hasAny(scopes);
  }

  private labelFor(url: string): string {
    const clean = url.split('?')[0].split('#')[0];
    if (clean === '/app/forbidden') return 'No access';
    for (const g of WORKSPACE_NAV) {
      for (const i of g.items) if (i.path === clean) return i.label;
    }
    return 'Workspace';
  }

  private readCollapsed(): boolean {
    try { return localStorage.getItem('spms-rail') === '1'; } catch { return false; }
  }
}
