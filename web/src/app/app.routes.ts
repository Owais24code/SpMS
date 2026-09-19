import { Routes } from '@angular/router';

/** Public marketing surface. */
const siteRoutes: Routes = [
  { path: '',         title: 'AARFID SpMS — Spa Management System', loadComponent: () => import('./features/site/home/home').then((m) => m.Home) },
  { path: 'platform', title: 'Platform — AARFID SpMS',              loadComponent: () => import('./features/site/home/home').then((m) => m.Home) },
  { path: 'modules',  title: 'Modules — AARFID SpMS',               loadComponent: () => import('./features/site/home/home').then((m) => m.Home) },
  { path: 'faq',      title: 'FAQ — AARFID SpMS',                   loadComponent: () => import('./features/site/home/home').then((m) => m.Home) },
  { path: 'contact',  title: 'Contact — AARFID SpMS',               loadComponent: () => import('./features/site/contact/contact').then((m) => m.Contact) },
];

/** Signed-in workspace. */
const workspaceRoutes: Routes = [
  { path: '',               title: 'Dashboard — SpMS',      loadComponent: () => import('./features/dashboard/dashboard').then((m) => m.Dashboard) },
  { path: 'schedule',       title: 'Schedule — SpMS',       loadComponent: () => import('./features/schedule/schedule').then((m) => m.Schedule) },
  { path: 'check-in',       title: 'Check-in — SpMS',       loadComponent: () => import('./features/check-in/check-in').then((m) => m.CheckIn) },
  { path: 'treatments',     title: 'Treatments — SpMS',     loadComponent: () => import('./features/treatments/treatments').then((m) => m.Treatments) },
  { path: 'booking',        title: 'Booking — SpMS',        loadComponent: () => import('./features/booking/booking').then((m) => m.Booking) },
  { path: 'appointments',   title: 'Appointments — SpMS',   loadComponent: () => import('./features/appointments/appointments').then((m) => m.Appointments) },
  { path: 'messaging',      title: 'Messaging — SpMS',      loadComponent: () => import('./features/messaging/messaging').then((m) => m.Messaging) },
  { path: 'inventory',      title: 'Inventory — SpMS',      loadComponent: () => import('./features/inventory/inventory').then((m) => m.Inventory) },
  { path: 'devices',        title: 'Devices — SpMS',        loadComponent: () => import('./features/devices/devices').then((m) => m.Devices) },
  { path: 'staff',          title: 'Staff — SpMS',          loadComponent: () => import('./features/staff/staff').then((m) => m.Staff) },
  { path: 'reconciliation', title: 'Reconciliation — SpMS', loadComponent: () => import('./features/reconciliation/reconciliation').then((m) => m.Reconciliation) },
  { path: 'reports',        title: 'Reports — SpMS',        loadComponent: () => import('./features/reports/reports').then((m) => m.Reports) },
  { path: 'integrations',   title: 'Integrations — SpMS',   loadComponent: () => import('./features/integrations/integrations').then((m) => m.Integrations) },
  { path: 'settings',       title: 'Settings — SpMS',       loadComponent: () => import('./features/settings/settings').then((m) => m.Settings) },
];

export const routes: Routes = [
  {
    path: 'app',
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
