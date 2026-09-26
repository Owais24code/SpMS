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

export const U = (n: number): string => `01920000-0000-7000-8000-${String(n).padStart(12, '0')}`;
export const SVC = { swedish: U(405), facial: U(403) };

/** Today at Riverside (America/New_York), as yyyy-MM-dd. */
export const riversideToday = (): string =>
  new Intl.DateTimeFormat('en-CA', { timeZone: 'America/New_York', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());

/** An instant late today at Riverside (23:00–23:59 local), unique enough per run. */
export const lateTodayUtc = (): string => {
  const day = riversideToday();
  const minute = Math.floor(Math.random() * 59);
  // New York is UTC-4 or UTC-5: 23:mm local is 03:mm or 04:mm UTC the next day.
  const probe = new Date(`${day}T23:${String(minute).padStart(2, '0')}:00-05:00`);
  const offset = new Intl.DateTimeFormat('en-US', { timeZone: 'America/New_York', timeZoneName: 'shortOffset' })
    .formatToParts(probe).find((p) => p.type === 'timeZoneName')?.value === 'GMT-4' ? '-04:00' : '-05:00';
  return new Date(`${day}T23:${String(minute).padStart(2, '0')}:00${offset}`).toISOString();
};

/** Some day next year at 03:00Z, far from any other booking in that room. */
export const farFutureUtc = (): string => {
  const d = new Date(Date.UTC(new Date().getUTCFullYear() + 1, 0, 1 + Math.floor(Math.random() * 360), 3, 0, 0));
  return d.toISOString();
};
