# SpMS — delivery plan

Status as of 21 September 2026 (backend slice added). Measured against the AARFID Spa handoff
package **v2.13.26** (prepared 16 Sep 2026), whose own manifest records
`production_approved: false` and 27 open work items.

---

## 1. What "done" means here

The handoff is explicit, and it is the rule this plan is written against:

> No row becomes ACCEPTED because code or a table exists. Closure requires
> requirement → database → business rule → API → screen/device → permission →
> audit → automated test → acceptance test evidence.

So a screen that renders is not a delivered feature. Anything below marked
**Shipped** means it renders, behaves, and is committed — it does **not** mean
accepted, because seven of those eight links do not exist yet.

**Scope for this plan: R1 Pilot MVP, front end and the scheduling API slice.** R1 is defined in
SPECIFICATION_v2.12.md §53.1 as *"run one property's daily spa operation end to
end"*. R0 foundation, R2 and R3 are out of scope here.

---

## 2. Where we actually are

### Shipped and verified

| Area | State |
|---|---|
| Design token foundation | Brand-derived, light + dark, 44 contrast pairs automated |
| Public marketing site | 6 pages, responsive, WCAG-checked |
| Workspace shell | Left rail with scope filtering, top bar, search, account menu |
| 14 workspace screens | All render, all code-split, zero console errors |
| Auth | Sign-in, route guards, scope guard, forbidden page, sign-out |
| Interaction layer | Toasts, confirm dialog, focus trap, 10 designed states |
| Client store | Signal-backed, simulated latency, stale/ambiguous results, audit ring |
| Contract alignment | Real scopes, error codes, CON register, state machines |
| Deploy config | Azure SWA + IIS rewrites for deep links |
| **Scheduling API slice** | **ASP.NET Core 8, zero external packages, 10 endpoints** |
| **Preflight/commit flow** | **SCH-020 / GUI-003, with re-validation at commit** |
| **Conflict register** | **CON-001..005 evaluated; soft/hard, reasons audited** |
| **Optimistic concurrency** | **RowVersion / ETag / If-Match / 412, wildcard refused** |
| **Idempotency** | **Claim-then-complete, scoped tenant+property+operation+route** |
| **Audit trail** | **Append-only, before/after hashes, tenant+property scoped** |

**Automated verification in place**

| Suite | Scope | Latest run |
|---|---|---|
| Contrast audit | 44 pairs, both themes | 44/44 |
| Interaction sweep | 14 screens, 86 controls | 0 dead, 0 console errors |
| Keyboard suite | Schedule move path | 14/14 |
| Domain unit tests | Rules, stores, API guards | **82/82** |
| HTTP acceptance sweep | Live wire contract | **73/73** |

The two new suites are the ones that matter for this increment: they exist
because three independent role reviews — QA, architecture, senior code
review — each found the same disqualifying defect, and a passing build said
nothing about any of it.

### Honest completion estimate

| Layer | Complete | Note |
|---|---:|---|
| R1 front end — screens and interaction | **~55%** | Renders and behaves; no server |
| R1 front end — spec conformance | ~30% | Contracts aligned; preflight/offline/undo absent |
| API (.NET) — scheduling slice | **~45%** | 10 endpoints, tested; no persistence, no auth |
| API (.NET) — R1 surface overall | **~12%** | Scheduling only; commerce, guest, device absent |
| Database | 0% | 284 tables specified, none built here |
| Integrations | 0% | PMS, POS, Gantner, Ojmar all uncertified |
| **R1 overall** | **~22%** | One vertical slice now reaches from screen to rule |

The overall figure moved 15% → 22%, not further, for two reasons worth
stating plainly. The API has no database: every appointment lives in process
memory and is gone on restart, so nothing built on it is durable. And it has
no authentication outside Development, where identity arrives in a header
that any caller could forge — so the slice cannot be deployed anywhere, by
design, until JWT bearer is wired.

What did change is the shape of the progress. Before this increment the front
end sat above nothing. One slice — reschedule an appointment, with conflict
detection, an override reason, optimistic concurrency and an audit row — now
runs end to end against real server rules.

---

## 3. What is left

### Blocking R1 — front end

1. **Preflight token flow** (SCH-020). `POST /schedule/preflight` returns a
   token; commit must quote it. Drag must create a *proposal* the operator
   confirms, not a mutation. Currently the drawer commits directly.
2. **Undo window** (CON-006). Configurable period, or an explicit compensating
   reschedule when downstream action has occurred. Not started.
