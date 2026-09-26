/*
 * Deploy-time configuration. The deployment OVERWRITES this file; it is not
 * part of the application bundle and is never fingerprinted, so the same built
 * bundle can be uploaded to blob storage for every environment.
 *
 * Every field is optional — whatever is left out falls back to the value
 * compiled into src/environments/environment.ts.
 *
 *   apiBaseUrl     absolute or same-origin, no trailing slash
 *   useRealApi     false serves the in-memory demo data instead
 *   authMode       'entra' | 'demo' (API Development only) | 'offline'
 *   entra          { clientId, authority, apiScopes, redirectUri? }
 *   guestTenant / guestProperty   codes the guest web asks for links under
 */
window.__SPMS_CONFIG__ = {
  // apiBaseUrl: 'https://spms-api.example.com',
  // authMode: 'entra',
  // entra: {
  //   clientId: '00000000-0000-0000-0000-000000000000',
  //   authority: 'https://login.microsoftonline.com/<tenant-id>',
  //   apiScopes: ['api://spms-api/access_as_user'],
  // },
};
