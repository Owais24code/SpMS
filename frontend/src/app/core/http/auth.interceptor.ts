import { inject } from '@angular/core';
import type { HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { environment } from '../../../environments/environment';
import { AuthService } from '../services/auth.service';

/**
 * Attaches the caller's identity.
 *
 * Today that is the API's development header set, which it only trusts in its
 * own Development environment; outside it every scoped endpoint answers 401
 * until Entra ID lands. When it does, `bearer()` below replaces `devHeaders()`
 * in ONE place and nothing else in the client changes — that is the only
 * reason this is a separate interceptor rather than two lines in the client.
 *
 * Scopes come from the signed-in principal rather than a constant, so the role
 * switcher in the UI produces the 403s a real token would. The server discards
 * unknown scope strings, and defaultEffect is deny.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  // Health probes are unauthenticated by design, and sending identity to them
  // would make a liveness check fail differently from how the probe will.
  if (isHealthProbe(req)) return next(req);

  const auth = inject(AuthService);
  return next(req.clone({ setHeaders: devHeaders(auth) }));
};

const isHealthProbe = (req: HttpRequest<unknown>): boolean =>
  /\/health(\/live|\/ready)?$/.test(new URL(req.url, 'http://local').pathname);

/**
 * Development identity. Space-separated scopes, exactly as the server splits
 * them.
 */
const devHeaders = (auth: AuthService): Record<string, string> => {
  const user = auth.user();
  const id = environment.devIdentity;
  return {
    'X-Spa-Scopes': (user?.scopes ?? []).join(' '),
    'X-Spa-Tenant': id.tenant,
    'X-Spa-Property': id.property,
    // The actor lands verbatim in the audit trail, so a signed-in operator's
    // own name has to win over the configured default.
    'X-Spa-Actor': user?.name ?? id.actor,
  };
};

/**
 * Production identity, for when the token source exists.
 *
 * Kept next to the header form on purpose: the swap is this function's name in
 * `authInterceptor` above, and a reviewer can see both shapes at once.
 */
export const bearer = (accessToken: string): Record<string, string> => ({
  Authorization: `Bearer ${accessToken}`,
});
