import { afterEach, describe, expect, it } from 'vitest';
import { resolveEnvironment, type SpmsEnvironment } from './runtime-config';

const compiled: SpmsEnvironment = {
  apiBaseUrl: '/api',
  useRealApi: true,
  authMode: 'entra',
  entra: null,
  guestSite: { tenant: 't', property: 'p' },
};

describe('resolveEnvironment', () => {
  afterEach(() => { delete window.__SPMS_CONFIG__; });

  it('keeps the compiled values when there is no runtime file, degrading entra without config to demo', () => {
    const env = resolveEnvironment(compiled);
    expect(env.apiBaseUrl).toBe('/api');
    expect(env.authMode).toBe('demo');
  });

  it('takes a complete Entra block from the runtime file', () => {
    window.__SPMS_CONFIG__ = {
      apiBaseUrl: 'https://api.example.com/',
      entra: { clientId: 'c', authority: 'https://login.microsoftonline.com/t/', apiScopes: 'api://spms/a api://spms/b' },
    };
    const env = resolveEnvironment(compiled);
    expect(env.apiBaseUrl).toBe('https://api.example.com');
    expect(env.authMode).toBe('entra');
    expect(env.entra).toEqual({ clientId: 'c', authority: 'https://login.microsoftonline.com/t', apiScopes: ['api://spms/a', 'api://spms/b'] });
  });

  it('is offline whenever there is no API', () => {
    window.__SPMS_CONFIG__ = { useRealApi: false, authMode: 'demo' };
    expect(resolveEnvironment(compiled).authMode).toBe('offline');
  });

  it('ignores values of the wrong type', () => {
    window.__SPMS_CONFIG__ = { apiBaseUrl: 42, useRealApi: 'yes', authMode: 'root' };
    const env = resolveEnvironment({ ...compiled, authMode: 'demo' });
    expect(env.apiBaseUrl).toBe('/api');
    expect(env.useRealApi).toBe(true);
    expect(env.authMode).toBe('demo');
  });
});
