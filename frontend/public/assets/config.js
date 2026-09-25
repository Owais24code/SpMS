/*
 * Deploy-time configuration. The deployment OVERWRITES this file; it is not
 * part of the application bundle and is never fingerprinted, so the same built
 * bundle can be uploaded to blob storage for every environment.
 *
 * Every field is optional — whatever is left out falls back to the value
 * compiled into src/environments/environment.ts.
 *
 *   apiBaseUrl  absolute or same-origin, no trailing slash
 *   useRealApi  false serves the in-memory demo data instead
 *   tenant / property / actor  development identity headers only
 */
window.__SPMS_CONFIG__ = {
  // apiBaseUrl: 'https://spms-api.example.com',
  // useRealApi: true,
  // tenant: 'tenant-demo',
  // property: 'prop-riverside',
  // actor: 'unknown',
};
