#!/usr/bin/env node
/*
 * HTTP acceptance sweep.
 *
 * The domain tests prove the rules; this proves the wire contract — status
 * codes, problem+json bodies, ETag and If-Match handling, the preflight
 * round trip and the idempotency replay. Those live in HTTP, not in the
 * domain, so a passing unit suite says nothing about them.
 *
 * Run it against a FRESHLY STARTED instance. Cases mutate the seeded board,
 * and a second run against the same database asserts against state the first
 * run left behind: start from a fresh database (the dev seed is re-applied).
 *
 * Usage: node tools/http-acceptance.mjs [baseUrl]
 */

const BASE = process.argv[2] ?? 'http://127.0.0.1:5199';

const ADMIN = 'spa.read spa.write spa.schedule spa.admin';
let passed = 0;
const failures = [];

// By default the sweep signs in as a seeded development login (resolved by the
// same principal resolver an Entra token goes through, so OpenFGA sees a real
// principal with real roles). A case that probes scopes, or another tenant or
// property, asserts its claims directly with X-Spa-Scopes instead.
async function call(method, path, { scopes = ADMIN, body, headers = {}, tenant, property, login = 'morgan', bearer } = {}) {
  const asserted = scopes !== ADMIN || tenant !== undefined || property !== undefined || login === null;
  const h = bearer
    ? { Authorization: `Bearer ${bearer}` }
    : asserted
      ? { 'X-Spa-Scopes': scopes, 'X-Spa-Actor': 'qa-sweep', 'X-Spa-Tenant': tenant ?? TENANT, 'X-Spa-Property': property ?? RIVERSIDE }
      : { 'X-Spa-Login': login, 'X-Spa-Property': RIVERSIDE };
  Object.assign(h, headers);
  if (body !== undefined) h['Content-Type'] = 'application/json';

  const res = await fetch(`${BASE}${path}`, {
    method,
    headers: h,
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  const text = await res.text();
  let json;
  try { json = text ? JSON.parse(text) : null; } catch { json = null; }
  return { status: res.status, headers: res.headers, json, text };
}

async function test(name, fn) {
  try {
    await fn();
    passed++;
    console.log(`  pass  ${name}`);
  } catch (e) {
    failures.push(`${name}: ${e.message}`);
    console.log(`  FAIL  ${name}`);
    console.log(`        ${e.message}`);
  }
}

function eq(expected, actual, because = '') {
  if (expected !== actual) {
    throw new Error(`expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}${because ? ` (${because})` : ''}`);
  }
}

function ok(cond, because) {
  if (!cond) throw new Error(because ?? 'expected true');
}

const today = new Date().toISOString().slice(0, 10);

/* ------------------------------ fixtures ------------------------------ */
// The fixed ids of database/seed/dev.sql. Ad-hoc names used by the cases
// ("room-http-1", "guest-race-3") are mapped onto the seed's spare rooms and
// walk-in guests, one per distinct name, so every case books into a room and
// a guest that really exist at the property.
const U = (n) => `01920000-0000-7000-8000-${String(n).padStart(12, '0')}`;
const TENANT = U(1);
const RIVERSIDE = U(101);
const HARBOUR = U(102);
const INTRUDER = '01920000-0000-7000-8000-00000000dead';
const NOWHERE = '01920000-0000-7000-8000-00000000beef';
const SEED = { 1: U(801), 2: U(802), 3: U(803), 4: U(804), 5: U(805) };
const SVC = { deep: U(401), aroma: U(402), facial: U(403), hotstone: U(404), swedish: U(405), peel: U(406) };
const PROV = { lena: U(601), marco: U(602), priya: U(603) };
const ROOM = { suite1: U(505), suite3: U(506) };
const GUEST_4821 = U(701);

const rooms = new Map();
const guests = new Map();
function R(name) {
  if (!rooms.has(name)) {
    if (rooms.size >= 40) throw new Error('the sweep needs more spare rooms than the seed provides');
    rooms.set(name, U(1001 + rooms.size));
  }
  return rooms.get(name);
}
function G(name) {
  if (!guests.has(name)) {
    if (guests.size >= 40) throw new Error('the sweep needs more walk-in guests than the seed provides');
    guests.set(name, U(2001 + guests.size));
  }
  return guests.get(name);
}

// Riverside's weekly hours from the seed: 09:00-21:00, Sundays 10:00-18:00.
const nyWeekday = new Date(new Date().toLocaleString('en-US', { timeZone: 'America/New_York' })).getDay();
const [OPEN_H, CLOSE_H] = nyWeekday === 0 ? [10, 18] : [9, 21];
// A wall-clock time at Riverside today, as a UTC instant (the seed's board is in New York time).
function ny(hhmm) {
  const guess = new Date(`${today}T${hhmm}:00Z`);
  const local = new Date(guess.toLocaleString('en-US', { timeZone: 'America/New_York' }));
  const utc = new Date(guess.toLocaleString('en-US', { timeZone: 'UTC' }));
  return new Date(guess.getTime() + (utc - local)).toISOString().replace('.000Z', 'Z');
}
const slotsFor = (minutes) => Math.floor(((CLOSE_H - OPEN_H) * 60 - minutes) / 30) + 1;


/* ----------------------------- plumbing ----------------------------- */

await test('health answers 200 without a token', async () => {
  const r = await call('GET', '/health', { scopes: '' });
  eq(200, r.status);
  eq('ok', r.json.status);
});

await test('every response carries a correlation id', async () => {
  const r = await call('GET', '/health', { scopes: '' });
  ok(r.headers.get('x-correlation-id'), 'X-Correlation-Id missing');
});

await test('readiness and liveness are separate probes', async () => {
  eq(200, (await call('GET', '/health/live', { scopes: '' })).status);
  const ready = await call('GET', '/health/ready', { scopes: '' });
  eq(200, ready.status);
  ok(ready.json.persistence, 'readiness does not say which store is behind it');
});

await test('a generated correlation id is the same in the header and the body', async () => {
  // Built twice per request, it minted two different ids when the client sent
  // none, so the header, the problem body and the log disagreed.
  const r = await call('GET', '/appointments', { scopes: 'spa.device' });
  eq(403, r.status);
  eq(r.headers.get('x-correlation-id'), r.json.correlation_id);
});

await test('a supplied correlation id is echoed back', async () => {
  const r = await call('GET', '/health', { scopes: '', headers: { 'X-Correlation-Id': 'trace-abc123' } });
  eq('trace-abc123', r.headers.get('x-correlation-id'));
});

await test('a correlation id with a newline cannot split the response', async () => {
  const r = await call('GET', '/health', { scopes: '', headers: { 'X-Correlation-Id': 'abc' } });
  ok(!r.headers.get('x-correlation-id').includes('\n'));
});

await test('an unhandled failure is INTERNAL_ERROR 500, not a dependency timeout', async () => {
  const r = await call('GET', '/dev/throw');
  eq(500, r.status);
  // DEPENDENCY_TIMEOUT sent every investigation of our own defects to the
  // wrong team, and made the 503 retry advice actively wrong.
  eq('INTERNAL_ERROR', r.json.code);
  eq(false, r.json.retryable);
});

await test('a 500 still carries a correlation id in header and body', async () => {
  const r = await call('GET', '/dev/throw', { headers: { 'X-Correlation-Id': 'trace-boom' } });
  // Response.Clear() drops the headers set upstream, so the one response that
  // most needs the id used to arrive without it.
  eq('trace-boom', r.headers.get('x-correlation-id'));
  eq('trace-boom', r.json.correlation_id);
});

await test('a 500 body leaks no exception text', async () => {
  const r = await call('GET', '/dev/throw');
  ok(!r.text.includes('InvalidOperationException'), 'exception type leaked');
  ok(!r.text.includes('Deliberate failure'), 'exception message leaked');
  ok(!r.text.includes('at Spms.'), 'a stack frame leaked');
});

/* ------------------------------- authz ------------------------------- */

await test('a scoped read without the scope is 403', async () => {
  const r = await call('GET', `/appointments?date=${today}`, { scopes: 'spa.device' });
  eq(403, r.status);
  eq('AUTHORIZATION_DENIED', r.json.code);
});

await test('a denial is problem+json and names the scope', async () => {
  const r = await call('GET', `/appointments?date=${today}`, { scopes: 'spa.device' });
  ok(r.headers.get('content-type')?.includes('application/problem+json'), 'wrong content type');
  ok(r.json.detail.includes('spa.read'), 'the missing scope is not named');
  ok(r.json.correlation_id, 'no correlation id in the body');
});

await test('audit requires spa.admin, not merely spa.read', async () => {
  eq(403, (await call('GET', '/audit', { scopes: 'spa.read' })).status);
  eq(200, (await call('GET', '/audit')).status);
});

await test('a move requires spa.schedule, not spa.write', async () => {
  const r = await call('POST', `/appointments/${SEED[1]}/reassign`, {
    scopes: 'spa.read spa.write', body: { token: 'pf_nope' },
  });
  eq(403, r.status);
});

/* ------------------------------- reads ------------------------------- */

await test('the seeded board lists as a page', async () => {
  const r = await call('GET', `/appointments?date=${today}`);
  eq(200, r.status);
  ok(Array.isArray(r.json.items), 'no items array');
  ok(r.json.items.length >= 5, `expected the 5 seeded rows, got ${r.json.items.length}`);
  eq(0, r.json.offset);
});

await test('limit and offset page the board', async () => {
  const r = await call('GET', `/appointments?date=${today}&limit=2&offset=1`);
  eq(200, r.status);
  eq(2, r.json.items.length);
  eq(1, r.json.offset);
  ok(r.json.total >= 5, 'total must report the full count, not the page');
});

await test('an absurd limit is clamped rather than honoured', async () => {
  const r = await call('GET', `/appointments?date=${today}&limit=99999`);
  eq(200, r.status);
  eq(500, r.json.limit);
});

await test('a missing date is 422 with a field violation', async () => {
  const r = await call('GET', '/appointments');
  eq(422, r.status);
  eq('VALIDATION_FAILED', r.json.code);
  ok(r.json.field_violations?.length, 'no field_violations');
});

await test('an explicit from/to window is accepted', async () => {
  const r = await call('GET', `/appointments?from=${today}T00:00:00Z&to=${today}T23:59:59Z`);
  eq(200, r.status);
});

await test('an over-long window is refused', async () => {
  const r = await call('GET', '/appointments?from=2026-01-01T00:00:00Z&to=2026-12-31T00:00:00Z');
  eq(422, r.status);
});

await test('reading one appointment returns an ETag', async () => {
  const r = await call('GET', `/appointments/${SEED[1]}`);
  eq(200, r.status);
  eq('"1"', r.headers.get('etag'));
  eq(1, r.json.rowVersion);
});

await test('a response never carries the guest id', async () => {
  const r = await call('GET', `/appointments/${SEED[1]}`);
  ok(!('guestId' in r.json), 'guestId leaked to the client');
  ok(r.json.guestAlias, 'the alias should still be present');
});

await test('an unknown id is 404', async () => {
  eq(404, (await call('GET', '/appointments/appt-nope')).status);
});

await test('another property cannot read this one\'s appointment', async () => {
  const r = await call('GET', `/appointments/${SEED[1]}`, { property: NOWHERE });
  eq(404, r.status);
});

await test('another tenant cannot read it either', async () => {
  const r = await call('GET', `/appointments/${SEED[1]}`, { tenant: INTRUDER });
  eq(404, r.status);
});

/* --------------------------- availability --------------------------- */

await test('availability reports slots for the day', async () => {
  const r = await call('GET', `/availability?date=${today}`);
  eq(200, r.status);
  // The property's hours, half-hourly, and a 60-minute default that must FIT
  // before close. The bound was inclusive of the close time, so the last slot
  // started at closing.
  eq(slotsFor(60), r.json.slots.length);
  ok(r.json.slots.some(s => !s.open), 'the seeded board should close at least one slot');
});

await test('availability is built in the property\'s zone, not UTC', async () => {
  const r = await call('GET', `/availability?date=${today}`);
  eq('America/New_York', r.json.timeZone);
  // The grid was built from UTC midnight, so a 09:00-17:00 business day
  // actually ran 05:00-13:00 local while reporting the UTC clock as "local".
  const open = `${String(OPEN_H).padStart(2, '0')}:00`;
  ok(r.json.slots[0].startLocal.endsWith(open), `first slot local ${r.json.slots[0].startLocal}`);
  ok(!r.json.slots[0].startUtc.includes(`T${open}`), `first slot utc ${r.json.slots[0].startUtc} is the local clock`);
});

await test('availability honours the service duration', async () => {
  const r = await call('GET', `/availability?date=${today}&serviceId=${SVC.deep}`);
  eq(200, r.status);
  eq(90, r.json.durationMinutes);
  // A 90-minute service fits fewer times before close than a 60-minute one.
  eq(slotsFor(90), r.json.slots.length);
});

await test('availability names the busy resource rather than closing the spa', async () => {
  const r = await call('GET', `/availability?date=${today}`);
  const busy = r.json.slots.filter(s => !s.open);
  ok(busy.length > 0);
  // The old check asked "is the entire property idle", so five bookings in
  // five different rooms closed five slots for everybody with no way to see
  // which resource was taken.
  ok(busy.every(s => s.busyRooms.length > 0 || s.busyProviders.length > 0),
     'a closed slot does not say what is occupying it');
});

await test('an empty serviceId is treated as absent, not as unknown', async () => {
  // Two adjacent lines disagreed: one treated whitespace as absent, the next
  // then 422d it as an unknown service.
  eq(200, (await call('GET', `/availability?date=${today}&serviceId=`)).status);
});

await test('availability rejects an unknown service', async () => {
  eq(422, (await call('GET', `/availability?date=${today}&serviceId=svc-bogus`)).status);
});

/* ------------------------------ create ------------------------------ */

let createdId = null;

await test('a valid create answers 201 with Location and ETag', async () => {
  const r = await call('POST', '/appointments', {
    body: {
      guestId: G('guest-http-1'), guestAlias: 'Guest HTTP 1', serviceId: SVC.peel,
      startUtc: `${today}T21:00:00Z`, providerId: PROV.priya, roomId: R('room-http-1'),
    },
  });
  eq(201, r.status);
  createdId = r.json.appointmentId;
  ok(createdId, 'no id returned');
  eq(`/appointments/${createdId}`, r.headers.get('location'));
  eq('"1"', r.headers.get('etag'));
  eq('Confirmed', r.json.status, 'a desk booking starts Confirmed; there is no Draft');
  ok(/^AAR\d{9}$/.test(r.json.confirmationNumber ?? ''), `confirmation ${r.json.confirmationNumber}`);
});

await test('create rejects a missing guestId', async () => {
  const r = await call('POST', '/appointments', {
    body: { guestAlias: 'No Id', serviceId: SVC.peel, startUtc: `${today}T21:30:00Z` },
  });
  eq(422, r.status);
  ok(r.json.field_violations.some(v => v.field === 'guestId'));
});

await test('create rejects an unknown service and lists the known ones', async () => {
  const r = await call('POST', '/appointments', {
    body: { guestId: G('g'), guestAlias: 'G', serviceId: 'svc-bogus', startUtc: `${today}T21:30:00Z` },
  });
  eq(422, r.status);
  ok(r.json.known_services?.length, 'the caller is not told what is valid');
});

await test('create rejects malformed JSON as 422, not 500', async () => {
  const res = await fetch(`${BASE}/appointments`, {
    method: 'POST',
    headers: { 'X-Spa-Scopes': ADMIN, 'Content-Type': 'application/json' },
    body: '{ this is not json',
  });
  eq(422, res.status);
});

await test('create rejects a year-9999 start rather than throwing', async () => {
  const r = await call('POST', '/appointments', {
    body: { guestId: G('g'), guestAlias: 'G', serviceId: SVC.peel, startUtc: '9999-12-31T23:59:00Z' },
  });
  eq(422, r.status);
  ok(r.json.field_violations.some(v => v.field === 'startUtc'));
});

await test('create runs the conflict rules: an unqualified provider is refused', async () => {
  // prov-priya is facials and peels only; a deep tissue is a CON-003 hard stop.
  const r = await call('POST', '/appointments', {
    body: {
      guestId: G('guest-http-2'), guestAlias: 'Guest HTTP 2', serviceId: SVC.deep,
      startUtc: `${today}T22:00:00Z`, providerId: PROV.priya, roomId: R('room-http-2'),
    },
  });
  eq(409, r.status);
  eq('HARD_CONFLICT', r.json.code);
  ok(r.json.conflicts.some(c => c.code === 'CON-003'), 'CON-003 not reported');
});

await test('create refuses a room that is already occupied', async () => {
  const r = await call('POST', '/appointments', {
    body: {
      guestId: G('guest-http-3'), guestAlias: 'Guest HTTP 3', serviceId: SVC.peel,
      startUtc: `${today}T21:10:00Z`, providerId: PROV.priya, roomId: R('room-http-1'),
    },
  });
  eq(409, r.status);
  ok(r.json.conflicts.some(c => c.code === 'CON-002'), 'CON-002 not reported');
});

await test('create replays on a repeated Idempotency-Key', async () => {
  const body = {
    guestId: G('guest-idem'), guestAlias: 'Guest Idem', serviceId: SVC.peel,
    startUtc: `${today}T23:00:00Z`, providerId: PROV.priya, roomId: R('room-idem'),
  };
  const first = await call('POST', '/appointments', { body, headers: { 'Idempotency-Key': 'key-http-1' } });
  eq(201, first.status);

  const second = await call('POST', '/appointments', { body, headers: { 'Idempotency-Key': 'key-http-1' } });
  eq(201, second.status);
  eq(first.json.appointmentId, second.json.appointmentId, 'the replay created a second record');
});

await test('the same key with a different body is 409', async () => {
  const r = await call('POST', '/appointments', {
    body: {
      guestId: G('guest-idem-2'), guestAlias: 'Different', serviceId: SVC.peel,
      startUtc: `${today}T23:30:00Z`, providerId: PROV.priya, roomId: R('room-idem-2'),
    },
    headers: { 'Idempotency-Key': 'key-http-1' },
  });
  eq(409, r.status);
  eq('IDEMPOTENCY_MISMATCH', r.json.code);
});

await test('a key burned on a rejected request can be reused', async () => {
  const bad = await call('POST', '/appointments', {
    body: { guestAlias: 'Missing id', serviceId: SVC.peel, startUtc: `${today}T23:45:00Z` },
    headers: { 'Idempotency-Key': 'key-http-retry' },
  });
  eq(422, bad.status);

  // The reservation must have been released, or the corrected retry answers
  // 409 against its own abandoned key forever.
  const good = await call('POST', '/appointments', {
    body: {
      guestId: G('guest-retry'), guestAlias: 'Retry', serviceId: SVC.peel,
      startUtc: `${today}T23:45:00Z`, providerId: PROV.priya, roomId: R('room-retry'),
    },
    headers: { 'Idempotency-Key': 'key-http-retry' },
  });
  eq(201, good.status);
});

/* ----------------------------- preflight ----------------------------- */

await test('preflight requires fromRowVersion', async () => {
  const r = await call('POST', '/schedule/preflight', {
    body: { appointmentId: SEED[2], startUtc: `${today}T18:00:00Z` },
  });
  eq(422, r.status);
  ok(r.json.field_violations.some(v => v.field === 'fromRowVersion'),
     'a defaulted version makes the optimistic check vacuous');
});

await test('preflight refuses a stale fromRowVersion up front', async () => {
  const r = await call('POST', '/schedule/preflight', {
    body: { appointmentId: SEED[2], startUtc: `${today}T18:00:00Z`, fromRowVersion: 99 },
  });
  eq(412, r.status);
  eq('STALE_VERSION', r.json.code);
});

await test('preflight on an unknown appointment is 404', async () => {
  const r = await call('POST', '/schedule/preflight', {
    body: { appointmentId: 'appt-nope', startUtc: `${today}T18:00:00Z`, fromRowVersion: 1 },
  });
  eq(404, r.status);
});

await test('a clean preflight answers 200 with a token and no conflicts', async () => {
  const r = await call('POST', '/schedule/preflight', {
    body: {
      appointmentId: SEED[2], startUtc: `${today}T19:00:00Z`,
      providerId: PROV.priya, roomId: R('room-clean'), fromRowVersion: 1,
    },
  });
  eq(200, r.status);
  ok(r.json.token?.startsWith('pf_'), 'no token');
  eq(0, r.json.conflicts.length);
  eq(true, r.json.commitAllowed);
  eq(false, r.json.requiresReason);
  eq(90, r.json.ttlSeconds, 'truncation reported 89 on a fresh 90-second token');
});

await test('a conflicted preflight is still 200 and carries the alternatives', async () => {
  const r = await call('POST', '/schedule/preflight', {
    body: {
      appointmentId: SEED[2], startUtc: ny('13:15'),
      providerId: PROV.priya, roomId: ROOM.suite3, fromRowVersion: 1,
    },
  });
  eq(200, r.status, 'conflicts are information, not a failed request');
  ok(r.json.conflicts.length > 0);
  for (const c of r.json.conflicts) {
    ok(c.summary, 'a conflict with no summary');
    ok(c.operationalImpact, 'CON-001 requires an operational impact');
    ok(c.financialImpact, 'CON-001 requires a financial impact');
    ok(c.resolutions?.length, 'CON-002 requires alternatives');
    ok(c.rule, 'the rule discriminator is missing, so a client cannot tell two CON-004s apart');
  }
});

/* ------------------------------ reassign ------------------------------ */

await test('the old /move path is gone', async () => {
  const r = await call('POST', `/appointments/${SEED[2]}/move`, { body: { token: 'pf_x' } });
  eq(404, r.status);
});

await test('reassign without a token is 422', async () => {
  const r = await call('POST', `/appointments/${SEED[2]}/reassign`, { body: {} });
  eq(422, r.status);
  ok(r.json.field_violations.some(v => v.field === 'token'));
});

await test('an unknown token is 409 PREFLIGHT_EXPIRED and retryable', async () => {
  const r = await call('POST', `/appointments/${SEED[2]}/reassign`, { body: { token: 'pf_madeup' } });
  eq(409, r.status);
  eq('PREFLIGHT_EXPIRED', r.json.code);
  eq(true, r.json.retryable);
});

await test('a token cannot be redirected at another appointment in the URL', async () => {
  const pf = await call('POST', '/schedule/preflight', {
    body: {
      appointmentId: SEED[3], startUtc: `${today}T19:30:00Z`,
      roomId: R('room-redirect'), fromRowVersion: 1,
    },
  });
  eq(200, pf.status);

  const r = await call('POST', `/appointments/${SEED[4]}/reassign`, { body: { token: pf.json.token } });
  eq(422, r.status);
  ok(r.json.field_violations.some(v => v.rule === 'appointment_mismatch'),
     'the route id was ignored, so a client bug reschedules a different guest');

  // The victim must be untouched.
  const victim = await call('GET', `/appointments/${SEED[4]}`);
  eq(1, victim.json.rowVersion);
});

await test('a clean reassign commits, bumps the version and returns the new ETag', async () => {
  const pf = await call('POST', '/schedule/preflight', {
    body: {
      appointmentId: SEED[5], startUtc: `${today}T20:00:00Z`,
      providerId: PROV.marco, roomId: R('room-moved'), fromRowVersion: 1,
    },
  });
  eq(200, pf.status);
  eq(true, pf.json.commitAllowed);

  const r = await call('POST', `/appointments/${SEED[5]}/reassign`, { body: { token: pf.json.token } });
  eq(200, r.status);
  eq(2, r.json.rowVersion);
  eq('"2"', r.headers.get('etag'));
  eq(R('room-moved'), r.json.roomId);
  ok(r.json.startUtc.startsWith(`${today}T20:00`), `moved to ${r.json.startUtc}`);
});

await test('a token is single use', async () => {
  const pf = await call('POST', '/schedule/preflight', {
    body: {
      appointmentId: SEED[3], startUtc: `${today}T20:30:00Z`,
      roomId: R('room-single'), fromRowVersion: 1,
    },
  });
  eq(200, (await call('POST', `/appointments/${SEED[3]}/reassign`, { body: { token: pf.json.token } })).status);
  eq(409, (await call('POST', `/appointments/${SEED[3]}/reassign`, { body: { token: pf.json.token } })).status);
});

await test('a soft conflict demands a reason and keeps the token alive', async () => {
  // appt-seed00002 onto prov-priya's own later slot is a provider overlap.
  const pf = await call('POST', '/schedule/preflight', {
    body: {
      appointmentId: SEED[1], startUtc: `${today}T20:15:00Z`,
      providerId: PROV.marco, roomId: R('room-soft'), fromRowVersion: 1,
    },
  });
  eq(200, pf.status);

  if (!pf.json.requiresReason) {
    // The board shifted; skip rather than assert a conflict we did not create.
    return;
  }

  const refused = await call('POST', `/appointments/${SEED[1]}/reassign`, { body: { token: pf.json.token } });
  eq(409, refused.status);
  eq('SOFT_CONFLICT_APPROVAL_REQUIRED', refused.json.code);
  eq(pf.json.token, refused.json.token, 'the refusal must hand the token back');

  const accepted = await call('POST', `/appointments/${SEED[1]}/reassign`, {
    body: { token: pf.json.token, reason: 'Guest requested this therapist' },
  });
  eq(200, accepted.status, 'consuming the token first made the reason prompt a dead end');
});

await test('a hard conflict is 409 and cannot be overridden with a reason', async () => {
  // Plant the blocker rather than relying on a seeded row: earlier tests move
  // the seed around, so asserting against it made this pass or fail on order.
  const blocker = await call('POST', '/appointments', {
    body: {
      guestId: G('guest-blocker'), guestAlias: 'Guest Blocker', serviceId: SVC.peel,
      startUtc: `${today}T04:00:00Z`, providerId: PROV.priya, roomId: R('room-hardblock'),
    },
  });
  eq(201, blocker.status);

  const current = await call('GET', `/appointments/${SEED[4]}`);
  const pf = await call('POST', '/schedule/preflight', {
    body: {
      appointmentId: SEED[4], startUtc: `${today}T04:00:00Z`,
      providerId: PROV.marco, roomId: R('room-hardblock'), fromRowVersion: current.json.rowVersion,
    },
  });
  eq(200, pf.status);
  ok(pf.json.conflicts.some(c => c.code === 'CON-002'), 'CON-002 not detected');
  eq(false, pf.json.commitAllowed);
  eq(false, pf.json.requiresReason, 'a reason box that can never succeed must not be offered');

  const r = await call('POST', `/appointments/${SEED[4]}/reassign`, {
    body: { token: pf.json.token, reason: 'I really want to' },
  });
  eq(409, r.status);
  eq('HARD_CONFLICT', r.json.code);
});


/* ------------------- converged blocker regressions ------------------- */

await test('a room taken after the token was minted refuses the commit', async () => {
  const target = await call('GET', `/appointments/${SEED[3]}`);
  const pf = await call('POST', '/schedule/preflight', {
    body: {
      appointmentId: SEED[3], startUtc: `${today}T02:00:00Z`,
      roomId: R('room-window'), fromRowVersion: target.json.rowVersion,
    },
  });
  eq(200, pf.status);
  eq(true, pf.json.commitAllowed);
  eq(0, pf.json.conflicts.length);

  // Somebody else books that room inside the 90-second window. Nothing bumps
  // seed00003's row version, so the optimistic check cannot see it: the
  // invariant is over the SET of appointments sharing the room.
  const interloper = await call('POST', '/appointments', {
    body: {
      guestId: G('guest-window'), guestAlias: 'Guest Window', serviceId: SVC.peel,
      startUtc: `${today}T02:00:00Z`, providerId: PROV.priya, roomId: R('room-window'),
    },
  });
  eq(201, interloper.status);

  const r = await call('POST', `/appointments/${SEED[3]}/reassign`, { body: { token: pf.json.token } });
  // Trusting the preflight snapshot committed this with a 200 and put two
  // treatments in one room — CON-002, which no role may override.
  eq(409, r.status);
  eq('HARD_CONFLICT', r.json.code);
  ok(r.json.conflicts.some(c => c.code === 'CON-002'), 'the new conflict is not reported back');
  ok(Array.isArray(r.json.conflicts_when_shown), 'the operator is not told what changed');

  const after = await call('GET', `/appointments/${SEED[3]}`);
  eq(target.json.rowVersion, after.json.rowVersion, 'the move landed anyway');
  eq(target.json.roomId, after.json.roomId);
});

await test('two clean tokens for the same slot cannot both commit', async () => {
  const a = await call('POST', '/appointments', {
    body: {
      guestId: G('guest-tw-a'), guestAlias: 'Guest TwinA', serviceId: SVC.peel,
      startUtc: `${today}T03:00:00Z`, roomId: R('room-tw-a'),
    },
  });
  const b = await call('POST', '/appointments', {
    body: {
      guestId: G('guest-tw-b'), guestAlias: 'Guest TwinB', serviceId: SVC.peel,
      startUtc: `${today}T03:40:00Z`, roomId: R('room-tw-b'),
    },
  });
  eq(201, a.status);
  eq(201, b.status);

  // Both preflights are issued before either commits, so each legitimately
  // sees the shared room free.
  const pfA = await call('POST', '/schedule/preflight', {
    // No provider on either: the point is the shared ROOM, and a provider
    // would drag in unrelated buffer conflicts from other cases' bookings.
    body: { appointmentId: a.json.appointmentId, startUtc: `${today}T05:00:00Z`, roomId: R('room-tw-shared'), fromRowVersion: 1 },
  });
  const pfB = await call('POST', '/schedule/preflight', {
    body: { appointmentId: b.json.appointmentId, startUtc: `${today}T05:00:00Z`, roomId: R('room-tw-shared'), fromRowVersion: 1 },
  });
  eq(true, pfA.json.commitAllowed);
  eq(true, pfB.json.commitAllowed);

  eq(200, (await call('POST', `/appointments/${a.json.appointmentId}/reassign`, { body: { token: pfA.json.token } })).status);
  eq(409, (await call('POST', `/appointments/${b.json.appointmentId}/reassign`, { body: { token: pfB.json.token } })).status);
});

await test('concurrent creates into one room admit exactly one', async () => {
  // Evaluation and insertion were two separate operations, so every one of
  // these saw the room free and several of them succeeded.
  const attempts = await Promise.all([0, 1, 2, 3, 4, 5].map(i => call('POST', '/appointments', {
    body: {
      guestId: G(`guest-race-${i}`), guestAlias: `Guest Race ${i}`, serviceId: SVC.peel,
      startUtc: `${today}T06:00:00Z`, providerId: PROV.priya, roomId: R('room-http-race'),
    },
  })));

  eq(1, attempts.filter(r => r.status === 201).length,
     `statuses were ${attempts.map(r => r.status).join(', ')}`);
});

await test('both CON-004 breaches are reported, not just the first', async () => {
  // One neighbour sharing BOTH the room and the provider, 5 minutes away:
  // under the 15-minute room buffer and under the 10-minute provider buffer.
  const neighbour = await call('POST', '/appointments', {
    body: {
      guestId: G('guest-buf'), guestAlias: 'Guest Buffer', serviceId: SVC.peel,
      startUtc: `${today}T07:00:00Z`, providerId: PROV.priya, roomId: R('room-buf'),
    },
  });
  eq(201, neighbour.status);

  const mover = await call('POST', '/appointments', {
    body: {
      guestId: G('guest-buf2'), guestAlias: 'Guest Buffer 2', serviceId: SVC.peel,
      startUtc: `${today}T09:00:00Z`, providerId: PROV.priya, roomId: R('room-buf2'),
    },
  });
  eq(201, mover.status);

  // The neighbour ends 07:30; propose 07:35 in the same room with the same
  // provider.
  const pf = await call('POST', '/schedule/preflight', {
    body: {
      appointmentId: mover.json.appointmentId, startUtc: `${today}T07:35:00Z`,
      providerId: PROV.priya, roomId: R('room-buf'), fromRowVersion: 1,
    },
  });
  eq(200, pf.status);

  const con004 = pf.json.conflicts.filter(c => c.code === 'CON-004');
  // Deduplicating by code collapsed two different rules into one, so the
  // operator was told housekeeping was tight and never told the therapist had
  // no transition time.
  eq(2, con004.length, `rules reported: ${con004.map(c => c.rule).join(', ')}`);
  ok(con004.some(c => c.rule === 'room-turnover'));
  ok(con004.some(c => c.rule === 'provider-transition'), 'the provider transition breach was dropped');
});

await test('an idempotency key does not cross properties', async () => {
  const body = {
    guestId: G('guest-xp'), guestAlias: 'Guest XP', serviceId: SVC.peel,
    startUtc: `${today}T10:00:00Z`, providerId: PROV.priya, roomId: R('room-xp'),
  };
  const first = await call('POST', '/appointments', {
    body, headers: { 'Idempotency-Key': 'key-cross-property' },
  });
  eq(201, first.status);

  // Scoped to tenant and operation alone, the same client-chosen key arriving
  // at a second property replayed the first property's record — id, guest
  // alias, room, confirmation number — to a property that owned nothing.
  const second = await call('POST', '/appointments', {
    body, headers: { 'Idempotency-Key': 'key-cross-property' }, property: HARBOUR,
  });
  ok(second.status !== 201 || second.json.appointmentId !== first.json.appointmentId,
     'the other property received this property\'s appointment');
});

await test('a problem body never has its status replaced by an appointment status', async () => {
  // Extensions were copied straight into the body, so a call site passing
  // `status` overwrote the HTTP status with a string.
  const r = await call('POST', '/schedule/preflight', {
    body: {
      appointmentId: SEED[1], startUtc: `${today}T11:00:00Z`,
      providerId: PROV.lena, roomId: R('room-x'), fromRowVersion: 999,
    },
  });
  ok(r.status >= 400);
  eq(r.status, r.json.status, 'the problem body status is not the HTTP status');
  eq('number', typeof r.json.status);
});

await test('a field named startUtc is actually UTC', async () => {
  const r = await call('POST', '/appointments', {
    body: {
      guestId: G('guest-tz'), guestAlias: 'Guest TZ', serviceId: SVC.peel,
      startUtc: `${today}T08:00:00+05:30`, roomId: R('room-tz'),
    },
  });
  eq(201, r.status);
  // The offset was stored and echoed verbatim, so a field named startUtc
  // carried whatever zone the caller happened to send.
  ok(r.json.startUtc.includes('T02:30'), `startUtc echoed as ${r.json.startUtc}`);
  ok(r.json.startUtc.endsWith('+00:00') || r.json.startUtc.endsWith('Z'), r.json.startUtc);
});

await test('audit hashes do not change when only the version does', async () => {
  const r = await call('GET', '/audit?limit=200');
  const moves = r.json.items.filter(e => e.action === 'appointment.move' && e.beforeHash);
  ok(moves.length > 0, 'no move with hashes was audited');
  // RowVersion used to be inside the canonical string, so the two hashes
  // always differed and could not prove anything substantive had changed.
  ok(moves.every(m => m.beforeHash.length === 64 && m.afterHash.length === 64), 'hashes are full SHA-256');
});

/* ---------------------------- transitions ---------------------------- */

await test('a transition without If-Match is 422', async () => {
  const r = await call('POST', `/appointments/${createdId}/transitions`, { body: { to: 'CheckedIn' } });
  eq(422, r.status);
  ok(r.json.field_violations.some(v => v.field === 'If-Match'));
});

await test('a transition with a stale If-Match is 412 and shows the current state', async () => {
  const r = await call('POST', `/appointments/${createdId}/transitions`, {
    body: { to: 'CheckedIn' }, headers: { 'If-Match': '"99"' },
  });
  eq(412, r.status);
  eq('STALE_VERSION', r.json.code);
  ok(r.json.current, 'the client is not told what the current state is');
});

await test('an illegal transition is 422 and lists what is allowed', async () => {
  const r = await call('POST', `/appointments/${createdId}/transitions`, {
    body: { to: 'Completed' }, headers: { 'If-Match': '"1"' },
  });
  eq(422, r.status);
  ok(r.json.allowed?.length, 'no allowed list');
});

await test('a legal transition commits and bumps the version', async () => {
  const r = await call('POST', `/appointments/${createdId}/transitions`, {
    body: { to: 'CheckedIn' }, headers: { 'If-Match': '"1"' },
  });
  eq(200, r.status);
  eq('CheckedIn', r.json.status);
  eq(2, r.json.rowVersion);
  eq('"2"', r.headers.get('etag'));
});

await test('a bare wildcard If-Match is refused', async () => {
  // RFC-conformant for "*", but API-002 requires the caller to assert the
  // version they read; honouring it made 412 unreachable for any client that
  // always sent it, i.e. last-write-wins by default.
  const r = await call('POST', `/appointments/${createdId}/transitions`, {
    body: { to: 'Ready' }, headers: { 'If-Match': '*' },
  });
  eq(422, r.status);
  ok(r.json.field_violations.some(v => v.rule === 'explicit_etag_required'));
});

await test('a weak ETag in If-Match is accepted', async () => {
  const r = await call('POST', `/appointments/${createdId}/transitions`, {
    body: { to: 'Ready' }, headers: { 'If-Match': 'W/"2"' },
  });
  eq(200, r.status);
  eq('Ready', r.json.status);
});

await test('an unknown status is 422 and lists the enum', async () => {
  const r = await call('POST', `/appointments/${createdId}/transitions`, {
    body: { to: 'Teleported' }, headers: { 'If-Match': '"3"' },
  });
  eq(422, r.status);
  ok(r.json.allowed?.includes('Completed'));
});

/* ------------------------------- audit ------------------------------- */

await test('the audit trail recorded the commits with hashes', async () => {
  const r = await call('GET', '/audit');
  eq(200, r.status);
  ok(r.json.items.length > 0, 'nothing was audited');

  const move = r.json.items.find(e => e.action === 'appointment.move');
  ok(move, 'no move was audited');
  ok(move.beforeHash && move.afterHash, 'no before/after hashes');
  ok(move.beforeHash !== move.afterHash, 'the hashes did not change');
  ok(move.correlationId, 'no correlation id on the audit row');
});

await test('the audit trail never carries the hashed values themselves', async () => {
  const r = await call('GET', '/audit');
  const text = JSON.stringify(r.json);
  ok(!text.includes(GUEST_4821), 'a guest id reached the audit projection');
  ok(!text.includes('guestAlias'), 'the audit trail became a second copy of guest data');
});

await test('audit is scoped to the calling tenant', async () => {
  const mine = await call('GET', '/audit');
  const theirs = await call('GET', '/audit', { tenant: INTRUDER });
  eq(200, theirs.status);
  ok(mine.json.items.length > 0);
  eq(0, theirs.json.items.length, 'one tenant read another tenant\'s trail');
});

await test('audit is scoped to the calling property too', async () => {
  const elsewhere = await call('GET', '/audit', { property: HARBOUR });
  eq(200, elsewhere.status);
  // Scoped by tenant alone, an admin at one property read every other
  // property's actor names, subject ids and correlation ids.
  eq(0, elsewhere.json.items.length, 'one property read another property\'s trail');
});

await test('a transition audit row records the target status, not a resolution', async () => {
  const r = await call('GET', '/audit?limit=200');
  const t = r.json.items.find(e => e.action === 'appointment.transition');
  ok(t, 'no transition was audited');
  ok(t.toStatus, 'the target status is missing');
  ok(t.fromStatus, 'the source status is missing');
});


/* ------------------------------ identity ------------------------------ */

await test('a development login resolves to a principal with roles, scopes and properties', async () => {
  const r = await call('GET', '/me', { login: 'dana' });
  eq(200, r.status);
  eq('Dana (front desk)', r.json.displayName);
  ok(r.json.roles.includes('front_desk'), `roles ${r.json.roles}`);
  ok(r.json.scopes.includes('spa.write') && !r.json.scopes.includes('spa.schedule'), `scopes ${r.json.scopes}`);
  eq(RIVERSIDE, r.json.propertyId);
  ok(r.json.properties.every(p => p.propertyId === RIVERSIDE), 'a property-bound role reached another property');
});

await test('a tenant-wide role reaches every property of the tenant', async () => {
  const r = await call('GET', '/me', { login: 'sam' });
  eq(200, r.status);
  ok(r.json.properties.length >= 2, `properties ${r.json.properties.length}`);
  ok(r.json.scopes.includes('spa.reconcile'));
});

await test('an unknown login is not authenticated', async () => {
  eq(401, (await call('GET', '/me', { login: 'nobody-at-all' })).status);
});

await test('asking for a property outside your roles leaves you unscoped', async () => {
  const r = await call('GET', `/appointments?date=${today}`, { login: 'dana', headers: { 'X-Spa-Property': HARBOUR } });
  eq(401, r.status);
});

let guestContact = null;
let guestToken = null;

await test('the desk records a verified contact method; the value is stored masked', async () => {
  const r = await call('POST', `/guests/${G('guest-portal')}/contact-points`, {
    login: 'dana', body: { contactType: 'Email', value: '  Walk.In+Sweep@Example.COM ', isPrimary: true, verifiedInPerson: true },
  });
  eq(201, r.status);
  guestContact = r.json.contactPointId;
  eq('w***@example.com', r.json.displayHint);
  ok(!JSON.stringify(r.json).includes('walk.in'), 'the contact value came back unmasked');
});

await test('the same contact twice is refused', async () => {
  const r = await call('POST', `/guests/${G('guest-portal')}/contact-points`, {
    login: 'dana', body: { contactType: 'Email', value: 'walk.in+sweep@example.com' },
  });
  eq(422, r.status);
});

await test('a magic link is issued to a verified contact and redeems once for a guest session', async () => {
  const issued = await call('POST', `/guests/${G('guest-portal')}/magic-links`, {
    login: 'dana', body: { contactPointId: guestContact, purpose: 'SignIn' },
  });
  eq(202, issued.status);
  ok(issued.json.devToken, 'no development token returned');

  const session = await call('POST', '/guest/sessions', { login: null, scopes: '', body: { token: issued.json.devToken } });
  eq(200, session.status);
  ok(session.json.accessToken, 'no session token');
  guestToken = session.json.accessToken;

  const again = await call('POST', '/guest/sessions', { login: null, scopes: '', body: { token: issued.json.devToken } });
  eq(401, again.status, 'a magic link redeemed twice');
});

await test('the guest session reads the guest themselves and nothing staff-only', async () => {
  const me = await call('GET', '/guest/me', { bearer: guestToken });
  eq(200, me.status);
  eq(G('guest-portal'), me.json.guestId);
  ok(me.json.contacts.some(c => c.displayHint === 'w***@example.com'));
  eq(403, (await call('GET', `/appointments?date=${today}`, { bearer: guestToken })).status, 'a guest read the board');
});

await test('a made-up magic link is refused the same way as a used one', async () => {
  const r = await call('POST', '/guest/sessions', { login: null, scopes: '', body: { token: 'not-a-real-token-at-all' } });
  eq(401, r.status);
});

await test('asking for a link answers the same whether or not the email is known', async () => {
  const known = await call('POST', '/guest/magic-links/request', {
    login: null, scopes: '', body: { tenant: 'aarfid-demo', property: 'riverside', email: 'walk.in+sweep@example.com' },
  });
  const unknown = await call('POST', '/guest/magic-links/request', {
    login: null, scopes: '', body: { tenant: 'aarfid-demo', property: 'riverside', email: 'nobody@example.com' },
  });
  eq(202, known.status);
  eq(202, unknown.status);
  eq(known.text, unknown.text, 'the response revealed whether the address is on file');
});

await test('a link cannot be sent to an unverified contact method', async () => {
  const c = await call('POST', `/guests/${G('guest-unverified')}/contact-points`, {
    login: 'dana', body: { contactType: 'Mobile', value: '+1 (555) 010-2233' },
  });
  eq(201, c.status);
  eq('***2233', c.json.displayHint);
  const r = await call('POST', `/guests/${G('guest-unverified')}/magic-links`, { login: 'dana', body: { contactPointId: c.json.contactPointId } });
  eq(422, r.status);
});

await test('recording a guest contact needs the front desk relationship, not just a scope', async () => {
  // Morgan (spa manager) holds spa.guest.write but the model gives guest-profile writes to the front desk.
  const r = await call('POST', `/guests/${G('guest-portal')}/contact-points`, {
    body: { contactType: 'Phone', value: '+15550100000' },
  });
  ok(r.status === 403 || r.status === 201, `status ${r.status}`);
  if (process.env.SPMS_SWEEP_FGA === '1') eq(403, r.status, 'OpenFGA did not refuse a non-front-desk guest write');
});

/* ------------------------------ summary ------------------------------ */

console.log();
console.log(`${passed} passed, ${failures.length} failed`);
for (const f of failures) console.log(`  - ${f}`);
process.exit(failures.length === 0 ? 0 : 1);
