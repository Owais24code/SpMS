# SpMS API — R1 pilot slice

ASP.NET Core 8 minimal API for the scheduling slice of AARFID SpMS. **Zero
external NuGet packages** (framework packs only) because the feed is
unreachable in this environment; `NuGet.config` clears all sources so a
restore cannot silently start depending on one.

## Layout

    src/Spms.Domain          rules, no framework or HTTP dependency
    src/Spms.Infrastructure  in-memory adapters for the domain ports
    src/Spms.Api             HTTP surface
    tests/Spms.Tests         82 cases, zero-dependency runner
    tools/http-acceptance.mjs 73 live wire-contract cases

## Running

    dotnet build Spms.sln -c Release
    dotnet run  --project tests/Spms.Tests/Spms.Tests.csproj       # 82 cases

    ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://127.0.0.1:5199 \
      dotnet run --project src/Spms.Api/Spms.Api.csproj -c Release
    node tools/http-acceptance.mjs http://127.0.0.1:5199           # 73 cases

The HTTP sweep mutates the seeded board, so run it against a **freshly
started** instance; a second run against the same process asserts against
state the first run left behind. That goes away with the database adapter.

## Authentication

Development only, and deliberately crude: `X-Spa-Scopes`, `X-Spa-Tenant`,
`X-Spa-Property`, `X-Spa-Actor`. Outside Development those headers are ignored
entirely and every scoped endpoint answers 401 — there is no JWT middleware
yet, and falling back to a demo tenant would be an open door. `/health`,
`/health/live` and `/health/ready` stay open for probes.

## Endpoints

| Method | Path | Scope |
|---|---|---|
| GET  | `/health`, `/health/live`, `/health/ready` | — |
| GET  | `/services` | `spa.read` |
| GET  | `/appointments?date=` or `?from=&to=` (`offset`, `limit`) | `spa.read` |
| GET  | `/appointments/{id}` | `spa.read` |
| POST | `/appointments` | `spa.write` |
| POST | `/appointments/{id}/transitions` | `spa.write` |
| GET  | `/availability?date=&serviceId=` | `spa.read` |
| POST | `/schedule/preflight` | `spa.schedule` |
| POST | `/appointments/{id}/reassign` | `spa.schedule` |
| GET  | `/audit?limit=` | `spa.admin` |

## The preflight flow (SCH-020 / GUI-003)

`POST /schedule/preflight` returns a single-use token, a 90-second TTL and the
conflicts. `POST /appointments/{id}/reassign` commits that token.

Three things about it are load-bearing and were each a defect first:

- **Conflicts are re-evaluated at commit.** The token's conflict list records
  what the operator was *shown*; it is not a warrant to skip validation.
  Trusting it let a room booked by anyone else inside the 90-second window go
  unnoticed, and CON-002 — which no role may override — committed with a 200.
  A board that changed answers 409 with both conflict sets.
- **A recoverable refusal does not consume the token.** Consuming it before
  validating meant the reason prompt CON-001 exists to collect was a dead end.
- **The route id is checked against the token.** It was ignored, so a client
  bug rescheduled a different guest than the URL named.

Evaluation and the write happen under one gate per tenant+property. That is a
single-node measure: the durable fix is a Postgres exclusion constraint on
`(property_id, room_id, tstzrange(start_utc, end_utc))` translated back to
CON-002, and until it exists this gate is the only thing enforcing the
constraint.

## Known gaps

Ranked, and none of them hidden behind a passing test:

1. **No unit of work.** The aggregate write and the audit row are separate
   operations, so a crash between them loses the audit row. Needed:
   `IUnitOfWork` spanning the write, the audit insert and the token
   consumption, with idempotency deliberately outside it.
2. **No authentication.** Header auth is Development-only; JWT bearer against
   Entra ID is not wired, so no non-Development environment can serve traffic.
3. **Tenancy is convention, not structure.** Scoping is two adjacent string
   parameters threaded through every call site. Under EF Core it belongs in
   `HasQueryFilter` plus a `SaveChangesInterceptor`, so a forgotten filter
   cannot leak.
4. **No event outbox**, so a second operator's board never learns of a change.
5. **CON-005 is property-local** while the rule ("the guest cannot attend
   both") is tenant-wide. Needs a deliberately tenant-scoped port method, or
   an amended requirement.
6. **Ports encode in-memory semantics**: `Copy()` return values, a manual
   `int` RowVersion, a computed `EndUtc` that will not translate to SQL, and a
   TOCTOU confirmation-number loop.
7. **No configuration** beyond CORS origins: buffers, TTLs, the catalogue and
   the qualification matrix are all compiled in.
8. **`CON-006` and `CON-007` are declared and never emitted**, as are
   `OWNERSHIP_AMBIGUOUS`, `PAYMENT_OUTCOME_AMBIGUOUS`, `RATE_LIMITED` and
   `OFFLINE_ACTION_BLOCKED`. They mirror the published catalogue; no client
   should build handling for them yet.
9. **`/availability` has no resource calendar** — no shifts, closures or room
   inventory — so it reports which resources are busy rather than what is
   genuinely bookable.
10. **No metrics or tracing.** Correlation ids reach the logs; conflict rates
    and 412 rates, which are the numbers a pilot is run to collect, do not.
