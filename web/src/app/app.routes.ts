import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    title: 'AARFID SpMS — Spa Management System',
    loadComponent: () => import('./pages/home/home').then((m) => m.Home),
  },
  {
    path: 'contact',
    title: 'Contact — AARFID SpMS',
    loadComponent: () => import('./pages/contact/contact').then((m) => m.Contact),
  },
  // Placeholder routes so the navigation is never dead. Each gets its own
  // page as the marketing surface grows.
  {
    path: 'platform',
    title: 'Platform — AARFID SpMS',
    loadComponent: () => import('./pages/home/home').then((m) => m.Home),
  },
  {
    path: 'modules',
    title: 'Modules — AARFID SpMS',
    loadComponent: () => import('./pages/home/home').then((m) => m.Home),
  },
  {
    path: 'faq',
    title: 'FAQ — AARFID SpMS',
    loadComponent: () => import('./pages/home/home').then((m) => m.Home),
  },
  {
    path: '**',
    title: 'Page not found — AARFID SpMS',
    loadComponent: () => import('./pages/not-found/not-found').then((m) => m.NotFound),
  },
];
