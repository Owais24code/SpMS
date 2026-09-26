import { beforeEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AuthService } from './auth.service';

const me = (over: Record<string, unknown> = {}) => ({
  principalId: 'p-1', displayName: 'Dana (front desk)', actorType: 'Staff', tenantId: 't-1',
  propertyId: 'riv', roles: ['front_desk'], scopes: ['spa.read', 'spa.write'],
  properties: [
    { propertyId: 'har', code: 'harbour', name: 'Harbour Spa', timezone: 'Europe/London' },
    { propertyId: 'riv', code: 'riverside', name: 'Riverside Spa', timezone: 'America/New_York' },
  ],
  ...over,
});

describe('AuthService (demo mode)', () => {
  let auth: AuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });

  it('signs in through GET /me with the dev login and runs on the server scopes', async () => {
    const done = auth.signIn('front_desk');
    const req = http.expectOne((r) => r.url.endsWith('/me'));
    expect(req.request.headers.get('X-Spa-Login')).toBe('dana');
    expect(req.request.headers.has('X-Spa-Scopes')).toBe(false);
    req.flush(me());
    expect(await done).toBe(true);

    const u = auth.user()!;
    expect(u.name).toBe('Dana');
    expect(u.initials).toBe('D');
    expect(u.roleLabel).toBe('Front desk');
    expect(u.property).toBe('Riverside Spa');
    expect(auth.has('spa.write')).toBe(true);
    // The preset lists spa.commerce; the server did not grant it, so it is not held.
    expect(auth.has('spa.commerce')).toBe(false);
  });

  it('sends the login and current property on API requests', async () => {
    const done = auth.signIn('front_desk');
    http.expectOne((r) => r.url.endsWith('/me')).flush(me());
    await done;
    expect(await auth.requestHeaders()).toEqual({ 'X-Spa-Login': 'dana', 'X-Spa-Property': 'riv' });
  });

  it('asks the server again when the property changes', async () => {
    const done = auth.signIn('spa_manager');
    http.expectOne((r) => r.url.endsWith('/me')).flush(me({ displayName: 'Morgan (spa manager)', roles: ['spa_manager'] }));
    await done;

    const switching = auth.switchProperty('har');
    const req = http.expectOne((r) => r.url.endsWith('/me'));
    expect(req.request.headers.get('X-Spa-Property')).toBe('har');
    req.flush(me({ displayName: 'Morgan (spa manager)', roles: ['spa_manager'], propertyId: 'har' }));
    expect(await switching).toBe(true);
    expect(auth.user()!.property).toBe('Harbour Spa');
  });

  it('reports an unreachable API instead of signing in', async () => {
    const done = auth.signIn('finance');
    http.expectOne((r) => r.url.endsWith('/me')).error(new ProgressEvent('error'), { status: 0 });
    expect(await done).toBe(false);
    expect(auth.isSignedIn()).toBe(false);
    expect(auth.error()).toContain('not reachable');
  });

  it('restores a remembered login by re-reading /me', async () => {
    const done = auth.signIn('finance');
    http.expectOne((r) => r.url.endsWith('/me')).flush(me({ roles: ['finance'], displayName: 'Sam (finance)' }));
    await done;

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const again = TestBed.inject(AuthService);
    const ctl = TestBed.inject(HttpTestingController);
    const restoring = again.restore();
    const req = ctl.expectOne((r) => r.url.endsWith('/me'));
    expect(req.request.headers.get('X-Spa-Login')).toBe('sam');
    req.flush(me({ roles: ['finance'], displayName: 'Sam (finance)' }));
    await restoring;
    expect(again.user()!.roleLabel).toBe('Finance');
  });

  it('signing out forgets the login', async () => {
    const done = auth.signIn('front_desk');
    http.expectOne((r) => r.url.endsWith('/me')).flush(me());
    await done;
    auth.signOut();
    expect(auth.isSignedIn()).toBe(false);
    expect(localStorage.getItem('spms-session')).toBeNull();
    expect(await auth.requestHeaders()).toEqual({});
  });
});
