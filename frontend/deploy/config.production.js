/*
 * Production runtime configuration for the `spms` web app (Azure App Service).
 * build-for-azure.ps1 copies this over dist/web/browser/assets/config.js after
 * every build. Nothing here is secret.
 *
 * authMode 'local': SpMS's own email + password sign-in (the API's Auth:Local).
 * Replace <api-default-domain> with the spms-api web app's Default domain.
 *
 * Demo instead (no API, sample data):  window.__SPMS_CONFIG__ = { useRealApi: false, authMode: 'offline' };
 * Entra later: authMode 'entra' plus  entra: { clientId, authority, apiScopes }  (see docs/deploy-azure-portal.md).
 */
window.__SPMS_CONFIG__ = {
  apiBaseUrl: 'https://spms-api-fqhzcmagfxdsh9bn.centralindia-01.azurewebsites.net',   // no trailing slash
  useRealApi: true,
  authMode: 'local',
  // The codes given to Provision__Tenant__Code and Provision__Properties__0__Code.
  guestTenant: 'aarfid',
  guestProperty: 'main',
};
