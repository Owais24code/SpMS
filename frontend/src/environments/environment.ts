import { resolveEnvironment, type SpmsEnvironment } from './runtime-config';

/**
 * Production defaults.
 *
 * `apiBaseUrl` is a same-origin relative path so that a deployment which never
 * writes `assets/config.js` still reaches an API behind the same front door,
 * rather than pointing at a hostname that only existed on the build agent.
 *
 * The dev identity values below are inert in production: the API only trusts
 * X-Spa-* headers in its Development environment, and the auth interceptor
 * sends a bearer token instead once one is configured.
 */
export const environment: SpmsEnvironment = resolveEnvironment({
  apiBaseUrl: '/api',
  useRealApi: true,
  devIdentity: {
    tenant: 'tenant-demo',
    property: 'prop-riverside',
    actor: 'unknown',
  },
});
