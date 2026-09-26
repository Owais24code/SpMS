import { resolveEnvironment, type SpmsEnvironment } from './runtime-config';

/**
 * Local development against `dotnet run --project backend/src/Spms.Host`.
 *
 * `demo` sign-in uses the seeded dev logins (database/seed/dev.sql): the API
 * resolves X-Spa-Login through the same principal resolver an Entra token
 * goes through, so roles, properties and scopes are the database's, not the
 * client's.
 *
 * `useRealApi: false` in assets/config.js demos the screens with no API
 * running, without editing or rebuilding anything.
 */
export const environment: SpmsEnvironment = resolveEnvironment({
  apiBaseUrl: 'http://127.0.0.1:5199',
  useRealApi: true,
  authMode: 'demo',
  entra: null,
  guestSite: { tenant: 'aarfid-demo', property: 'riverside' },
});
