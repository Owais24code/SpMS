import { inject } from '@angular/core';
import type { HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { from, switchMap } from 'rxjs';
import { AuthService, SKIP_AUTH } from '../services/auth.service';
import { GuestSession } from '../services/guest-session.service';

/**
 * Attaches the caller's identity, in one place:
 *
 *   entra    Authorization: Bearer <Entra access token> + X-Spa-Property
 *   demo     X-Spa-Login: <seeded dev login> + X-Spa-Property (API Development only)
 *   guest    Authorization: Bearer <guest session> for /guest/* calls
 *
 * The API decides everything else — roles, properties, scopes — from what
 * this proves; nothing the client claims about itself is trusted.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  // Health probes are unauthenticated by design, and sending identity to them
  // would make a liveness check fail differently from how the probe will.
  if (isHealthProbe(req) || req.context.get(SKIP_AUTH) || req.headers.has('Authorization')) return next(req);

  if (isGuestCall(req)) {
    const token = inject(GuestSession).token();
    return next(token ? req.clone({ setHeaders: bearer(token) }) : req);
  }

  const auth = inject(AuthService);
  return from(auth.requestHeaders()).pipe(
    switchMap((headers) => next(req.clone({ setHeaders: headers }))),
  );
};

const pathOf = (req: HttpRequest<unknown>): string => new URL(req.url, 'http://local').pathname;

const isHealthProbe = (req: HttpRequest<unknown>): boolean => /\/health(\/live|\/ready)?$/.test(pathOf(req));

/** The guest web's own endpoints. /guests/* (plural) is staff-facing and keeps the operator's identity. */
const isGuestCall = (req: HttpRequest<unknown>): boolean => /\/guest(\/|$)/.test(pathOf(req));

export const bearer = (accessToken: string): Record<string, string> => ({
  Authorization: `Bearer ${accessToken}`,
});
