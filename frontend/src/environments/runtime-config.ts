/**
 * Deploy-time configuration.
 *
 * One built bundle is uploaded to blob storage for several environments, so the
 * API base URL cannot be a compile-time constant: baking it in would mean a
 * separate build per environment and a rebuild to repoint one of them.
 *
 * `assets/config.js` is a plain script the deployment overwrites. It runs
 * before the bundle, sets `window.__SPMS_CONFIG__`, and whatever it supplies
 * wins over the compiled value. A missing or malformed file is not fatal — the
 * compiled value is a working fallback, because a config file that failed to
 * upload must not present as a blank application.
 */

/**
 * How the workspace proves who the operator is.
 *
 *   demo     The API's Development-only dev logins: the sign-in screen picks a
 *            seeded login handle (X-Spa-Login) and the server resolves it
 *            exactly as it resolves an Entra token — same principal, roles,
 *            properties and scopes. Refused by the API outside Development.
 *   entra    Microsoft Entra ID through MSAL (authorization code + PKCE). The
 *            access token goes to the API as a bearer token.
 *   offline  No API at all (useRealApi false): the role presets stand in.
 */
export type AuthMode = 'demo' | 'entra' | 'offline';

export interface EntraConfig {
  readonly clientId: string;
  /** https://login.microsoftonline.com/<tenant-id> */
  readonly authority: string;
  /** The API's delegated scope(s), e.g. api://spms-api/access_as_user. */
  readonly apiScopes: readonly string[];
  /** Defaults to the page origin. Must be registered on the app. */
  readonly redirectUri?: string;
}

/** Where the guest web asks for a sign-in link. Codes, not ids: they appear in a URL a guest may type. */
export interface GuestSite {
  readonly tenant: string;
  readonly property: string;
}

export interface SpmsEnvironment {
  /** No trailing slash. The API client joins paths with a single separator. */
  readonly apiBaseUrl: string;
  /** False keeps the in-memory store, so screens with no endpoint still work. */
  readonly useRealApi: boolean;
  readonly authMode: AuthMode;
  readonly entra: EntraConfig | null;
  readonly guestSite: GuestSite;
}

/** Everything the deployment may override. Every field is optional. */
export interface SpmsRuntimeConfig {
  readonly apiBaseUrl?: unknown;
  readonly useRealApi?: unknown;
  readonly authMode?: unknown;
  readonly entra?: {
    readonly clientId?: unknown;
    readonly authority?: unknown;
    readonly apiScopes?: unknown;
    readonly redirectUri?: unknown;
  };
  readonly guestTenant?: unknown;
  readonly guestProperty?: unknown;
}

declare global {
  interface Window {
    __SPMS_CONFIG__?: SpmsRuntimeConfig;
  }
}

const config = (): SpmsRuntimeConfig => {
  // Guarded rather than assumed: this module is also evaluated by tooling that
  // has no window, and a thrown TypeError there takes the whole bundle down.
  try {
    return typeof window === 'undefined' ? {} : (window.__SPMS_CONFIG__ ?? {});
  } catch {
    return {};
  }
};

const text = (value: unknown, fallback: string): string =>
  typeof value === 'string' && value.trim().length > 0 ? value.trim() : fallback;

const flag = (value: unknown, fallback: boolean): boolean =>
  typeof value === 'boolean' ? value : fallback;

/**
 * Overlays the runtime file onto the compiled defaults.
 *
 * The trailing slash is stripped here rather than at each call site, because a
 * deployment that writes `https://api.example.com/` would otherwise produce
 * `//appointments` on every request.
 */
const mode = (value: unknown, fallback: AuthMode): AuthMode =>
  value === 'demo' || value === 'entra' || value === 'offline' ? value : fallback;

const words = (value: unknown): readonly string[] | null =>
  Array.isArray(value) && value.every((v) => typeof v === 'string' && v.trim().length > 0)
    ? value.map((v: string) => v.trim())
    : typeof value === 'string' && value.trim().length > 0 ? value.trim().split(/\s+/) : null;

const entraFrom = (c: SpmsRuntimeConfig['entra'], compiled: EntraConfig | null): EntraConfig | null => {
  const clientId = text(c?.clientId, compiled?.clientId ?? '');
  const authority = text(c?.authority, compiled?.authority ?? '');
  const apiScopes = words(c?.apiScopes) ?? compiled?.apiScopes ?? [];
  if (!clientId || !authority || apiScopes.length === 0) return null;
  const redirectUri = text(c?.redirectUri, compiled?.redirectUri ?? '');
  return { clientId, authority: authority.replace(/\/+$/, ''), apiScopes, ...(redirectUri ? { redirectUri } : {}) };
};

/**
 * Overlays the runtime file onto the compiled defaults.
 *
 * The trailing slash is stripped here rather than at each call site, because a
 * deployment that writes `https://api.example.com/` would otherwise produce
 * `//appointments` on every request.
 *
 * The auth mode degrades rather than lies: `entra` without a complete Entra
 * block cannot sign anyone in, so it falls back to `demo` (which the API only
 * honours in Development), and no API at all means `offline`.
 */
export const resolveEnvironment = (compiled: SpmsEnvironment): SpmsEnvironment => {
  const c = config();
  const useRealApi = flag(c.useRealApi, compiled.useRealApi);
  const entra = entraFrom(c.entra, compiled.entra);
  let authMode = mode(c.authMode, compiled.authMode);
  if (!useRealApi) authMode = 'offline';
  else if (authMode === 'entra' && entra === null) authMode = 'demo';
  else if (authMode === 'offline') authMode = 'demo';
  return {
    apiBaseUrl: text(c.apiBaseUrl, compiled.apiBaseUrl).replace(/\/+$/, ''),
    useRealApi,
    authMode,
    entra,
    guestSite: {
      tenant: text(c.guestTenant, compiled.guestSite.tenant),
      property: text(c.guestProperty, compiled.guestSite.property),
    },
  };
};
