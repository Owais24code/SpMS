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

export interface DevIdentity {
  /** Sent as X-Spa-Tenant while the API trusts development headers. */
  readonly tenant: string;
  /** Sent as X-Spa-Property. Drives the property's time zone and business day. */
  readonly property: string;
  /** Sent as X-Spa-Actor. Appears verbatim in the server's audit trail. */
  readonly actor: string;
}

export interface SpmsEnvironment {
  /** No trailing slash. The API client joins paths with a single separator. */
  readonly apiBaseUrl: string;
  /** False keeps the in-memory store, so screens with no endpoint still work. */
  readonly useRealApi: boolean;
  readonly devIdentity: DevIdentity;
}

/** Everything the deployment may override. Every field is optional. */
export interface SpmsRuntimeConfig {
  readonly apiBaseUrl?: unknown;
  readonly useRealApi?: unknown;
  readonly tenant?: unknown;
  readonly property?: unknown;
  readonly actor?: unknown;
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
export const resolveEnvironment = (compiled: SpmsEnvironment): SpmsEnvironment => {
  const c = config();
  return {
    apiBaseUrl: text(c.apiBaseUrl, compiled.apiBaseUrl).replace(/\/+$/, ''),
    useRealApi: flag(c.useRealApi, compiled.useRealApi),
    devIdentity: {
      tenant: text(c.tenant, compiled.devIdentity.tenant),
      property: text(c.property, compiled.devIdentity.property),
      actor: text(c.actor, compiled.devIdentity.actor),
    },
  };
};
