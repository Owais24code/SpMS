# AARFID SpMS

Spa Management System for AARFID. It is built against the v2.13.26 development-team
handoff (`SPECIFICATION_v2.12.md`, 781-row traceability register), the UX Field Specification
(UX-001 … UX-012) and the AARFID brand guidelines.

**Release rule (from the handoff):** a row is not accepted just because code or a table
exists. Closure needs the full chain: requirement → database → business rule → API →
screen/device → permission → audit → automated test → acceptance evidence.

```
SpMS/
  frontend/        Angular 22: public site, staff workspace, design tokens
  backend/         .NET 8 / ASP.NET Core: modular monolith (see Architecture)
  database/        R1 PostgreSQL 16 schema: model, generator, generated SQL, SQL tests
  authorization/   OpenFGA model, model tests, live-server smoke test
  infra/           main.bicep (front end), openfga.bicep (Container Apps), local/compose.yaml
  .github/         CI (backend, frontend, database, authorization, bicep) and web deploy
```

---

## Getting started

| What | Command |
|---|---|
| Local PostgreSQL + OpenFGA | `docker compose -f infra/local/compose.yaml up -d` |
| Database: build + test | `PGURL=postgres://postgres:spms@localhost:5432/spms database/tools/verify.sh` |
| Database: regenerate SQL | `python3 database/tools/generate.py` (use `--check` in CI) |
| Authorization tests | `fga model test --tests authorization/model.fga.yaml` |
| Authorization live smoke | `OPENFGA_DATASTORE_URI=postgres://…/openfga authorization/smoke.sh` |
| Front end | `cd frontend && npm install && npm start` (http://localhost:4200) |
| Front-end checks | `npm run build` · `npm test` (vitest) · `npm run audit:contrast` |
| Front-end e2e | `npm run e2e` (Playwright; needs the dev API on :5199, starts `ng serve` itself) |
| Backend | `cd backend && dotnet build Spms.sln -c Release && dotnet test` |
| API (dev) | `ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Spms.Host` (migrates, seeds `database/seed/dev.sql`) |
| API with real OpenFGA | add `Authorization__OpenFga__ApiUrl=http://127.0.0.1:8080` (bootstraps the store and model in Development) |
| HTTP contract sweep | `node backend/tools/http-acceptance.mjs http://127.0.0.1:5199` (fresh instance only; `SPMS_SWEEP_FGA=1` with OpenFGA) |
| OpenFGA model JSON | `fga model transform --file authorization/model.fga > authorization/model.json` (CI checks it) |

**Dev logins.** In Development the API accepts `X-Spa-Login: <handle>` and resolves it through
the same principal resolver an Entra token uses: roles, properties and scopes come from the
database. The dev seed also publishes a health intake form. The seeded handles are `dana` (front desk, Riverside), `hana` (housekeeping, Riverside), `morgan` (spa manager and
platform admin, both properties), `riley` (scheduler), `lena` (provider), `sam` (finance) and
`ada` (platform admin). The sign-in screen offers them in `demo` mode.

On Windows, install npm dependencies from a local path, not a mounted or network share,
because npm's atomic renames fail on some mounts. Backend Postgres tests **skip silently**
unless `SPMS_TEST_CONNECTION` is set. CI sets it explicitly for that reason.

---

## Architecture

### Modular monolith

Everything deploys as one process with one database. It is organised as eleven modules, and
each module owns one PostgreSQL schema. At pilot scale (one property, 1,500 appointments a
day, per DEC-007), splitting into services would add network failure modes and distributed
transactions and buy nothing in return.

Modules sit on a dependency DAG. A module may reference only the modules below it, never
sideways or upward. The generator enforces this at design time, and `database/tests/01_structure.sql`
enforces it again in the database.

```
core ─┬─ catalog ─┬─ resources ─┐
      │           └─ workforce ─┤
      └─ guest ─────────────────┴─ scheduling ─┬─ intake
                                               ├─ inventory ─ commerce
                                               └─ messaging
core ── reporting
```

| Module | Tables | Owns |
|---|---:|---|
| core | 15 | tenant, property, principal + logins, devices, capability ownership (DEC-001), governed settings, code lists, audit + seals, outbox/inbox, idempotency, external mappings, legal hold |
| catalog | 4 | services, per-property offering, options, tax |
| resources | 3 | locations, rooms, maintenance windows |
| workforce | 7 | staff, HR profile (kept apart, SEC-007), roles, credentials (incl. screening), documents, qualifications (CON-003), schedule and leave |
| guest | 8 | tenant-wide guest (IDN-005), contact points, relationships, merge cases, delegation (IDN-003), consent, privacy requests, magic links |
| scheduling | 6 | visit (the day plan), appointments with room and provider (CON-001/002), preflight proposals, waitlist, turnaround, visit exceptions |
| intake | 3 | versioned forms, submissions, treatment notes (restricted: SEC-008) |
| inventory | 7 | items, variants, balances, stock ledger, laundry batches, service supplies, counts |
| commerce | 5 | orders (a Draft is the cart), lines, payment intents (payments, deposits, refunds), transactions, Marquee delegation |
| messaging | 2 | versioned templates with reminder timing, scheduled messages with delivery state |
| reporting | 1 | saved report runs |

**Backend layout.** `Spms.Host` is the composition root; `Spms.SharedKernel` holds the ports
(unit of work, audit, outbox, idempotency, access decisions, field protection); `Spms.Persistence`
owns the single `SpmsDbContext`, the tenancy interceptors and the migrations; `Spms.Web` the
request context, guards and problem+json. Each module is its own project
(`backend/src/Modules/Spms.Modules.<Name>`) and contributes its EF model through
`IModelContributor`. The row classes are generated from `database/model/*.py` by
`database/tools/generate_ef.py`, the same source the SQL comes from; CI fails if either is stale.

### Tenancy: three layers

1. **Authorization (OpenFGA).** Decides who may act on which object. See Authorization.
2. **Application.** EF Core `HasQueryFilter` on `tenant_id`/`property_id`, plus a
   `SaveChangesInterceptor` that stamps them. A new endpoint cannot forget to scope.
3. **Database (RLS).** Every table carrying `tenant_id` has row-level security **enabled and
   forced**, 61 of 61. Each request opens a transaction and calls
   `core.begin_scope(tenant, property_ids[], principal, correlation)`, which sets
   transaction-local settings. A pooled connection cannot carry one request's tenant into
   the next. With no scope set, nothing is visible.

---

## Database

`database/model/*.py` describes every table once. `database/tools/generate.py` applies the
conventions and writes `database/schema/*.sql` and `database/catalog.json`: a table
dictionary with the spec source for each table and every handoff column dropped, with the
reason. The generated SQL is the reviewed reference schema. The EF Core model must produce
exactly this structure, and a CI drift check (pg_dump diff) compares the two once the EF
model lands. Structures EF cannot model are written with `migrationBuilder.Sql()` in the same
migration: exclusion constraints, triggers, RLS and SECURITY DEFINER functions.

**Scope:** R1 in full, in **61 tables across 11 schemas**. The first draft had 123. It was
refactored to one table per entity using five rules:

1. History and version tables become the audit trail.
2. Template + version pairs become one versioned row.
3. Duplicate concepts get one owner. A cart is a Draft order, a hold is a Held appointment, the
   itinerary is the visit, and refunds and deposits are payment intents.
4. 1:N link tables and small child tables become columns or `jsonb` on the parent.
5. Governed configuration and simple lookups get one mechanism each: `core.setting` and
   `core.code_list`.

Typed tables remain wherever the database must enforce something or the spec requires
separation. `catalog.json` lists every removed table and column with its replacement.
`01_structure.sql` asserts the count, so adding a table is a deliberate change.

### Conventions

| Rule | Decision |
|---|---|
| Names | Handoff table and column names, snake_case |
| Keys | `<table>_id uuid`, **UUIDv7** generated by the app (`core.uuid_v7()` as the SQL default). `UNIQUE (tenant_id, id)` on every tenant table, so every FK is tenant-composite and cannot cross tenants |
| Same-property FKs | e.g. an appointment's room must belong to the appointment's property. Enforced with `(tenant_id, property_id, id)` FKs |
| Columns per entity | Each concept has one owner column. Settled money is `bigint *_minor` plus `currency_code`. Quantities, rates and costs are `numeric(…,6)` (spec, Six decimal places). Times are `timestamptz *_at` |
| Status | `text` + `CHECK`, **PascalCase** values (`Confirmed`, `CheckedIn`), the same values as the API contract |
| Lifecycle | Status + audit. **No hard delete** and no `deleted_at`. Removal happens only through retention and erasure jobs. The runtime role holds DELETE on three transient tables only |
| Concurrency | `version` must advance by exactly 1 on every update of a versioned row. This is enforced by trigger (SQLSTATE 40001), not only by the ORM (DEC-005) |
| Append-only | Audit, ledgers, status history, treatment notes, deliveries and facts. UPDATE/DELETE **raise** (the trigger stops even the owner). The only exception is `core.redact_*` for erasure |
| Partitioning | Monthly range partitions on `audit_event`, `event_outbox` and `inventory_ledger_entry`. They are created ahead by `core.ensure_monthly_partitions`, and the DEFAULT partition must stay empty |
| Restricted data | Intake answers, treatment notes, contact values, credential numbers and consent evidence are `*_cipher bytea` + `key_version`: app-level envelope encryption with Key Vault-wrapped keys. Search uses HMAC `lookup_hash` columns |
| Cross-module FKs | Only along the DAG |
| Settings & code lists | `core.setting`: effective-dated, one active value per key and scope, approved by someone other than the author. Used for feature flags, policies, retention and quiet hours. `core.code_list`: reason codes, tenders, license types, departments and revenue centres. Tax, capability ownership and legal hold stay typed |

### Hard rules the database enforces on its own

- **CON-002.** A room cannot hold two treatments. This is a GiST exclusion on
  `scheduling.appointment (room_id, [start_at, end_at))`, ignoring Cancelled and NoShow rows,
  so a cancellation frees the room at once.
- **CON-001 is soft.** Provider overlap is indexed, not constrained, so an audited override
  stays possible.
- **CON-005 is tenant-wide.** `scheduling.guest_busy_intervals()` sees every property of the
  current tenant, never another tenant's.
- **DEC-001.** One authority per capability, property and instant (`capability_ownership`
  exclusion).
- **SEC-014.** The proposer cannot approve their own change: configuration, capability
  ownership, roles, refunds, counts.
- **CON-003.** A staff member is not qualified for a service unless a qualification row says
  so (fails closed), and qualifications cannot overlap.
- **Consent evidence is immutable.** Only revocation can change a consent row.
- **IDN-003.** A delegation must name its delegate, use actions from a fixed set, and
  expire. No visibility level reaches intake.

### Database roles

| Role | Used by | Notes |
|---|---|---|
| `spms_owner` | migrations | owns everything; never used at runtime |
| `spms_app` | the API | NOBYPASSRLS; no privileges on `intake` |
| `spms_intake` | intake unit of work | granted to `spms_app` **without inherit**, so the code must `SET LOCAL ROLE spms_intake` |
| `spms_reporting` | reports/export | read-only |
| `spms_erasure` | privacy worker | redacts through `core.redact_*` only |
| `spms_outbox` | outbox publisher | BYPASSRLS, but only on `core.event_outbox` |
| `spms_definer` | SECURITY DEFINER functions | identity resolution before a tenant is known; CON-005 lookup |

### Verified

`database/tools/verify.sh` builds the whole schema on PostgreSQL 16 and runs six SQL test
files, all passing:

- structure: forced RLS, ownership, DELETE grants, FK index coverage, DAG, append-only
  triggers, partitions, exclusions
- RLS isolation run as `spms_app`
- scheduling: CON-002, CON-001, same-property room, version, cancel frees the room, hold
  expiry, hard conflicts never overridden, CON-005
- append-only and redaction
- identity resolution and single-use magic links
- integrity: DEC-001, SEC-014 on settings and refunds, consent, IDN-003, draft-only line
  deletion, UUIDv7

---

## Authentication and authorization

| Layer | Question | Mechanism |
|---|---|---|
| Authentication, staff | Who is calling? | **Microsoft Entra ID**, JWT bearer (`scp`/`roles`). `(issuer, oid)` in `core.principal_login` resolves to a `core.principal` through `core.resolve_principal()` |
| Authentication, guests | Who is calling? | **Own magic links** (SEC-010/011): short-lived, single-use and purpose-limited. Only the SHA-256 is stored, and `guest.resolve_magic_link()` consumes it atomically |
| Authentication, services | Who is calling? | Entra client credentials, linked through `core.principal_login` |
| Coarse gate | May this client call this API family? | `spa.*` scopes in the token |
| Fine authorization | May this principal do this to this object? | **OpenFGA** (`authorization/model.fga`) |
| Last line | Even if the app is wrong, what cannot leak? | PostgreSQL RLS |

The OpenFGA user id is `user:<principal_id>`. Replacing the identity provider therefore
never rewrites tuples. Roles and property lists stay out of the token.

**Stored tuples vs. contextual tuples.** Slow-changing facts are stored in OpenFGA and
synced from SpMS tables through the outbox. The tables stay the source of truth, and
`fga_synced_at` records the sync:

- roles (`workforce.staff_role_assignment`, per property or tenant-wide)
- guest ownership
- delegations (conditional `active_delegation`: expiry plus allowed properties)
- device registration

Facts about the row already loaded for the request are passed as **contextual tuples**:
appointment → property, guest, assigned provider; note → author. A reassign therefore needs
no tuple write.

**Lists:** check the parent object, then filter in SQL. For example, check
`property:<id>#can_view_board` once, then run the board query. `ListObjects` is only for
small sets.

**Kept out of OpenFGA:**

- monetary limits and dual approval (domain services and DB checks)
- "before lock" style state rules
- field masking (serialisers, driven by the model's relations)

**Failure behaviour:** closed. If OpenFGA is unreachable the API answers 503, never allow.
Revocation-sensitive checks use `HIGHER_CONSISTENCY`. Sensitive-read decisions are written
to `core.audit_event.authorization_decision` with the model id.

**Verified:** `fga model test` passes 91/91 checks across 11 tests. `smoke.sh` runs against a
real OpenFGA v1.10.2 on PostgreSQL, with stored roles, contextual tuples and a conditional
delegation, and passes 6/6.

**In this build:**

- `/me` returns the resolved principal, roles, effective scopes and the operator's properties.
  Effective scopes are the role scopes narrowed by any `spa.*` scopes in the token.
- `X-Spa-Property` picks the current property among those the principal holds; anything else is
  refused.
- Guests: staff add a verified contact point and issue a link
  (`POST /guests/{id}/magic-links`); a guest can also ask by email
  (`POST /guest/magic-links/request`, the same 202 whether or not the address is known).
  `POST /guest/sessions` redeems the link for a 30-minute guest session (`spa.guest.self`).
- Tuples are written from outbox events (role assignments, property registration, guest
  ownership, delegation, devices) and reconciled on start in Development.
- Without `OpenFga:ApiUrl` in Development, relationship checks are permissive and say so in
  the log. Everywhere else a missing or unreachable OpenFGA is a 503.

**Front-end sign-in** (`assets/config.js` → `authMode`):

| Mode | How | Where |
|---|---|---|
| `entra` | MSAL, authorization code + PKCE, full-page redirect; bearer token to the API | Production. Set `entra: { clientId, authority, apiScopes }` |
| `demo` | Pick a seeded dev login; the client sends `X-Spa-Login` | API in Development only |
| `offline` | Role presets, no API (`useRealApi: false`) | Screen demos |

The guest web lives at `/g/:token` (redeems the link, then drops it from the URL),
`/guest/sign-in` and `/guest`.

**Hosting:** `infra/openfga.bicep` provides Azure Container Apps with internal ingress only,
a preshared key from Key Vault through managed identity, a migration job, and at least one
replica. The datastore is a separate `openfga` database on the application's PostgreSQL
Flexible Server.

---

## Front end

```
frontend/src/app/
  core/       services, view models, placeholder data (no UI)
  shared/     reusable UI with no feature knowledge
  layouts/    site-layout (public shell), app-layout (workspace shell)
  features/   one lazy-loaded folder per screen
```

The front end uses standalone components with per-feature lazy routes. Every screen is its
own bundle. Screens carry almost no CSS of their own, because `styles/app.scss` provides the
workspace primitives.

**Design tokens.** `frontend/src/styles/` holds `tokens.scss`, `themes.scss`, `base.scss` and
`app.scss`. Components use only semantic tokens, never ramp values, so dark mode is a single
mapping change. `npm run audit:contrast` checks 44 pairs in both themes against WCAG 2.2 and
fails the build on regression.

**Brand deviations**, deliberate and recorded:

1. A semantic palette (success, warning, danger, info) was added.
2. Cool Gray 4C is not used for borders because it measures 1.86:1.
3. All-caps is limited to short `.eyebrow` labels.
4. `--font-numeric` provides tabular figures.

Glass effects are used on marketing surfaces only.

**Accessibility:** skip link, visible focus, 44px targets, `prefers-reduced-motion`,
`inert` on closed panels, and field-associated errors.

---

## Deployment (front end)

The Angular build is served from Azure Blob static website hosting behind **Front Door
Standard**. Front Door rewrites extensionless paths to `/index.html` so deep links answer
200 rather than 404, and the deploy workflow asserts this. The deployed build runs in demo
mode.

To repoint it at a real API, overwrite `assets/config.js` in `$web` and purge Front Door.
No rebuild is needed.

One-time setup:

1. `az group create -n rg-spms-dev -l westeurope`
2. Create an app registration with a federated credential for
   `repo:OWNER/REPO:environment:dev`. Assign Contributor on the resource group; the template
   grants *Storage Blob Data Contributor* to `deployerPrincipalId`.
3. Add repository secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`,
   variables `AZURE_RESOURCE_GROUP` and `AZURE_DEPLOYER_OBJECT_ID`, and a GitHub environment
   `dev`.

After that, every push under `frontend/` or `infra/` deploys. Purge Front Door after a
manual upload, because edges hold `index.html`:
`az afd endpoint purge -g $RG --profile-name $PROFILE --endpoint-name $ENDPOINT --content-paths '/' '/index.html' '/assets/config.js'`.

Front Door Standard has a monthly base charge. Azure Static Web Apps is the free
alternative, but it is a different resource.

---

## Status

Work is delivered in eight batches. Each is one commit on `main`.

| # | Batch | State |
|---|---|---|
| 1 | Backend foundation: EF Core on the 61-table R1 schema, three-layer tenancy, module split | Done |
| 2 | Identity and authorization: Entra JWT, principal resolution, OpenFGA checks and tuple sync, guest magic links; MSAL / dev-login sign-in, property switcher, guest landing | Done |
| 3 | Scheduling completion: tenant-wide CON-005, undo window, all-or-nothing bulk move, hold expiry, visits, waitlist, room turnover; desk check-in, undo, waitlist and turnover screens on the API | Done |
| 4 | Guests and intake: profiles and search, reviewed merge and split, delegation, consent, privacy requests with export and erasure; intake and treatment notes under `spms_intake` with envelope encryption; Guests, live booking, provider tablet and guest intake screens | Done |
| 5 | Commerce and payments: orders, deposits, refunds with dual approval, reconciliation, payment ownership, provider-agnostic port; Checkout and live Reconciliation screens | Done |
| 6 | Reference data: catalogue and property offering, rooms and closures, staff with HR file, roles, qualifications, credentials and roster, stock ledger with counts and laundry, governed settings; Staff, Inventory and Setup screens | Done |
| 7 | Messaging, reporting, devices, integrations, Marquee mode | Planned |
| 8 | Operating mode, search, live board, partition/retention jobs, API + PostgreSQL bicep, observability | Planned |

**Verified (batch 6):** backend 227/227 tests against PostgreSQL 16 and OpenFGA 1.10.2; HTTP
sweep 114/114 in permissive and real-FGA modes; `fga model test` 13/13 tests (135 checks); web
unit tests 12/12; Playwright e2e 16/16.

**Scheduling operations (batch 3):**

- **Undo (CON-006 window).** A committed reassign answers `undoUntilUtc`; the same token undoes it
  once (`POST /appointments/{id}/undo-reassign`). The undo is refused if the appointment changed
  since, or the original slot was taken in the meantime. The window is `Scheduling:UndoWindowSeconds`
  (120 by default).
- **Bulk move** (`POST /schedule/bulk-move`, `dryRun`) is all or nothing. Each item is judged against
  the board after every item has moved, so two appointments can trade rooms. The room exclusion is
  `DEFERRABLE` and deferred for the batch only.
- **Holds** expire through the per-property job runner (`core.active_properties()`, one scope and one
  transaction per job per property): Held becomes Cancelled with reason `HoldExpired`.
  `POST /dev/jobs/run` forces a run in Development.
- **Visits** (`/visits`) follow their appointments. The first check-in marks the visit Arrived and the
  first treatment marks it InProgress. Cancelling or no-showing a visit cascades to its bookings that
  have not started.
- **Waitlist** (`/waitlist`, `/waitlist/candidates`): offer for N minutes, accept onto a booking.
  Lapsed offers return to Waiting.
- **Room turnover** (`/turnaround`): a completed treatment creates a Turnover task. Housekeeping
  (`spa.inventory` plus `can_update_room_readiness`) completes it. `/front-desk/arrivals` reports the
  room as not ready while the task is open.

**Reference data (batch 6):**

- **Catalogue** (`/catalog/services`, `can_manage_catalog`). A service is drafted, activated, withdrawn
  and retired, never deleted. Edits need `If-Match`, and codes are unique. A change never reaches
  existing bookings, because each appointment froze its price and duration. `/catalog/offering`
  shows what this property offers and at what price (`property_service`, `can_manage_offering`).
  A new tax rate is a new row from its date and ends the old one there.
- **Rooms and closures** (`/rooms`, `/rooms/closures`). A room is retired rather than deleted. Closures
  of one room cannot overlap (the database's exclusion), and scheduling refuses a booking inside one.
- **Staff** (`/staff`). The operational profile is visible to everyone. The HR file (`/staff/{id}/hr`)
  is visible only to HR and to the person (`staff_record` in OpenFGA). A role is proposed by one
  administrator and approved by another (`can_approve_role`; the database refuses self-approval),
  and reaches OpenFGA through the outbox. A qualification for a licensed service needs a verified,
  unexpired credential of each type the service names (CON-003). Credential numbers are encrypted
  and shown as `•••• 1234`. The `workforce.credential-expiry` job expires lapsed credentials and the
  qualifications granted on them.
- **Roster** (`/roster`). Shifts are drafted and published by the scheduler, and published shifts
  cannot overlap. Anyone may request their own leave; a manager, never the requester, approves it.
  Scheduling reads published shifts and approved leave.
- **Stock** (`/inventory/*`). Every movement is a ledger entry, and the balance is updated in the same
  transaction under a row lock. An ordinary movement never takes stock below zero. Movements need an
  `Idempotency-Key`, and a retry is a replay. Transfers post a pair. Housekeeping moves linen Clean →
  Soiled → laundry → Clean, and losses are recorded. A count is approved by someone other than the
  counter, and its variance is posted against the stock at approval. A completed treatment consumes
  its service's supplies (`service_supply`) in its own savepoint, so bookkeeping never fails a
  treatment.
- **Governed settings** (`/settings`). A change is proposed with a reason and approved by a different
  person (`can_propose_configuration` / `can_approve_configuration`). Approval closes the value it
  replaces at its effective date; a future value waits (`core.setting-activation`). Known keys
  (`policy.deposit`, `policy.cancellation`, `messaging.quiet_hours`, `retention.*`) are shape-checked.
- **Seed.** Iris (inventory manager) and Hugo (HR & compliance) are new, and Sam is also the
  configuration approver. The seed adds stock locations, towels, robes, oil and a retail lotion with
  opening receipts.
- **Screens.** Staff (profile, HR file, roles, qualifications, credentials, roster), Inventory
  (balances, movements, counts, laundry) and Setup (catalogue and prices, rooms and closures,
  policies) run on the API.

**Commerce and payments (batch 5):**

- **Orders** (`/orders`). A cart is a Draft order. A line for a booking takes the price frozen on the
  booking, not today's catalogue, and tax from `catalog.tax_rule` (the property's rule over the
  tenant's). A Discount line is a comp and needs `can_approve_comp`. Placing the order (`If-Match`)
  numbers it (`ORD…`) and applies any deposits for its bookings. It is Paid once approved money
  covers the total, and the receipt number (`RCT…`) is issued then. Money is integer minor units.
- **Payments** (`/orders/{id}/payments`, `/appointments/{id}/deposit`) require an `Idempotency-Key`,
  and so does every call to the provider. A retried request is the same payment. When the provider
  does not confirm, the answer is `202 PAYMENT_OUTCOME_AMBIGUOUS` with the intent to query (BR-014).
  `POST /payment-intents/{id}/resolve` looks up the original and never charges again; the
  `commerce.ambiguous-payments` job does the same every minute. The provider is behind
  `IPaymentGateway` (DEC-010 is open). The development provider is deterministic by token:
  `tok_approve`, `tok_decline`, `tok_error`, and `tok_timeout` (unknown until a lookup). Card data never
  reaches SpMS, only the provider's token for it.
- **Deposits** follow the governed setting `policy.deposit` (`{"percent":50,"minimumMinor":2000}` in
  the seed; hot stone requires one). The arrivals list reports Pending or Settled. A no-show forfeits
  the deposit in the no-show's own transaction, through an `ISchedulingObserver`.
- **Refunds** (`/payment-transactions/{id}/refunds`) are requested at the desk and approved by finance
  (`can_refund`), never by the requester. The service checks this and so does the database's dual
  control. A refund cannot exceed what is left of the original sale.
- **Ownership** (DEC-001). Exactly one system owns payment at a property at an instant. With no
  explicit `core.capability_ownership` row, a Standalone property owns its own. A Marquee-integrated
  property with no decision answers `OWNERSHIP_AMBIGUOUS`. When Marquee owns payment the order is
  Delegated, SpMS records the `CreateCart` call it owes, and it takes no money.
- **Reconciliation** (`/reconciliation?date=`, finance: `spa.reconcile` plus `can_reconcile`) gives
  the property's day by tender, the unconfirmed payments, and the refunds waiting for approval.
- **Screens.** Checkout (`/app/checkout`) covers deposits, the bill, tips and comps, card or cash, a
  "waiting for the provider" state that hides Pay, and refund requests. Reconciliation runs on the
  API when `useRealApi` is set.

**Guests and intake (batch 4):**

- **Profiles** (`/guests`). Email and phone are searched through a keyed hash and never decrypted to
  search. Guests are shown by a privacy alias ("Ava R.") and a public queue id. The desk sees whether a
  guest is a minor, never the date of birth. Preferences are an allow-list of operational keys, so
  health information is refused there; it belongs to intake.
- **Merge** (`/guest-merge-cases`, IDN-001). A likely duplicate at creation becomes a candidate.
  `can_approve_guest_merge` (spa manager) decides, and the proposer never can. A merge moves the
  duplicate's contact points and records which ones, so a split moves exactly those back.
- **Delegation** (IDN-003) has explicit actions, properties, limit, visibility and expiry, with
  evidence. It becomes conditional OpenFGA tuples through the outbox, and expiry is a job.
- **Consent** evidence is encrypted and immutable (a database trigger); revocation is the only change.
- **Privacy requests** (IDN-006). The desk logs them; the tenant's privacy role
  (`can_handle_privacy_request`) verifies and fulfils them. Access/Export collects every module's
  part (`IGuestDataContributor`). Deletion empties the profile, destroys contact values, cancels open
  waitlist entries and redacts audit payloads through `spms_erasure`. Bookings are kept, and so are
  intake records, under health-record retention (an open decision on durations).
- **Intake** (SEC-008). The API role cannot read `intake.*` at all. The intake service switches the
  transaction to `spms_intake` for its statements. Answers and the provider summary are each sealed
  with a per-record data key wrapped by the key ring (`EnvelopeCipher`). The desk sees status only,
  through `scheduling.intake_status()`. The assigned provider sees the summary fields and
  acknowledges them, and the form then locks. The guest completes the form from a magic link.
- **Treatment notes** are encrypted, written by the assigned provider, and never edited. An amendment
  supersedes the note, once, by its author.

**Open decisions (not ours to close):**

- 144 points in the Open Decision Register
- DEC-010 payment provider
- DEC-011 pilot property
- KMS/key rotation
- break-glass rules (not modelled)
- jurisdiction retention durations
