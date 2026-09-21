import {
  ApplicationConfig,
  provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection,
} from '@angular/core';
import {
  provideRouter,
  withInMemoryScrolling,
  withRouterConfig,
} from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';

import { routes } from './app.routes';
import { authInterceptor } from './core/http/auth.interceptor';
import { correlationInterceptor } from './core/http/correlation.interceptor';
import { problemInterceptor } from './core/http/problem.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    // Order is load-bearing. Correlation and auth run outward-in so the
    // request they decorate is the one that goes on the wire; the problem
    // normaliser is LAST, closest to the backend, so it sees the final request
    // and can quote the correlation id it actually carried.
    provideHttpClient(
      withInterceptors([correlationInterceptor, authInterceptor, problemInterceptor]),
    ),
    provideRouter(
      routes,
      // Restore position on back/forward, jump to top on a new page, and
      // honour #fragment links from the nav.
      withInMemoryScrolling({
        scrollPositionRestoration: 'enabled',
        anchorScrolling: 'enabled',
      }),
      withRouterConfig({ onSameUrlNavigation: 'reload' }),
    ),
  ],
};