3. **Bulk move** (CON-007), all-or-nothing or reviewed-partial.
4. **Server-published operating mode** (OFF-001..006). The client must enforce
   the mode the server publishes. Treatments currently fakes offline locally.
5. **Keyboard parity for drag** (GUI-004). Drag exists; the keyboard Move
   command does not.
6. **Universal search** (UX-002) resolving confirmation number, phone, email,
   room, folio, member, credential UID — currently jumps between screens only.
7. **Field-level annotation** (UX-013/014) for every screen. None produced.
8. **Test harness.** None. vitest was dropped over an npm resolver crash.

### Blocking R1 — everything else

9. **API host.** No ASP.NET project exists. 17 contract endpoints plus an
   80-command runtime bus are specified.
10. **Database.** 284 tables, 4,910 columns, 29 candidate migrations.
11. **Identity provider.** Sign-in is a role picker against localStorage.
12. **Integration certification** — PMS/POS, Gantner, Ojmar, SeQure.

### Not ours to close

144 open decision points in the Open Decision Register, and DEC-010 payment
provider, DEC-011 pilot property, DEC-012 quiet device remain unanswered.
Several front-end behaviours cannot be finalised until those land.

---

## 4. Sequenced plan

### Next — make the API durable

In this order, because each one is the prerequisite for the next.

1. **Unit of work + EF Core against PostgreSQL.** The aggregate write and the
   audit row are currently separate operations, so a crash between them loses
   the audit row. One transaction must span the write, the audit insert and
   the preflight-token consumption, with idempotency deliberately outside it.
2. **The exclusion constraint.** `EXCLUDE USING gist (property_id WITH =,
   room_id WITH =, tstzrange(start_utc, end_utc) WITH &&) WHERE (status NOT IN
   ('Cancelled','NoShow'))`, translated back to CON-002 on violation. Until it
   exists, a per-property in-process gate is the only thing enforcing the
   room-overlap invariant, and it does not survive a second instance.
3. **Entra ID / JWT bearer** issuing the real `spa.*` scopes, with the
   Development header handler behind the same abstraction rather than a static
   flag.
4. **Tenancy in the model, not the call site.** `HasQueryFilter` plus a
   `SaveChangesInterceptor`, so a new endpoint that forgets to scope cannot
   leak. Today it is two adjacent string parameters threaded by hand.

### Then — connect the front end

- Point WorkspaceStore at the real API behind a flag. Client/server drift to
  reconcile first: the client's `MoveProposal` carries `laneName`/`startPct`,
  has no `requiresReason`, and uses `expiresAtMs` where the server sends
  `expiresUtc`; status display strings differ from the enum names.
- `PREFLIGHT_EXPIRED`, `SOFT_CONFLICT_APPROVAL_REQUIRED` and the new
  board-changed refusal all need operator-facing handling.
- Undo window as a compensating reschedule through the same endpoint.
- Event outbox, so a second operator's board learns of a change at all.

### Then — the rest of R1

- Operating mode (OFF-001..006) gating every screen's offline state.
- Guest, commerce and device endpoints.
- Field-annotation pass, then acceptance evidence against the golden guest
  journey.

### Not in this increment, deliberately

CON-006 and CON-007 are declared and never emitted; so are
`OWNERSHIP_AMBIGUOUS`, `PAYMENT_OUTCOME_AMBIGUOUS`, `RATE_LIMITED` and
`OFFLINE_ACTION_BLOCKED`. They mirror the published catalogue and are listed
as reserved so the front end does not build handling for paths that cannot
occur yet. CON-005 is also property-local while the rule is tenant-wide — a
guest booked at two of a tenant's properties at once produces no conflict.
That needs a decision, not code.

---

## 5. Risks

| Risk | Mitigation |
|---|---|
| Front end drifts from an API that does not exist yet | Contracts are pinned in `core/models/contract.ts` and store method signatures already match |
| "Looks done" mistaken for done | The sweep counts behaviour, not markup; this document states the denominator |
| 144 open decisions stall build | Sequence API and database work, which almost none of them block |
| No tests | Closed for this slice: 82 unit + 73 HTTP cases, each blocker carrying a regression test |
| In-memory store mistaken for persistence | `/health/ready` names the store; the README leads with it |
| The property gate mistaken for a real constraint | Documented at the gate itself as single-node only, with the SQL that replaces it |
| Header auth reaching a deployed environment | Structurally impossible: outside Development the headers are ignored and every scoped endpoint answers 401 |
