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
 * so a second run against the same process asserts against state the first
 * run left behind. The in-memory store is the reason; it goes away with the
 * database adapter.
 *
 * Usage: node tools/http-acceptance.mjs [baseUrl]
 */

const BASE = process.argv[2] ?? 'http://127.0.0.1:5199';

const ADMIN = 'spa.read spa.write spa.schedule spa.admin';
let passed = 0;
const failures = [];

async function call(method, path, { scopes = ADMIN, body, headers = {}, tenant, property } = {}) {
  const h = {
    'X-Spa-Scopes': scopes,
    'X-Spa-Actor': 'qa-sweep',
    'X-Spa-Tenant': tenant ?? 'tenant-demo',
    'X-Spa-Property': property ?? 'prop-riverside',
    ...headers,
  };
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
  const r = await call('POST', '/appointments/appt-seed00001/reassign', {
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
  const r = await call('GET', '/appointments/appt-seed00001');
  eq(200, r.status);
  eq('"1"', r.headers.get('etag'));
  eq(1, r.json.rowVersion);
});

await test('a response never carries the guest id', async () => {
  const r = await call('GET', '/appointments/appt-seed00001');
  ok(!('guestId' in r.json), 'guestId leaked to the client');
  ok(r.json.guestAlias, 'the alias should still be present');
});

await test('an unknown id is 404', async () => {
  eq(404, (await call('GET', '/appointments/appt-nope')).status);
});

await test('another property cannot read this one\'s appointment', async () => {
  const r = await call('GET', '/appointments/appt-seed00001', { property: 'prop-somewhere-else' });
  eq(404, r.status);
});

await test('another tenant cannot read it either', async () => {
  const r = await call('GET', '/appointments/appt-seed00001', { tenant: 'tenant-intruder' });
  eq(404, r.status);
});

/* --------------------------- availability --------------------------- */

await test('availability reports slots for the day', async () => {
  const r = await call('GET', `/availability?date=${today}`);
  eq(200, r.status);
  // 09:00-17:00 local, half-hourly, and a 60-minute default that must FIT
  // before close: 09:00 through 16:00 inclusive. The bound was inclusive of
  // the close time, so the last slot started at closing.
  eq(15, r.json.slots.length);
  ok(r.json.slots.some(s => !s.open), 'the seeded board should close at least one slot');
});

await test('availability is built in the property\'s zone, not UTC', async () => {
  const r = await call('GET', `/availability?date=${today}`);
  eq('America/New_York', r.json.timeZone);
  // The grid was built from UTC midnight, so a 09:00-17:00 business day
  // actually ran 05:00-13:00 local while reporting the UTC clock as "local".
  ok(r.json.slots[0].startLocal.endsWith('09:00'), `first slot local ${r.json.slots[0].startLocal}`);
  ok(r.json.slots[0].startUtc.includes('T13:00'), `first slot utc ${r.json.slots[0].startUtc}`);
});

await test('availability honours the service duration', async () => {
  const r = await call('GET', `/availability?date=${today}&serviceId=svc-deep`);
  eq(200, r.status);
  eq(90, r.json.durationMinutes);
  // A 90-minute service fits fewer times before close than a 60-minute one.
  eq(14, r.json.slots.length);
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
      guestId: 'guest-http-1', guestAlias: 'Guest HTTP 1', serviceId: 'svc-peel',
      startUtc: `${today}T21:00:00Z`, providerId: 'prov-priya', roomId: 'room-http-1',
    },
  });
  eq(201, r.status);
  createdId = r.json.appointmentId;
  ok(createdId, 'no id returned');
  eq(`/appointments/${createdId}`, r.headers.get('location'));
  eq('"1"', r.headers.get('etag'));
  eq('Draft', r.json.status);
  ok(/^AAR\d{9}$/.test(r.json.confirmationNumber ?? ''), `confirmation ${r.json.confirmationNumber}`);
});

await test('create rejects a missing guestId', async () => {
  const r = await call('POST', '/appointments', {
    body: { guestAlias: 'No Id', serviceId: 'svc-peel', startUtc: `${today}T21:30:00Z` },
  });
  eq(422, r.status);
  ok(r.json.field_violations.some(v => v.field === 'guestId'));
});

await test('create rejects an unknown service and lists the known ones', async () => {
  const r = await call('POST', '/appointments', {
    body: { guestId: 'g', guestAlias: 'G', serviceId: 'svc-bogus', startUtc: `${today}T21:30:00Z` },
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
    body: { guestId: 'g', guestAlias: 'G', serviceId: 'svc-peel', startUtc: '9999-12-31T23:59:00Z' },
  });
  eq(422, r.status);
  ok(r.json.field_violations.some(v => v.field === 'startUtc'));
});

