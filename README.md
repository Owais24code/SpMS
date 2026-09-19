# AARFID SpMS

Spa Management System — marketing surface and application foundation.

Built against the **UX Field Specification v1.1** (UX-001 … UX-012) and the
**UX Prototype and Device Acceptance Plan**, using the AARFID Branding Guidelines.

```
SpMS/
  web/     Angular 22 — marketing surface + shared design-token layer
  api/     .NET 10 — domain foundation (permissions, codes, concurrency)
```

## Getting started

```bash
cd web
npm install
npm start            # http://localhost:4200
npm run build        # production bundle
npm run audit:contrast   # WCAG 2.2 AA token audit — exits non-zero on failure
```

> Install dependencies on Windows directly, not through a mounted/network path —
> npm's atomic renames fail on some mounts.

## Design token layer

`web/src/styles/` is the shared foundation. Everything else consumes it.

| File | Contains |
|---|---|
| `tokens.scss` | Brand primitives, ramps, semantic colors, type, space, motion |
| `themes.scss` | Light/dark semantic mapping — components use **only** these |
| `base.scss` | Reset, focus, containers, glass, buttons |

Components never reference a ramp value (`--blue-500`) directly; they use the
semantic token (`--bg-accent`). That is what makes the dark theme a single
mapping change rather than a sweep through every stylesheet.

### Documented brand deviations

The brand guide is a marketing identity and needed three product extensions.
Each is deliberate and recorded here so it can be overruled knowingly.

1. **Semantic palette added.** The guide defines two primaries and two
   secondaries — no success, warning, danger or info. The UX spec requires
   success / warning / blocked / error / stale / offline states on every
   screen. Semantic ramps were derived chroma-matched to the brand.

2. **Cool Gray 4C is not used for borders.** `#bcbec0` measures **1.86:1** on
   white, below the 3:1 WCAG 2.2 requires for borders, focus rings and icon
   shapes. It is retained as `--surface-gray-brand` for large decorative fills
   only; `--border-default` uses `#7e8791` (3.64:1).

3. **All-caps headings scoped, not global.** The guide mandates ALL CAPS
   headings in the lighter blue. All-caps degrades readability and
   screen-reader pronunciation, and the acceptance plan requires testing with
   long localized labels. All-caps is applied via `.eyebrow` to short labels
   only; headings use sentence case.

A fourth addition: `--font-numeric` provides tabular figures for schedule times
and money columns, which Arial's proportional figures make hard to scan.

### Glassmorphism scope

Glass is **marketing surfaces only**, per decision. Operational screens stay
solid and high-contrast. Glass tokens carry an opacity floor that keeps text
above 4.5:1, and every `.glass` rule falls back to a solid surface where
`backdrop-filter` is unsupported — contrast never depends on the blur landing.

## Accessibility

`npm run audit:contrast` checks 44 token pairs across both themes against
WCAG 2.2 (4.5:1 text, 3:1 non-text) and fails the build on regression. It is
the design-system guard, not a substitute for the manual critical-journey
evidence the acceptance plan requires.

Also in place: skip link, visible focus on every interactive element, 44px
minimum targets, `prefers-reduced-motion` honoured, `inert` on closed panels,
form errors associated via `aria-describedby` with focus moved to the first
field in error.

## API foundation

`api/src/Spms.Domain` holds the parts that cannot be retrofitted later:

- **`Permissions/SpmsScopes.cs`** — the full scope catalogue extracted from the
  UX spec. `Restricted` marks the scopes that additionally require relationship
  and declared purpose, not just possession of the scope.
- **`Errors/SpmsCode.cs`** — stable `SPMS-<area>-<nnn>` codes. Append-only,
  never reused; these appear in audit records and analytics.
  `ConflictSeverity` separates soft (overridable) from hard (never overridable).
- **`Concurrency/OptimisticResult.cs`** — a stale result carries the rejected
  payload *and* current server state, so the client can preserve the user's
  unsaved work as the spec requires, rather than returning a bare 409.

## Known scope decisions

Three open items from the specification review remain unresolved and are
carried here rather than silently assumed:

1. Prototype journey scope — the field spec names 7 critical journeys, the
   acceptance plan names 9, the readiness register implies 12.
2. Quiet notification (UX-009) is in the field spec's journey list but dropped
   from the acceptance plan's.
3. Kiosk is a certified surface with no UX-### screen, and its
   abandoned-data-purge requirement conflicts with preserve-unsaved-work.
