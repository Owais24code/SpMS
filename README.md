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
| Front-end checks | `npm run build` · `npm run audit:contrast` |
| Backend | `cd backend && dotnet build Spms.sln -c Release && dotnet test` |
| API (dev) | `ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Spms.Api` |
| HTTP contract sweep | `node backend/tools/http-acceptance.mjs http://127.0.0.1:5199` (fresh instance only) |

On Windows, install npm dependencies from a local path, not a mounted or network share,
because npm's atomic renames fail on some mounts. Backend Postgres tests **skip silently**
unless `SPMS_TEST_CONNECTION` is set. CI sets it explicitly for that reason.

---

## Architecture

### Modular monolith

Everything deploys as one process with one database. It is organised as twelve modules, and
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
                                               ├─ visit ─────┐
                                               ├─ inventory ─┴─ commerce
                                               └─ messaging
core ── reporting   (facts are denormalised: no FKs outside core)
```

| Module | Owns |
|---|---|
| core | tenant, property, principal and identity links, capability ownership (DEC-001), configuration, audit, outbox/inbox, idempotency, retention, legal hold |
| catalog | services and versions, per-property offering, options, protocols, sellable items, prices, tax |
| resources | facilities, locations, rooms and equipment, schedules, maintenance, sanitation |
| workforce | staff, HR profile, Entra links, roles, qualifications (CON-003), credentials, documents, schedules, screening |
| guest | tenant-wide guest (IDN-005), contact points, household, relationships, merge cases, aliases, delegation (IDN-003), consent, privacy requests, magic links |
| scheduling | appointments, resource assignments (CON-001/002), lines, participants, status history, itineraries, holds, preflight proposals and conflicts, waitlist, turnaround |
| intake | forms, intake submissions, provider acknowledgements, treatment notes (restricted: SEC-008) |
| visit | visit, participants, appointment links, events, exceptions |
| inventory | items, variants, lots, balances, stock ledger, transfers, laundry, recipes, forecast, counts, product use |
| commerce | carts, orders, payment intents and transactions (provider-agnostic, DEC-010 open), refunds, deposits, receipts, Marquee delegation |
| messaging | templates and versions, reminder rules, scheduled messages, deliveries, suppression, inbound |
| reporting | report runs, schedules, fact stream |

**Backend layout.** Today it is still layer-per-project (`Spms.Domain`, `Spms.Infrastructure`,
`Spms.Infrastructure.Postgres`, `Spms.Api`). The next step is a `Spms.Host` composition root,
a `Spms.SharedKernel`, and one project per module. There will be **one EF Core `DbContext`
across all schemas**, with each module contributing its `IEntityTypeConfiguration`s. That
split waits on a .NET toolchain; see Status.

### Tenancy: three layers

1. **Authorization (OpenFGA).** Decides who may act on which object. See Authorization.
2. **Application.** EF Core `HasQueryFilter` on `tenant_id`/`property_id`, plus a
   `SaveChangesInterceptor` that stamps them. A new endpoint cannot forget to scope.
3. **Database (RLS).** Every table carrying `tenant_id` has row-level security **enabled and
   forced**, 123 of 123. Each request opens a transaction and calls
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

**Scope:** R1 in full. That is 123 tables in 12 schemas: 47 handoff tables kept as they were,
65 handoff stubs completed from the spec text, and 11 new.

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
| Partitioning | Monthly range partitions on `audit_event`, `event_outbox`, `inventory_ledger_entry`, `message_delivery` and `reporting_fact`. They are created ahead by `core.ensure_monthly_partitions`, and the DEFAULT partition must stay empty |
| Restricted data | Intake answers, treatment notes, contact values, credential numbers and consent evidence are `*_cipher bytea` + `key_version`: app-level envelope encryption with Key Vault-wrapped keys. Search uses HMAC `lookup_hash` columns |
| Cross-module FKs | Only along the DAG. For example, `appointment.visit_id` is gone and `visit.visit_appointment` links the other way |

### Hard rules the database enforces on its own

- **CON-002.** A room or piece of equipment cannot hold two bookings. This is a GiST
  exclusion on `appointment_resource_assignment` over `[starts_at, ends_at)`. Cancelling an
  appointment releases its assignments in the same statement.
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
- scheduling: CON-002, CON-001, same-property FK, version, release on cancel, CON-005
- append-only and redaction
- identity resolution and single-use magic links
- integrity: DEC-001, SEC-014, consent, IDN-003, derived money, hard conflicts, UUIDv7

---

## Authentication and authorization

| Layer | Question | Mechanism |
|---|---|---|
| Authentication, staff | Who is calling? | **Microsoft Entra ID**, JWT bearer (`scp`/`roles`). `(issuer, oid)` resolves to a `core.principal` through `core.resolve_principal()` |
| Authentication, guests | Who is calling? | **Own magic links** (SEC-010/011): short-lived, single-use and purpose-limited. Only the SHA-256 is stored, and `guest.resolve_magic_link()` consumes it atomically |
| Authentication, services | Who is calling? | Entra client credentials, linked through `core.service_identity` |
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

| Area | State |
|---|---|
| R1 schema design (123 tables) | Done; verified on PostgreSQL 16 |
| RLS, roles, scope functions | Done; verified |
| OpenFGA model, tests, hosting template | Done; verified (model tests, live smoke, bicep build) |
| Repository layout (`backend/`, `frontend/`) | Done |
| Backend scheduling slice (10 endpoints) | Working on its **own** V001–V003 schema (text ids, `public`) |
| Backend → modular monolith + EF Core on the new schema | **Not started: blocked on toolchain** |
| `IAccessDecider` port, OpenFGA adapter, endpoint filters, magic-link endpoints | **Not started: blocked on toolchain** |
| EF `HasQueryFilter`, scope interceptor, drift check | **Not started: blocked on toolchain** |

**Toolchain blocker:** the build environment's network policy blocks the .NET SDK and
NuGet (dot.net, builds.dotnet.microsoft.com, api.nuget.org), so no C# could be compiled or
tested. The C# work waits until those hosts are allowlisted or the SDK is installed on a
machine that can build. It is deliberately not written blind.

**Open decisions (not ours to close):**

- 144 points in the Open Decision Register
- DEC-010 payment provider
- DEC-011 pilot property
- KMS/key rotation
- break-glass rules (not modelled)
- jurisdiction retention durations
