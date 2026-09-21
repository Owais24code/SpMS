import { Injectable, signal, computed } from '@angular/core';

export interface Principal {
  readonly name: string;
  readonly initials: string;
  readonly property: string;
  readonly roleLabel: string;
  readonly scopes: readonly string[];
}

/** Demo roles. Replace with claims from the real identity provider. */
export const ROLE_PRESETS = {
  manager: {
    name: 'Owais Khan', initials: 'OK', property: 'Riverside Spa', roleLabel: 'Spa manager',
    scopes: ['spa.read','spa.schedule','spa.override','frontdesk','provider','inventory',
             'housekeeping','device','device.assign','device.alert','device.return',
             'staff.read','messaging.admin','reports','reports.operational','config.propose'],
  },
  frontDesk: {
    name: 'Dana Reyes', initials: 'DR', property: 'Riverside Spa', roleLabel: 'Front desk',
    scopes: ['spa.read','spa.schedule','frontdesk','device.assign','device.return'],
  },
  finance: {
    name: 'Sam Okafor', initials: 'SO', property: 'All properties', roleLabel: 'Finance',
    scopes: ['spa.read','reconcile','reconcile.replay','reconcile.resolve','finance.restricted',
             'reports','reports.operational','reports.financial','reports.export'],
  },
} as const satisfies Record<string, Principal>;

export type RoleKey = keyof typeof ROLE_PRESETS;

const KEY = 'spms-session';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly _user = signal<Principal | null>(this.restore());

  readonly user = this._user.asReadonly();
  readonly isSignedIn = computed(() => this._user() !== null);

  signIn(role: RoleKey): void {
    const p = ROLE_PRESETS[role];
    this._user.set(p);
    try { localStorage.setItem(KEY, role); } catch { /* session-only */ }
  }

  signOut(): void {
    this._user.set(null);
    try { localStorage.removeItem(KEY); } catch { /* ignore */ }
  }

  /** Scope check only. Relationship and purpose are enforced server-side. */
  has(scope: string): boolean {
    return this._user()?.scopes.includes(scope) ?? false;
  }

  hasAny(scopes: readonly string[]): boolean {
    return scopes.some((s) => this.has(s));
  }

  private restore(): Principal | null {
    try {
      const k = localStorage.getItem(KEY) as RoleKey | null;
      return k && k in ROLE_PRESETS ? ROLE_PRESETS[k] : null;
    } catch {
      return null;
    }
  }
}
