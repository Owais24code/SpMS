import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient, HttpContext, HttpContextToken, HttpHeaders } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import { SCOPES, type RoleCode, type Scope } from '../models/contract';
import type { EntraClient } from '../auth/entra-client';

export interface PropertyRef {
  readonly propertyId: string;
  readonly code: string;
  readonly name: string;
  readonly timezone: string;
  readonly operatingMode?: string;
}

export interface Principal {
  readonly principalId: string;
  /** The operator's staff record (providers list their own appointments by it). */
  readonly staffId: string | null;
  readonly tenantId: string;
  readonly roleCode: RoleCode;
  readonly roles: readonly string[];
  readonly name: string;
  readonly initials: string;
  /** The current property's display name, or "All properties". */
  readonly property: string;
  readonly propertyId: string | null;
  readonly properties: readonly PropertyRef[];
  readonly roleLabel: string;
  readonly scopes: readonly Scope[];
  readonly source: 'demo' | 'entra' | 'offline';
}

/** Requests that must not be decorated with the signed-in operator's identity. */
export const SKIP_AUTH = new HttpContextToken<boolean>(() => false);

export const ROLE_LABELS: Record<string, string> = {
  guest: 'Guest', provider: 'Provider', front_desk: 'Front desk', spa_manager: 'Spa manager',
  hr_compliance: 'HR & compliance', finance: 'Finance', platform_admin: 'Platform admin',
  configuration_approver: 'Configuration approver', scheduler: 'Scheduler', housekeeping: 'Housekeeping',
  inventory_manager: 'Inventory manager', marketing: 'Marketing', support: 'Support',
  operations_analyst: 'Operations analyst', executive: 'Executive', release_manager: 'Release manager',
  security_admin: 'Security admin', integration_service: 'Integration service',
};

/** Which role names the operator when they hold several. */
const ROLE_ORDER: readonly string[] = [
  'spa_manager', 'front_desk', 'scheduler', 'provider', 'finance', 'inventory_manager', 'hr_compliance',
  'housekeeping', 'configuration_approver', 'platform_admin', 'security_admin', 'operations_analyst',
  'executive', 'marketing', 'support', 'release_manager', 'integration_service', 'guest',
];

/**
 * The seeded dev logins (database/seed/dev.sql). In `demo` mode the sign-in
 * screen offers these and the API resolves the handle; the scopes listed are
 * only the offline stand-in and the picker's preview — the server's answer
 * from GET /me is what the workspace runs on.
 */
export const ROLE_PRESETS = {
  spa_manager: {
    login: 'morgan', roleCode: 'spa_manager', name: 'Morgan', property: 'All properties', roleLabel: 'Spa manager',
    scopes: [SCOPES.read, SCOPES.write, SCOPES.schedule, SCOPES.guestWrite,
             SCOPES.workforceRead, SCOPES.messaging, SCOPES.device, SCOPES.inventory, SCOPES.commerce, SCOPES.admin],
  },
  front_desk: {
    login: 'dana', roleCode: 'front_desk', name: 'Dana', property: 'Riverside Spa', roleLabel: 'Front desk',
    scopes: [SCOPES.read, SCOPES.write, SCOPES.guestWrite, SCOPES.device, SCOPES.commerce],
  },
  scheduler: {
    login: 'riley', roleCode: 'scheduler', name: 'Riley', property: 'Riverside Spa', roleLabel: 'Scheduler',
    scopes: [SCOPES.read, SCOPES.write, SCOPES.schedule],
  },
  provider: {
    login: 'lena', roleCode: 'provider', name: 'Lena', property: 'Riverside Spa', roleLabel: 'Provider',
    scopes: [SCOPES.read, SCOPES.healthRestricted, SCOPES.commerce],
  },
  finance: {
    login: 'sam', roleCode: 'finance', name: 'Sam', property: 'All properties', roleLabel: 'Finance',
    scopes: [SCOPES.read, SCOPES.commerce, SCOPES.reconcile],
  },
  housekeeping: {
    login: 'hana', roleCode: 'housekeeping', name: 'Hana', property: 'Riverside Spa', roleLabel: 'Housekeeping',
    scopes: [SCOPES.read, SCOPES.inventory],
  },
  platform_admin: {
    login: 'ada', roleCode: 'platform_admin', name: 'Ada', property: 'All properties', roleLabel: 'Platform admin',
    scopes: [SCOPES.read, SCOPES.admin],
  },
} as const;

