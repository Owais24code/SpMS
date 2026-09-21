import re, sys
p = sys.argv[1]
s = open(p, encoding='utf-8').read()

s = s.replace(
"Status as of 21 September 2026.",
"Status as of 21 September 2026 (backend slice added).")

s = s.replace("""**Scope for this plan: R1 Pilot MVP front end.**""",
"""**Scope for this plan: R1 Pilot MVP, front end and the scheduling API slice.**""")

s = s.replace("""| Deploy config | Azure SWA + IIS rewrites for deep links |""",
"""| Deploy config | Azure SWA + IIS rewrites for deep links |
| **Scheduling API slice** | **ASP.NET Core 8, zero external packages, 10 endpoints** |
| **Preflight/commit flow** | **SCH-020 / GUI-003, with re-validation at commit** |
| **Conflict register** | **CON-001..005 evaluated; soft/hard, reasons audited** |
| **Optimistic concurrency** | **RowVersion / ETag / If-Match / 412, wildcard refused** |
| **Idempotency** | **Claim-then-complete, scoped tenant+property+operation+route** |
| **Audit trail** | **Append-only, before/after hashes, tenant+property scoped** |""")

s = s.replace("""**Automated verification in place:** contrast audit (44 pairs, both themes,
fails CI on regression) and an interaction sweep (14 screens, 86 controls,
asserts an observable effect per control). Latest run: **zero dead controls,
zero console errors.**""",
"""**Automated verification in place**

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
nothing about any of it.""")

s = s.replace("""| API (.NET) | **~3%** | Domain constants only; no host, no controllers |""",
"""| API (.NET) — scheduling slice | **~45%** | 10 endpoints, tested; no persistence, no auth |
| API (.NET) — R1 surface overall | **~12%** | Scheduling only; commerce, guest, device absent |""")

s = s.replace("""| **R1 overall** | **~15%** | Front end is the only meaningful progress |""",
"""| **R1 overall** | **~22%** | One vertical slice now reaches from screen to rule |""")

s = s.replace("""The front-end figure is high and the overall figure is low because everything
built so far sits above an API that does not exist.""",
"""The overall figure moved 15% → 22%, not further, for two reasons worth
stating plainly. The API has no database: every appointment lives in process
memory and is gone on restart, so nothing built on it is durable. And it has
no authentication outside Development, where identity arrives in a header
that any caller could forge — so the slice cannot be deployed anywhere, by
design, until JWT bearer is wired.

What did change is the shape of the progress. Before this increment the front
end sat above nothing. One slice — reschedule an appointment, with conflict
detection, an override reason, optimistic concurrency and an audit row — now
runs end to end against real server rules.""")

# Replace the sequenced plan section
old_plan_start = s.index("## 4. Sequenced plan")
old_plan_end = s.index("## 5. Risks")
s = s[:old_plan_start] + """## 4. Sequenced plan

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

""" + s[old_plan_end:]

s = s.replace("""| No tests | Add the harness before the API lands, not after |""",
"""| No tests | Closed for this slice: 82 unit + 73 HTTP cases, each blocker carrying a regression test |
| In-memory store mistaken for persistence | `/health/ready` names the store; the README leads with it |
| The property gate mistaken for a real constraint | Documented at the gate itself as single-node only, with the SQL that replaces it |
| Header auth reaching a deployed environment | Structurally impossible: outside Development the headers are ignored and every scoped endpoint answers 401 |""")

open(p, 'w', encoding='utf-8').write(s)
print("patched")