await test('create runs the conflict rules: an unqualified provider is refused', async () => {
  // prov-priya is facials and peels only; a deep tissue is a CON-003 hard stop.
  const r = await call('POST', '/appointments', {
    body: {
      guestId: 'guest-http-2', guestAlias: 'Guest HTTP 2', serviceId: 'svc-deep',
      startUtc: `${today}T22:00:00Z`, providerId: 'prov-priya', roomId: 'room-http-2',
    },
  });
  eq(409, r.status);
  eq('HARD_CONFLICT', r.json.code);
  ok(r.json.conflicts.some(c => c.code === 'CON-003'), 'CON-003 not reported');
});

await test('create refuses a room that is already occupied', async () => {
  const r = await call('POST', '/appointments', {
    body: {
      guestId: 'guest-http-3', guestAlias: 'Guest HTTP 3', serviceId: 'svc-peel',
      startUtc: `${today}T21:10:00Z`, providerId: 'prov-priya', roomId: 'room-http-1',
    },
  });
  eq(409, r.status);
  ok(r.json.conflicts.some(c => c.code === 'CON-002'), 'CON-002 not reported');
});

await test('create replays on a repeated Idempotency-Key', async () => {
  const body = {
    guestId: 'guest-idem', guestAlias: 'Guest Idem', serviceId: 'svc-peel',
    startUtc: `${today}T23:00:00Z`, providerId: 'prov-priya', roomId: 'room-idem',
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
      guestId: 'guest-idem-2', guestAlias: 'Different', serviceId: 'svc-peel',
      startUtc: `${today}T23:30:00Z`, providerId: 'prov-priya', roomId: 'room-idem-2',
    },
    headers: { 'Idempotency-Key': 'key-http-1' },
  });
  eq(409, r.status);
  eq('IDEMPOTENCY_MISMATCH', r.json.code);
});

await test('a key burned on a rejected request can be reused', async () => {
  const bad = await call('POST', '/appointments', {
    body: { guestAlias: 'Missing id', serviceId: 'svc-peel', startUtc: `${today}T23:45:00Z` },
    headers: { 'Idempotency-Key': 'key-http-retry' },
  });
  eq(422, bad.status);

  // The reservation must have been released, or the corrected retry answers
  // 409 against its own abandoned key forever.
  const good = await call('POST', '/appointments', {
    body: {
      guestId: 'guest-retry', guestAlias: 'Retry', serviceId: 'svc-peel',
      startUtc: `${today}T23:45:00Z`, providerId: 'prov-priya', roomId: 'room-retry',
    },
    headers: { 'Idempotency-Key': 'key-http-retry' },
  });
  eq(201, good.status);
});

/* ----------------------------- preflight ----------------------------- */

await test('preflight requires fromRowVersion', async () => {
  const r = await call('POST', '/schedule/preflight', {
    body: { appointmentId: 'appt-seed00002', startUtc: `${today}T18:00:00Z` },
  });
  eq(422, r.status);
  ok(r.json.field_violations.some(v => v.field === 'fromRowVersion'),
     'a defaulted version makes the optimistic check vacuous');
});

