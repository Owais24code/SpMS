import { request, type APIRequestContext } from '@playwright/test';

export const API = process.env['SPMS_API_URL'] ?? 'http://127.0.0.1:5199';

export const ids = {
  riverside: '01920000-0000-7000-8000-000000000101',
  harbour: '01920000-0000-7000-8000-000000000102',
  guestAva: '01920000-0000-7000-8000-000000000701',
};

/** An API client signed in as a seeded dev login. */
export const staff = (login: string): Promise<APIRequestContext> =>
  request.newContext({ baseURL: API, extraHTTPHeaders: { 'X-Spa-Login': login } });

export const key = (): string => `e2e-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;