export type RoleKey = keyof typeof ROLE_PRESETS;

interface MeDto {
  readonly principalId: string;
  readonly staffId?: string | null;
  readonly displayName: string;
  readonly actorType: string;
  readonly tenantId: string;
  readonly propertyId: string | null;
  readonly roles: readonly string[];
  readonly scopes: readonly string[];
  readonly properties: readonly PropertyRef[];
}

interface Stored {
  readonly source: 'demo' | 'entra' | 'offline';
  readonly login?: string;
  readonly role?: RoleKey;
  readonly propertyId?: string | null;
}

const KEY = 'spms-session';
const EMPTY = '00000000-0000-0000-0000-000000000000';

/** "Dana (front desk)" → "Dana": the seed's role hint is for the database, not the header. */
const personOf = (name: string): string => name.replace(/\s*\([^)]*\)\s*/g, ' ').trim() || name;

const initialsOf = (name: string): string =>
  personOf(name).split(/\s+/).filter(Boolean).slice(0, 2).map((w) => w[0]!.toUpperCase()).join('') || '?';

/**
 * Who the operator is, as the API sees them.
 *
 * Nothing here is an authorization decision. Scopes and roles come from GET
 * /me — the server's resolution of the token (or dev login) — and are used
 * only to hide screens the operator could not use anyway. The API checks the
 * scope, the relationship (OpenFGA) and the purpose on every call.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly _user = signal<Principal | null>(null);
  private readonly _busy = signal(false);
  private readonly _error = signal<string | null>(null);
  private stored: Stored | null = this.read();
  private entra: EntraClient | null = null;

  readonly mode = environment.authMode;
  readonly user = this._user.asReadonly();
  readonly busy = this._busy.asReadonly();
  readonly error = this._error.asReadonly();
  readonly isSignedIn = computed(() => this._user() !== null);

  /**
   * Runs before the first navigation (provideAppInitializer). Finishes an
   * Entra redirect, or re-reads /me for a remembered demo login, so a reload
   * lands back in the workspace instead of on the sign-in screen.
   *
   * Never throws: an API that is down leaves the operator signed out with a
   * reason on the sign-in screen.
   */
  async restore(): Promise<string | null> {
    try {
      if (this.mode === 'entra') {
        const client = await this.entraClient();
        const { account, returnUrl } = await client.complete();
        if (!account) return null;
        await this.loadMe('entra', this.stored?.propertyId ?? null);
        return returnUrl;
      }
      const s = this.stored;
      if (s?.source === 'offline' && s.role && s.role in ROLE_PRESETS) {
        this._user.set(this.fromPreset(s.role));
      } else if (s?.source === 'demo' && s.login) {
        await this.loadMe('demo', s.propertyId ?? null, s.login);
      }
    } catch (err) {
      this._user.set(null);
      this._error.set(this.describe(err));
    }
    return null;
  }

  /** Demo and offline: sign in as a seeded dev login. */
  async signIn(role: RoleKey): Promise<boolean> {
    this._error.set(null);
    if (this.mode === 'offline') {
      this._user.set(this.fromPreset(role));
      this.write({ source: 'offline', role });
      return true;
    }
    this._busy.set(true);
    try {
      await this.loadMe('demo', null, ROLE_PRESETS[role].login);
      return true;
    } catch (err) {
      this._error.set(this.describe(err));
      return false;
    } finally {
      this._busy.set(false);
    }
  }

  /** Entra: leaves the page for Microsoft sign-in and comes back through restore(). */
  async signInWithEntra(returnUrl: string): Promise<void> {
    this._error.set(null);
    this._busy.set(true);
    try {
      await (await this.entraClient()).signIn(returnUrl);
    } catch (err) {
      this._busy.set(false);
      this._error.set(this.describe(err));
    }
  }

  signOut(): void {
    const wasEntra = this._user()?.source === 'entra';
    this._user.set(null);
    this.stored = null;
    try { localStorage.removeItem(KEY); } catch { /* ignore */ }
    if (wasEntra && this.entra) void this.entra.signOut();
  }

  /**
   * Moves the workspace to another of the operator's properties. The API is
   * asked again rather than the list filtered locally, because roles — and so
   * scopes — are granted per property.
   */
  async switchProperty(propertyId: string): Promise<boolean> {
    const u = this._user();
    if (!u || u.propertyId === propertyId) return false;
    if (u.source === 'offline') return false;
    this._busy.set(true);
    try {
      await this.loadMe(u.source, propertyId, this.stored?.login);
      return true;
    } catch (err) {
      this._error.set(this.describe(err));
      return false;
    } finally {
      this._busy.set(false);
    }
  }

  /** Identity headers for one API request. Async because an Entra token may need refreshing. */
  async requestHeaders(): Promise<Record<string, string>> {
    const u = this._user();
    if (!u) return {};
    const property: Record<string, string> = u.propertyId ? { 'X-Spa-Property': u.propertyId } : {};
    switch (u.source) {
      case 'entra':
        return { Authorization: `Bearer ${await (await this.entraClient()).accessToken()}`, ...property };
      case 'demo':
        return { 'X-Spa-Login': this.stored?.login ?? '', ...property };
      default:
        // Offline never reaches an API; asserted scopes keep a stray call honest.
        return { 'X-Spa-Scopes': u.scopes.join(' ') };
    }
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

  private async loadMe(source: 'demo' | 'entra', propertyId: string | null, login?: string): Promise<void> {
    let headers = new HttpHeaders();
    if (source === 'demo') headers = headers.set('X-Spa-Login', login ?? '');
    else headers = headers.set('Authorization', `Bearer ${await (await this.entraClient()).accessToken()}`);
    if (propertyId) headers = headers.set('X-Spa-Property', propertyId);

    const me = await firstValueFrom(this.http.get<MeDto>(`${environment.apiBaseUrl}/me`, {
      headers, context: new HttpContext().set(SKIP_AUTH, true),
    }));

    const roles = [...me.roles];
    const primary = ROLE_ORDER.find((r) => roles.includes(r)) ?? roles[0] ?? 'guest';
    const current = me.properties.find((p) => p.propertyId === me.propertyId) ?? null;
    const user: Principal = {
      principalId: me.principalId,
      staffId: me.staffId ?? null,
      tenantId: me.tenantId,
      roleCode: primary as RoleCode,
      roles,
      name: personOf(me.displayName),
      initials: initialsOf(me.displayName),
      property: current?.name ?? (me.properties.length > 1 ? 'All properties' : me.properties[0]?.name ?? '—'),
      propertyId: me.propertyId && me.propertyId !== EMPTY ? me.propertyId : null,
      properties: me.properties,
      roleLabel: ROLE_LABELS[primary] ?? primary,
      scopes: me.scopes as Scope[],
      source,
    };
    this._user.set(user);
    this.write({ source, login, propertyId: user.propertyId });
  }

  private fromPreset(role: RoleKey): Principal {
    const p = ROLE_PRESETS[role];
    return {
      principalId: EMPTY, staffId: null, tenantId: EMPTY, roleCode: p.roleCode, roles: [p.roleCode],
      name: p.name, initials: initialsOf(p.name), property: p.property, propertyId: null, properties: [],
      roleLabel: p.roleLabel, scopes: p.scopes, source: 'offline',
    };
  }

  private async entraClient(): Promise<EntraClient> {
    if (this.entra) return this.entra;
    if (!environment.entra) throw new Error('Entra sign-in is not configured for this deployment.');
    const { EntraClient } = await import('../auth/entra-client');
    this.entra = await EntraClient.create(environment.entra);
    return this.entra;
  }

  private describe(err: unknown): string {
    const e = err as { status?: number; detail?: string; title?: string; message?: string };
    if (e?.status === 0) return 'The SpMS API is not reachable. Is it running?';
    if (e?.status === 401) return e.detail ?? 'The API did not accept this sign-in.';
    if (e?.status === 403) return e.detail ?? 'This account has no access to SpMS.';
    return e?.detail ?? e?.title ?? e?.message ?? 'Sign-in failed.';
  }

  private write(s: Stored): void {
    this.stored = s;
    try { localStorage.setItem(KEY, JSON.stringify(s)); } catch { /* session-only */ }
  }

  private read(): Stored | null {
    try {
      const raw = localStorage.getItem(KEY);
      if (!raw) return null;
      const s = JSON.parse(raw) as Stored;
      return s && typeof s === 'object' && typeof s.source === 'string' ? s : null;
    } catch {
      return null;
    }
  }
}
