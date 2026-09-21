import { Injectable, signal, computed } from '@angular/core';
import { SCOPES, type RoleCode, type Scope } from '../models/contract';

export interface Principal {
  readonly roleCode: RoleCode;
  readonly name: string;
  readonly initials: string;
  readonly property: string;
  readonly roleLabel: string;
  readonly scopes: readonly Scope[];
}

/**
 * Demo principals mapped onto the real role codes and OAuth scopes from
 * role_permissions.json. Replace the presets with claims from the identity
 * provider — the scope strings are already the production ones, so nothing
 * downstream changes when real tokens arrive.
 *
 * defaultEffect is deny: a scope absent from this list is denied.
 */
export const ROLE_PRESETS = {
  spa_manager: {
    roleCode: 'spa_manager',
    name: 'Owais Khan', initials: 'OK', property: 'Riverside Spa', roleLabel: 'Spa manager',
    scopes: [SCOPES.read, SCOPES.write, SCOPES.schedule, SCOPES.guestWrite,
             SCOPES.workforceRead, SCOPES.messaging, SCOPES.device, SCOPES.inventory],
  },
  front_desk: {
    roleCode: 'front_desk',
    name: 'Dana Reyes', initials: 'DR', property: 'Riverside Spa', roleLabel: 'Front desk',
    scopes: [SCOPES.read, SCOPES.write, SCOPES.guestWrite, SCOPES.device, SCOPES.commerce],
  },
  provider: {
    roleCode: 'provider',
    name: 'Lena Kovač', initials: 'LK', property: 'Riverside Spa', roleLabel: 'Provider',
    scopes: [SCOPES.read, SCOPES.healthRestricted, SCOPES.commerce],
  },
  finance: {
    roleCode: 'finance',
    name: 'Sam Okafor', initials: 'SO', property: 'All properties', roleLabel: 'Finance',
    scopes: [SCOPES.read, SCOPES.commerce, SCOPES.reconcile],
  },
  platform_admin: {
    roleCode: 'platform_admin',
    name: 'Ada Osei', initials: 'AO', property: 'All properties', roleLabel: 'Platform admin',
    scopes: [SCOPES.read, SCOPES.admin],
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
    this._user.set(ROLE_PRESETS[role]);
    try { localStorage.setItem(KEY, role); } catch { /* session-only */ }
  }

  signOut(): void {
    this._user.set(null);
    try { localStorage.removeItem(KEY); } catch { /* ignore */ }
  }

  /**
   * Scope check only.
   *
   * Passing this is necessary but never sufficient: the API also checks the
   * caller's relationship to the record and the declared purpose, and filters
   * restricted fields at serialization. Never treat a true here as permission
   * to display a restricted value the server has not sent.
   */
  has(scope: Scope | string): boolean {
    return this._user()?.scopes.includes(scope as Scope) ?? false;
  }

  hasAny(scopes: readonly (Scope | string)[]): boolean {
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
