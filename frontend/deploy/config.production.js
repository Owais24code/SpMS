/*
 * Production runtime configuration for the `spms` web app (Azure App Service).
 * build-for-azure.ps1 copies this over dist/web/browser/assets/config.js after
 * every build. Nothing here is secret: client ids and tenant ids are public.
 *
 * Fill in the four <...> values from docs/deploy-azure-portal.md (steps 2 and 4).
 */
window.__SPMS_CONFIG__ = {
  apiBaseUrl: 'https://<api-default-domain>',          // the spms-api web app, no trailing slash
  useRealApi: true,
  authMode: 'entra',
  entra: {
    clientId: '<spa-client-id>',                         // App registration "SpMS web" → Application (client) ID
    authority: 'https://login.microsoftonline.com/<tenant-id>',
    apiScopes: ['api://<api-client-id>/access_as_user'], // App registration "SpMS API" → Expose an API
  },
  // The codes given to Provision__Tenant__Code and Provision__Properties__0__Code.
  guestTenant: 'aarfid',
  guestProperty: 'main',
};
