import { resolveEnvironment, type SpmsEnvironment } from './runtime-config';

/**
 * Local development against `dotnet run` in the api/ folder.
 *
 * The tenant and property strings are the API's own Development fallbacks
 * (RequestContext.Build): sending something else would read an empty board,
 * because the seeded appointments belong to that tenant and property.
 *
 * `useRealApi` still reads the runtime file, so a developer can drop a
 * `useRealApi: false` into assets/config.js and demo the screens with no API
 * running without editing or rebuilding anything.
 */
export const environment: SpmsEnvironment = resolveEnvironment({
  apiBaseUrl: 'http://127.0.0.1:5199',
  useRealApi: true,
  devIdentity: {
    tenant: 'tenant-demo',
    property: 'prop-riverside',
    actor: 'dev@aarfid.com',
  },
});
