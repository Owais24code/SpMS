import { resolveEnvironment, type SpmsEnvironment } from './runtime-config';

/**
 * Production defaults.
 *
 * `apiBaseUrl` is a same-origin relative path so that a deployment which never
 * writes `assets/config.js` still reaches an API behind the same front door,
 * rather than pointing at a hostname that only existed on the build agent.
 *
 * `entra` is supplied by assets/config.js at deploy time (client id,
 * authority, API scope). Until it is, the mode degrades to `demo`, which the
 * API refuses outside its Development environment — a misconfigured
 * deployment shows a sign-in error, never somebody else's workspace.
 */
export const environment: SpmsEnvironment = resolveEnvironment({
  apiBaseUrl: '/api',
  useRealApi: true,
  authMode: 'entra',
  entra: null,
  guestSite: { tenant: 'aarfid-demo', property: 'riverside' },
});
