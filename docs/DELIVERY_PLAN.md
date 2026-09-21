# SpMS — delivery plan

Status as of 21 September 2026. Measured against the AARFID Spa handoff
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

**Scope for this plan: R1 Pilot MVP front end.** R1 is defined in
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

**Automated verification in place:** contrast audit (44 pairs, both themes,
fails CI on regression) and an interaction sweep (14 screens, 86 controls,
asserts an observable effect per control). Latest run: **zero dead controls,
zero console errors.**

### Honest completion estimate

| Layer | Complete | Note |
|---|---:|---|
| R1 front end — screens and interaction | **~55%** | Renders and behaves; no server |
| R1 front end — spec conformance | ~30% | Contracts aligned; preflight/offline/undo absent |
| API (.NET) | **~3%** | Domain constants only; no host, no controllers |
| Database | 0% | 284 tables specified, none built here |
| Integrations | 0% | PMS, POS, Gantner, Ojmar all uncertified |
| **R1 overall** | **~15%** | Front end is the only meaningful progress |

The front-end figure is high and the overall figure is low because everything
built so far sits above an API that does not exist.

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

### Next session — finish the front-end slice
- Preflight token flow on Schedule, including PREFLIGHT_EXPIRED handling
- Undo window with compensating reschedule
- Keyboard Move parity for drag
- Operating-mode service driving every screen's offline state
- Re-run both audits

### Then — make it real
- ASP.NET host, `/availability`, `/appointments`, `/schedule/preflight`
- EF Core against the first migration slice (appointment, guest, staff,
  service, capability_ownership)
- Replace WorkspaceStore's method bodies with HTTP; the `WriteResult` union
  already mirrors the API's `OptimisticResult`, so call sites do not change
- Idempotency-Key and If-Match on every write
- Entra ID sign-in issuing the real `spa.*` scopes

### Then — evidence
- Test harness, then the field-annotation pass, then acceptance evidence
  against the golden guest journey

---

## 5. Risks

| Risk | Mitigation |
|---|---|
| Front end drifts from an API that does not exist yet | Contracts are pinned in `core/models/contract.ts` and store method signatures already match |
| "Looks done" mistaken for done | The sweep counts behaviour, not markup; this document states the denominator |
| 144 open decisions stall build | Sequence API and database work, which almost none of them block |
| No tests | Add the harness before the API lands, not after |
