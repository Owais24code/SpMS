import { Routes } from '@angular/router';
import { authGuard, scopeGuard } from './core/guards/auth.guard';
import { SCOPES } from './core/models/contract';

/** Public marketing surface. */
const siteRoutes: Routes = [
  { path: '',         title: 'AARFID SpMS — Spa Management System', loadComponent: () => import('./features/site/home/home').then((m) => m.Home) },
  { path: 'platform', title: 'Platform — AARFID SpMS',              loadComponent: () => import('./features/site/platform/platform').then((m) => m.Platform) },
  { path: 'modules',  title: 'Modules — AARFID SpMS',               loadComponent: () => import('./features/site/modules/modules').then((m) => m.Modules) },
  { path: 'faq',      title: 'FAQ — AARFID SpMS',                   loadComponent: () => import('./features/site/faq/faq').then((m) => m.Faq) },
  { path: 'contact',  title: 'Contact — AARFID SpMS',               loadComponent: () => import('./features/site/contact/contact').then((m) => m.Contact) },
];

/**
 * Signed-in workspace.
 *
 * scopeGuard is used only where a role has no business on the screen at all.
 * Screens that merely mask a region — Staff, Reports — stay reachable and
 * render a denied panel in place of the restricted part, because the spec
 * distinguishes "you may not see this value" from "this does not exist".
 */
const workspaceRoutes: Routes = [
  { path: '',               title: 'Dashboard — SpMS',      loadComponent: () => import('./features/dashboard/dashboard').then((m) => m.Dashboard) },
  { path: 'schedule',       title: 'Schedule — SpMS',       canActivate: [scopeGuard(SCOPES.schedule)],  loadComponent: () => import('./features/schedule/schedule').then((m) => m.Schedule) },
  { path: 'check-in',       title: 'Check-in — SpMS',       canActivate: [scopeGuard(SCOPES.guestWrite)],     loadComponent: () => import('./features/check-in/check-in').then((m) => m.CheckIn) },
  { path: 'treatments',     title: 'Treatments — SpMS',     canActivate: [scopeGuard(SCOPES.healthRestricted)],      loadComponent: () => import('./features/treatments/treatments').then((m) => m.Treatments) },
  { path: 'booking',        title: 'Booking — SpMS',        loadComponent: () => import('./features/booking/booking').then((m) => m.Booking) },
  { path: 'appointments',   title: 'Appointments — SpMS',   loadComponent: () => import('./features/appointments/appointments').then((m) => m.Appointments) },
  { path: 'messaging',      title: 'Messaging — SpMS',      canActivate: [scopeGuard(SCOPES.messaging)], loadComponent: () => import('./features/messaging/messaging').then((m) => m.Messaging) },
  { path: 'inventory',      title: 'Inventory — SpMS',      canActivate: [scopeGuard(SCOPES.inventory)], loadComponent: () => import('./features/inventory/inventory').then((m) => m.Inventory) },
  { path: 'devices',        title: 'Devices — SpMS',        canActivate: [scopeGuard(SCOPES.device)],   loadComponent: () => import('./features/devices/devices').then((m) => m.Devices) },
  { path: 'staff',          title: 'Staff — SpMS',          canActivate: [scopeGuard(SCOPES.workforceRead)],    loadComponent: () => import('./features/staff/staff').then((m) => m.Staff) },
  { path: 'reconciliation', title: 'Reconciliation — SpMS', canActivate: [scopeGuard(SCOPES.reconcile)],     loadComponent: () => import('./features/reconciliation/reconciliation').then((m) => m.Reconciliation) },
  { path: 'reports',        title: 'Reports — SpMS',        canActivate: [scopeGuard(SCOPES.read)],       loadComponent: () => import('./features/reports/reports').then((m) => m.Reports) },
  { path: 'integrations',   title: 'Integrations — SpMS',   canActivate: [scopeGuard(SCOPES.admin)], loadComponent: () => import('./features/integrations/integrations').then((m) => m.Integrations) },
  { path: 'settings',       title: 'Settings — SpMS',       loadComponent: () => import('./features/settings/settings').then((m) => m.Settings) },
  { path: 'forbidden',      title: 'No access — SpMS',      loadComponent: () => import('./features/auth/forbidden/forbidden').then((m) => m.Forbidden) },
];

export const routes: Routes = [
  {
    path: 'sign-in',
    title: 'Sign in — AARFID SpMS',
    loadComponent: () => import('./features/auth/sign-in/sign-in').then((m) => m.SignIn),
  },
  {
    path: 'app',
    canActivate: [authGuard],
    loadComponent: () => import('./layouts/app-layout/app-layout').then((m) => m.AppLayout),
    children: workspaceRoutes,
  },
  {
    path: '',
    loadComponent: () => import('./layouts/site-layout/site-layout').then((m) => m.SiteLayout),
    children: siteRoutes,
  },
  {
    path: '**',
    title: 'Page not found — AARFID SpMS',
    loadComponent: () => import('./layouts/site-layout/site-layout').then((m) => m.SiteLayout),
    children: [
      { path: '', loadComponent: () => import('./features/not-found/not-found').then((m) => m.NotFound) },
    ],
  },
];