await test('preflight refuses a stale fromRowVersion up front', async () => {
  const r = await call('POST', '/schedule/preflight', {
    body: { appointmentId: 'appt-seed00002', startUtc: `${today}T18:00:00Z`, fromRowVersion: 99 },
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
      appointmentId: 'appt-seed00002', startUtc: `${today}T19:00:00Z`,
      providerId: 'prov-priya', roomId: 'room-clean', fromRowVersion: 1,
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
      appointmentId: 'appt-seed00002', startUtc: `${today}T13:15:00Z`,
      providerId: 'prov-priya', roomId: 'room-suite3', fromRowVersion: 1,
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
  const r = await call('POST', '/appointments/appt-seed00002/move', { body: { token: 'pf_x' } });
  eq(404, r.status);
});

await test('reassign without a token is 422', async () => {
  const r = await call('POST', '/appointments/appt-seed00002/reassign', { body: {} });
  eq(422, r.status);
  ok(r.json.field_violations.some(v => v.field === 'token'));
});

await test('an unknown token is 409 PREFLIGHT_EXPIRED and retryable', async () => {
  const r = await call('POST', '/appointments/appt-seed00002/reassign', { body: { token: 'pf_madeup' } });
  eq(409, r.status);
  eq('PREFLIGHT_EXPIRED', r.json.code);
  eq(true, r.json.retryable);
});

await test('a token cannot be redirected at another appointment in the URL', async () => {
  const pf = await call('POST', '/schedule/preflight', {
    body: {
      appointmentId: 'appt-seed00003', startUtc: `${today}T19:30:00Z`,
      roomId: 'room-redirect', fromRowVersion: 1,
    },
  });
  eq(200, pf.status);

  const r = await call('POST', '/appointments/appt-seed00004/reassign', { body: { token: pf.json.token } });
  eq(422, r.status);
  ok(r.json.field_violations.some(v => v.rule === 'appointment_mismatch'),
     'the route id was ignored, so a client bug reschedules a different guest');

  // The victim must be untouched.
  const victim = await call('GET', '/appointments/appt-seed00004');
  eq(1, victim.json.rowVersion);
});

await test('a clean reassign commits, bumps the version and returns the new ETag', async () => {
  const pf = await call('POST', '/schedule/preflight', {
    body: {
      appointmentId: 'appt-seed00005', startUtc: `${today}T20:00:00Z`,
      providerId: 'prov-marco', roomId: 'room-moved', fromRowVersion: 1,
    },
  });
  eq(200, pf.status);
  eq(true, pf.json.commitAllowed);

  const r = await call('POST', '/appointments/appt-seed00005/reassign', { body: { token: pf.json.token } });
  eq(200, r.status);
  eq(2, r.json.rowVersion);
  eq('"2"', r.headers.get('etag'));
  eq('room-moved', r.json.roomId);
  ok(r.json.startUtc.startsWith(`${today}T20:00`), `moved to ${r.json.startUtc}`);
});

await test('a token is single use', async () => {
  const pf = await call('POST', '/schedule/preflight', {
    body: {
      appointmentId: 'appt-seed00003', startUtc: `${today}T20:30:00Z`,
      roomId: 'room-single', fromRowVersion: 1,
    },
  });
  eq(200, (await call('POST', '/appointments/appt-seed00003/reassign', { body: { token: pf.json.token } })).status);
  eq(409, (await call('POST', '/appointments/appt-seed00003/reassign', { body: { token: pf.json.token } })).status);
});

await test('a soft conflict demands a reason and keeps the token alive', async () => {
  // appt-seed00002 onto prov-priya's own later slot is a provider overlap.
  const pf = await call('POST', '/schedule/preflight', {
    body: {
      appointmentId: 'appt-seed00001', startUtc: `${today}T20:15:00Z`,
      providerId: 'prov-marco', roomId: 'room-soft', fromRowVersion: 1,
    },
  });
  eq(200, pf.status);

  if (!pf.json.requiresReason) {
    // The board shifted; skip rather than assert a conflict we did not create.
    return;
  }

  const refused = await call('POST', '/appointments/appt-seed00001/reassign', { body: { token: pf.json.token } });
  eq(409, refused.status);
  eq('SOFT_CONFLICT_APPROVAL_REQUIRED', refused.json.code);
  eq(pf.json.token, refused.json.token, 'the refusal must hand the token back');

  const accepted = await call('POST', '/appointments/appt-seed00001/reassign', {
    body: { token: pf.json.token, reason: 'Guest requested this therapist' },
  });
  eq(200, accepted.status, 'consuming the token first made the reason prompt a dead end');
});

await test('a hard conflict is 409 and cannot be overridden with a reason', async () => {
  // Plant the blocker rather than relying on a seeded row: earlier tests move
  // the seed around, so asserting against it made this pass or fail on order.
  const blocker = await call('POST', '/appointments', {
    body: {
      guestId: 'guest-blocker', guestAlias: 'Guest Blocker', serviceId: 'svc-peel',
      startUtc: `${today}T04:00:00Z`, providerId: 'prov-priya', roomId: 'room-hardblock',
    },
  });
  eq(201, blocker.status);

  const current = await call('GET', '/appointments/appt-seed00004');
  const pf = await call('POST', '/schedule/preflight', {
    body: {
      appointmentId: 'appt-seed00004', startUtc: `${today}T04:00:00Z`,
      providerId: 'prov-marco', roomId: 'room-hardblock', fromRowVersion: current.json.rowVersion,
    },
  });
  eq(200, pf.status);
  ok(pf.json.conflicts.some(c => c.code === 'CON-002'), 'CON-002 not detected');
  eq(false, pf.json.commitAllowed);
  eq(false, pf.json.requiresReason, 'a reason box that can never succeed must not be offered');

  const r = await call('POST', '/appointments/appt-seed00004/reassign', {
    body: { token: pf.json.token, reason: 'I really want to' },
  });
  eq(409, r.status);
  eq('HARD_CONFLICT', r.json.code);
});


/* ------------------- converged blocker regressions ------------------- */

await test('a room taken after the token was minted refuses the commit', async () => {
  const target = await call('GET', '/appointments/appt-seed00003');
  const pf = await call('POST', '/schedule/preflight', {
    body: {
      appointmentId: 'appt-seed00003', startUtc: `${today}T02:00:00Z`,
      roomId: 'room-window', fromRowVersion: target.json.rowVersion,
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
      guestId: 'guest-window', guestAlias: 'Guest Window', serviceId: 'svc-peel',
      startUtc: `${today}T02:00:00Z`, providerId: 'prov-priya', roomId: 'room-window',
    },
  });
  eq(201, interloper.status);

  const r = await call('POST', '/appointments/appt-seed00003/reassign', { body: { token: pf.json.token } });
  // Trusting the preflight snapshot committed this with a 200 and put two
  // treatments in one room — CON-002, which no role may override.
  eq(409, r.status);
  eq('HARD_CONFLICT', r.json.code);
  ok(r.json.conflicts.some(c => c.code === 'CON-002'), 'the new conflict is not reported back');
  ok(Array.isArray(r.json.conflicts_when_shown), 'the operator is not told what changed');

  const after = await call('GET', '/appointments/appt-seed00003');
  eq(target.json.rowVersion, after.json.rowVersion, 'the move landed anyway');
  eq(target.json.roomId, after.json.roomId);
});

await test('two clean tokens for the same slot cannot both commit', async () => {
  const a = await call('POST', '/appointments', {
    body: {
      guestId: 'guest-tw-a', guestAlias: 'Guest TwinA', serviceId: 'svc-peel',
      startUtc: `${today}T03:00:00Z`, roomId: 'room-tw-a',
    },
  });
  const b = await call('POST', '/appointments', {
    body: {
      guestId: 'guest-tw-b', guestAlias: 'Guest TwinB', serviceId: 'svc-peel',
      startUtc: `${today}T03:40:00Z`, roomId: 'room-tw-b',
    },
  });
  eq(201, a.status);
  eq(201, b.status);

  // Both preflights are issued before either commits, so each legitimately
  // sees the shared room free.
  const pfA = await call('POST', '/schedule/preflight', {
    // No provider on either: the point is the shared ROOM, and a provider
    // would drag in unrelated buffer conflicts from other cases' bookings.
    body: { appointmentId: a.json.appointmentId, startUtc: `${today}T05:00:00Z`, roomId: 'room-tw-shared', fromRowVersion: 1 },
  });
  const pfB = await call('POST', '/schedule/preflight', {
    body: { appointmentId: b.json.appointmentId, startUtc: `${today}T05:00:00Z`, roomId: 'room-tw-shared', fromRowVersion: 1 },
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
      guestId: `guest-race-${i}`, guestAlias: `Guest Race ${i}`, serviceId: 'svc-peel',
      startUtc: `${today}T06:00:00Z`, providerId: 'prov-priya', roomId: 'room-http-race',
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
      guestId: 'guest-buf', guestAlias: 'Guest Buffer', serviceId: 'svc-peel',
      startUtc: `${today}T07:00:00Z`, providerId: 'prov-priya', roomId: 'room-buf',
    },
  });
  eq(201, neighbour.status);

  const mover = await call('POST', '/appointments', {
    body: {
      guestId: 'guest-buf2', guestAlias: 'Guest Buffer 2', serviceId: 'svc-peel',
      startUtc: `${today}T09:00:00Z`, providerId: 'prov-priya', roomId: 'room-buf2',
    },
  });
  eq(201, mover.status);

  // The neighbour ends 07:30; propose 07:35 in the same room with the same
  // provider.
  const pf = await call('POST', '/schedule/preflight', {
    body: {
      appointmentId: mover.json.appointmentId, startUtc: `${today}T07:35:00Z`,
      providerId: 'prov-priya', roomId: 'room-buf', fromRowVersion: 1,
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
    guestId: 'guest-xp', guestAlias: 'Guest XP', serviceId: 'svc-peel',
    startUtc: `${today}T10:00:00Z`, providerId: 'prov-priya', roomId: 'room-xp',
  };
  const first = await call('POST', '/appointments', {
    body, headers: { 'Idempotency-Key': 'key-cross-property' },
  });
  eq(201, first.status);

  // Scoped to tenant and operation alone, the same client-chosen key arriving
  // at a second property replayed the first property's record — id, guest
  // alias, room, confirmation number — to a property that owned nothing.
  const second = await call('POST', '/appointments', {
    body, headers: { 'Idempotency-Key': 'key-cross-property' }, property: 'prop-other',
  });
  ok(second.status !== 201 || second.json.appointmentId !== first.json.appointmentId,
     'the other property received this property\'s appointment');
});

await test('a problem body never has its status replaced by an appointment status', async () => {
  // Extensions were copied straight into the body, so a call site passing
  // `status` overwrote the HTTP status with a string.
  const r = await call('POST', '/schedule/preflight', {
    body: {
      appointmentId: 'appt-seed00001', startUtc: `${today}T11:00:00Z`,
      providerId: 'prov-lena', roomId: 'room-x', fromRowVersion: 999,
    },
  });
  ok(r.status >= 400);
  eq(r.status, r.json.status, 'the problem body status is not the HTTP status');
  eq('number', typeof r.json.status);
});

await test('a field named startUtc is actually UTC', async () => {
  const r = await call('POST', '/appointments', {
    body: {
      guestId: 'guest-tz', guestAlias: 'Guest TZ', serviceId: 'svc-peel',
      startUtc: `${today}T08:00:00+05:30`, roomId: 'room-tz',
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
  ok(moves.every(m => m.beforeHash.length === 32 && m.afterHash.length === 32), 'hash shape changed');
});

/* ---------------------------- transitions ---------------------------- */

await test('a transition without If-Match is 422', async () => {
  const r = await call('POST', `/appointments/${createdId}/transitions`, { body: { to: 'Confirmed' } });
  eq(422, r.status);
  ok(r.json.field_violations.some(v => v.field === 'If-Match'));
});

await test('a transition with a stale If-Match is 412 and shows the current state', async () => {
  const r = await call('POST', `/appointments/${createdId}/transitions`, {
    body: { to: 'Confirmed' }, headers: { 'If-Match': '"99"' },
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
    body: { to: 'Confirmed' }, headers: { 'If-Match': '"1"' },
  });
  eq(200, r.status);
  eq('Confirmed', r.json.status);
  eq(2, r.json.rowVersion);
  eq('"2"', r.headers.get('etag'));
});

await test('a bare wildcard If-Match is refused', async () => {
  // RFC-conformant for "*", but API-002 requires the caller to assert the
  // version they read; honouring it made 412 unreachable for any client that
  // always sent it, i.e. last-write-wins by default.
  const r = await call('POST', `/appointments/${createdId}/transitions`, {
    body: { to: 'CheckedIn' }, headers: { 'If-Match': '*' },
  });
  eq(422, r.status);
  ok(r.json.field_violations.some(v => v.rule === 'explicit_etag_required'));
});

await test('a weak ETag in If-Match is accepted', async () => {
  const r = await call('POST', `/appointments/${createdId}/transitions`, {
    body: { to: 'CheckedIn' }, headers: { 'If-Match': 'W/"2"' },
  });
  eq(200, r.status);
  eq('CheckedIn', r.json.status);
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
  ok(!text.includes('guest-4821'), 'a guest id reached the audit projection');
  ok(!text.includes('guestAlias'), 'the audit trail became a second copy of guest data');
});

await test('audit is scoped to the calling tenant', async () => {
  const mine = await call('GET', '/audit');
  const theirs = await call('GET', '/audit', { tenant: 'tenant-intruder' });
  eq(200, theirs.status);
  ok(mine.json.items.length > 0);
  eq(0, theirs.json.items.length, 'one tenant read another tenant\'s trail');
});

await test('audit is scoped to the calling property too', async () => {
  const elsewhere = await call('GET', '/audit', { property: 'prop-elsewhere' });
  eq(200, elsewhere.status);
  // Scoped by tenant alone, an admin at one property read every other
  // property's actor names, subject ids and correlation ids.
  eq(0, elsewhere.json.items.length, 'one property read another property\'s trail');
});

await test('a transition audit row records the target status, not a resolution', async () => {
  const r = await call('GET', '/audit?limit=200');
  const t = r.json.items.find(e => e.action === 'appointment.transition');
  ok(t, 'no transition was audited');
  ok(t.targetStatus, 'the target status is missing');
  eq(null, t.selectedResolution ?? null, 'the target status was written into the resolution field');
});

/* ------------------------------ summary ------------------------------ */

console.log();
console.log(`${passed} passed, ${failures.length} failed`);
for (const f of failures) console.log(`  - ${f}`);
process.exit(failures.length === 0 ? 0 : 1);
