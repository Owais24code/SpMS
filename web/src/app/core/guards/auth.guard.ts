import { inject } from '@angular/core';
import { CanActivateFn, Router, createUrlTreeFromSnapshot } from '@angular/router';
import { AuthService } from '../services/auth.service';

/** Sends a signed-out visitor to sign-in, remembering where they were going. */
export const authGuard: CanActivateFn = (route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (auth.isSignedIn()) return true;

  return router.createUrlTree(['/sign-in'], {
    queryParams: { returnUrl: state.url },
  });
};

/**
 * Blocks a screen the caller's role cannot see at all.
 *
 * Screens that merely mask some fields are NOT guarded — they render with a
 * denied panel in place of the restricted region, which is what the spec
 * asks for: you may see that a thing exists without seeing its contents.
 */
export function scopeGuard(...scopes: string[]): CanActivateFn {
  return (route) => {
    const auth = inject(AuthService);
    const router = inject(Router);
    if (auth.hasAny(scopes)) return true;
    return router.createUrlTree(['/app/forbidden'], {
      queryParams: { need: scopes.join(','), from: route.routeConfig?.path ?? '' },
    });
  };
}
