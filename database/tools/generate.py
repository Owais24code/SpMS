#!/usr/bin/env python3
"""
Generates database/schema/*.sql and database/catalog.json from database/model/.

    python3 database/tools/generate.py          # write files
    python3 database/tools/generate.py --check  # fail if generated files are stale

The generated SQL is the reviewed reference schema. The EF Core model must
produce exactly this structure; CI compares the two (see README, Database).
"""
from __future__ import annotations
import importlib.util, json, pathlib, sys

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(HERE))
import dsl  # noqa: E402
from dsl import MASTER, AGGREGATE, CHILD, LEDGER, GLOBAL, TENANT, TENANT_OPT, PROPERTY  # noqa: E402

VERSIONED = (MASTER, AGGREGATE)


def load_models():
    for path in sorted((ROOT / "model").glob("*.py")):
        spec = importlib.util.spec_from_file_location(path.stem, path)
        spec.loader.exec_module(importlib.util.module_from_spec(spec))
    return dsl.REGISTRY


# --------------------------------------------------------------------------
# validation
# --------------------------------------------------------------------------
def closure(module: str) -> set[str]:
    seen, stack = set(), list(dsl.DEPENDS[module])
    while stack:
        m = stack.pop()
        if m not in seen:
            seen.add(m)
            stack.extend(dsl.DEPENDS[m])
    return seen


def resolve_fk(t, c, by_fq):
    parts = c.fk.split(".")
    target = by_fq.get(".".join(parts[:2]))
    if target is None:
        raise SystemExit(f"{t.fq}.{c.name}: FK target {c.fk} does not exist")
    target_col = parts[2] if len(parts) == 3 else target.pk_col
    return target, target_col


def validate(tables):
    by_fq, errors = {t.fq: t for t in tables}, []
    if len(by_fq) != len(tables):
        errors.append("duplicate table names")
    for t in tables:
        if t.schema not in dsl.DEPENDS:
            errors.append(f"{t.fq}: unknown module")
        names = [c.name for c in all_columns(t)]
        dup = {n for n in names if names.count(n) > 1}
        if dup:
            errors.append(f"{t.fq}: duplicate columns {sorted(dup)}")
        for c in t.cols:
            if not c.fk:
                continue
            target, _ = resolve_fk(t, c, by_fq)
            if target.schema != t.schema and target.schema not in closure(t.schema):
                errors.append(f"{t.fq}.{c.name} -> {target.fq}: violates module DAG "
                              f"({t.schema} may reference {sorted(closure(t.schema))})")
            if target.partition_by:
                errors.append(f"{t.fq}.{c.name} -> {target.fq}: FK into a partitioned table")
            if c.same_property and not (t.scope == PROPERTY and target.scope == PROPERTY):
                errors.append(f"{t.fq}.{c.name}: same_property needs both sides PROPERTY-scoped")
            if t.has_tenant and not target.has_tenant:
                pass  # tenant table -> global catalogue: plain FK
            elif not t.has_tenant and target.has_tenant:
                errors.append(f"{t.fq}.{c.name}: global table cannot reference tenant data")
    if errors:
        raise SystemExit("model errors:\n  " + "\n  ".join(errors))
    return by_fq


# --------------------------------------------------------------------------
# column assembly
# --------------------------------------------------------------------------
def standard_prefix(t):
    cols = [dsl.col(t.pk_col, t.pk_type, default="core.uuid_v7()" if t.pk_type == "uuid" else None)]
    if t.has_tenant:
        cols.append(dsl.col("tenant_id", "uuid", fk=None))
    if t.has_property:
        cols.append(dsl.col("property_id", "uuid", null=(t.scope == TENANT_OPT)))
    if t.key and t.kind in (MASTER, AGGREGATE):
        if t.key == "record_key":
            cols.append(dsl.col("record_key", "text", null=t.key_nullable,
                                doc="business key, unique within tenant/property"))
    return cols


def standard_suffix(t):
    cols = []
    if t.statuses:
        cols.append(dsl.col("status", "text", default=f"'{t.status_default or t.statuses[0]}'"))
    effective = t.effective if t.effective is not None else (t.kind == MASTER)
    if effective:
        cols += [dsl.col("effective_from", "timestamptz", default="now()"),
                 dsl.col("effective_to", "timestamptz", null=True)]
    source = t.source_cols if t.source_cols is not None else (t.kind in VERSIONED)
    if source:
        cols += [dsl.col("source_system", "text", default="'Spa'"),
                 dsl.col("source_key", "text", null=True)]
    if t.kind in VERSIONED:
        cols.append(dsl.col("version", "integer", default="1", check="version >= 1"))
    cols += [dsl.col(t.time_col if t.kind == LEDGER else "created_at", "timestamptz", default="now()"),
             dsl.col("created_by", "uuid", null=True)]
    if t.kind != LEDGER:
        cols += [dsl.col("updated_at", "timestamptz", default="now()"),
                 dsl.col("updated_by", "uuid", null=True)]
    cols.append(dsl.col("correlation_id", "text", null=True))
    return [c for c in cols if c.name not in t.omit]


def all_columns(t):
    return [c for c in standard_prefix(t) if c.name not in t.omit] + t.cols + standard_suffix(t)


def q(v):
    return "'" + v.replace("'", "''") + "'"


# --------------------------------------------------------------------------
# DDL
# --------------------------------------------------------------------------
def ddl_table(t, extra_uniques):
    lines, cons = [], []
    for c in all_columns(t):
        s = f"    {c.name:<30} {c.type}"
        if not c.null:
            s += " NOT NULL"
        if c.default is not None:
            s += f" DEFAULT {c.default}"
        lines.append(s)
        if c.check:
            cons.append(f"CONSTRAINT {short(t.name, c.name, 'ck')} CHECK ({c.check})")
    pk = f"{t.pk_col}, {t.partition_by}" if t.partition_by else t.pk_col
    cons.insert(0, f"CONSTRAINT {t.name}_pkey PRIMARY KEY ({pk})")
    if t.has_tenant and not t.partition_by:
        cons.append(f"CONSTRAINT {t.name}_tenant_identity_uq UNIQUE (tenant_id, {t.pk_col})")
    for u in sorted(extra_uniques.get(t.fq, [])):
        tag = "prop" if "property_id" in u else u.split(",")[-1].strip()
        cons.append(f"CONSTRAINT {short(t.name, tag, 'ref_uq')} UNIQUE ({u})")
    if t.key == "record_key" and t.kind in VERSIONED and not t.partition_by:
        scope = {GLOBAL: "", TENANT: "tenant_id, ", TENANT_OPT: "tenant_id, property_id, ",
                 PROPERTY: "tenant_id, property_id, "}[t.scope]
        cons.append(f"CONSTRAINT {t.name}_record_key_uq UNIQUE NULLS NOT DISTINCT ({scope}record_key)")
    if t.statuses:
        allowed = ", ".join(q(s) for s in t.statuses)
        cons.append(f"CONSTRAINT {t.name}_status_known CHECK (status IN ({allowed}))")
    effective = t.effective if t.effective is not None else (t.kind == MASTER)
    if effective:
        cons.append(f"CONSTRAINT {t.name}_effective_range_ck CHECK (effective_to IS NULL OR effective_to > effective_from)")
    for name, expr in t.checks:
        cons.append(f"CONSTRAINT {t.name}_{name} CHECK ({expr})")
    for name, colset in t.uniques:
        cons.append(f"CONSTRAINT {t.name}_{name} UNIQUE {colset}" if colset.strip().startswith(("NULLS", "("))
                    else f"CONSTRAINT {t.name}_{name} UNIQUE ({colset})")
    body = ",\n".join(lines + ["    " + c for c in cons])
    part = f" PARTITION BY RANGE ({t.partition_by})" if t.partition_by else ""
    out = [f"CREATE TABLE {t.fq} (\n{body}\n){part};"]
    if t.doc:
        out.append(f"COMMENT ON TABLE {t.fq} IS {q(t.doc + (' [' + t.spec + ']' if t.spec else ''))};")
    for c in all_columns(t):
        if c.doc:
            out.append(f"COMMENT ON COLUMN {t.fq}.{c.name} IS {q(c.doc)};")
    return "\n".join(out)


def short(table, column, suffix):
    name = f"{table}_{column}_{suffix}"
    return name if len(name) <= 63 else f"{table[:30]}_{column[:26]}_{suffix}"


def leading_column_sets(t):
    """Every leading-column prefix of the PK, uniques and declared indexes (for FK index coverage)."""
    import re
    lists = []
    if t.has_tenant and not t.partition_by:
        lists.append(["tenant_id", t.pk_col])
    for _, u in t.uniques:
        lists.append([c.strip() for c in u.replace("NULLS NOT DISTINCT", "").strip(" ()").split(",")])
    for ix in t.indexes:
        m = re.search(r"\bON\s+\S+\s+(?:USING\s+\w+\s+)?\(([^)]*)", ix)
        if m:
            lists.append([c.strip().split(" ")[0] for c in m.group(1).split(",")])
    for c in t.cols:
        if c.fk:
            lists.append(["tenant_id", "property_id", c.name] if c.same_property else ["tenant_id", c.name])
    out = set()
    for cols in lists:
        for i in range(1, len(cols) + 1):
            out.add(tuple(cols[:i]))
    return out


def ddl_fks(t, by_fq):
    out = []
    for c in t.cols:
        if not c.fk:
            continue
        target, tcol = resolve_fk(t, c, by_fq)
        if t.has_tenant and target.has_tenant:
            if c.same_property:
                local, remote = f"tenant_id, property_id, {c.name}", f"tenant_id, property_id, {tcol}"
            else:
                local, remote = f"tenant_id, {c.name}", f"tenant_id, {tcol}"
        else:
            local, remote = c.name, tcol
        out.append(f"ALTER TABLE {t.fq} ADD CONSTRAINT {short(t.name, c.name, 'fk')} "
                   f"FOREIGN KEY ({local}) REFERENCES {target.fq} ({remote}) ON DELETE {c.on_delete};")
        # every FK gets a supporting index unless it is the leading PK/unique column
        out.append(f"CREATE INDEX {short(t.name, c.name, 'ix')} ON {t.fq} ({local});")
    covered = leading_column_sets(t)
    if t.has_tenant and t.partition_by and ("tenant_id",) not in covered:
        out.append(f"CREATE INDEX {t.name}_tenant_ix ON {t.fq} (tenant_id);")
    if t.has_property and t.fq != "core.property" and ("tenant_id", "property_id") not in covered:
        out.append(f"CREATE INDEX {t.name}_scope_ix ON {t.fq} (tenant_id, property_id);")
    if t.has_tenant:
        out.append(f"ALTER TABLE {t.fq} ADD CONSTRAINT {t.name}_tenant_fk "
                   f"FOREIGN KEY (tenant_id) REFERENCES core.tenant (tenant_id);"
                   if t.fq != "core.tenant" else "")
    if t.has_property:
        out.append(f"ALTER TABLE {t.fq} ADD CONSTRAINT {t.name}_property_fk "
                   f"FOREIGN KEY (tenant_id, property_id) REFERENCES core.property (tenant_id, property_id);"
                   if t.fq != "core.property" else "")
    return [o for o in out if o]


def ddl_triggers(t):
    out = []
    if t.kind == LEDGER:
        out.append(f"CREATE TRIGGER {t.name}_append_only BEFORE UPDATE OR DELETE ON {t.fq} "
                   f"FOR EACH ROW EXECUTE FUNCTION core.forbid_mutation();")
    elif {"updated_at", "created_by"} <= {c.name for c in all_columns(t)}:
        fn = "core.touch_versioned" if t.kind in VERSIONED else "core.touch"
        out.append(f"CREATE TRIGGER {t.name}_touch BEFORE UPDATE ON {t.fq} "
                   f"FOR EACH ROW EXECUTE FUNCTION {fn}();")
    return out


def ddl_partitions(t):
    if not t.partition_by:
        return []
    return [f"DO $$ BEGIN PERFORM core.ensure_monthly_partitions('{t.fq}'::regclass, "
            f"(date_trunc('month', now() AT TIME ZONE 'UTC') - interval '1 month')::date, 4); END $$;",
            f"CREATE TABLE {t.schema}.{t.name}_default PARTITION OF {t.fq} DEFAULT;"]


def rls(t):
    if not t.has_tenant:
        return []
    tenant = "tenant_id = (SELECT core.current_tenant_id())"
    if t.fq == "core.tenant":
        expr = tenant
    elif t.scope == PROPERTY:
        expr = f"{tenant} AND property_id = ANY ((SELECT core.current_property_ids())::uuid[])"
    elif t.scope == TENANT_OPT:
        expr = f"{tenant} AND (property_id IS NULL OR property_id = ANY ((SELECT core.current_property_ids())::uuid[]))"
    else:
        expr = tenant
    return [f"ALTER TABLE {t.fq} ENABLE ROW LEVEL SECURITY;",
            f"ALTER TABLE {t.fq} FORCE ROW LEVEL SECURITY;",
            f"CREATE POLICY {t.name}_scope ON {t.fq} USING ({expr}) WITH CHECK ({expr});"]


def grants(t):
    if t.grants == "none":
        return []
    role = {"app": "spms_app", "intake": "spms_intake", "reporting": "spms_app"}[t.grants]
    if not t.has_tenant:
        privs = "SELECT"
    elif t.kind == LEDGER:
        privs = "SELECT, INSERT"
    else:
        privs = "SELECT, INSERT, UPDATE"
    out = [f"GRANT {privs} ON {t.fq} TO {role};"]
    if t.grants == "reporting":
        out.append(f"GRANT SELECT ON {t.fq} TO spms_reporting;")
    if t.has_tenant and t.schema != "intake":
        out.append(f"GRANT SELECT ON {t.fq} TO spms_erasure;")
    return out


# --------------------------------------------------------------------------
def main(check: bool):
    tables = load_models()
    by_fq = validate(tables)

    extra_uniques: dict[str, set[str]] = {}
    for t in tables:
        for c in t.cols:
            if c.fk and c.same_property:
                target, tcol = resolve_fk(t, c, by_fq)
                extra_uniques.setdefault(target.fq, set()).add(f"tenant_id, property_id, {tcol}")
            elif c.fk:
                target, tcol = resolve_fk(t, c, by_fq)
                if tcol != target.pk_col and target.has_tenant and t.has_tenant:
                    extra_uniques.setdefault(target.fq, set()).add(f"tenant_id, {tcol}")

    files: dict[str, str] = {"schema/000_roles.sql": (ROOT / "tools" / "roles.sql").read_text()}
    for i, module in enumerate(dsl.MODULE_ORDER, start=1):
        mts = [t for t in tables if t.schema == module]
        parts = [f"-- {i:02d} {module}: generated by database/tools/generate.py from database/model/. Do not edit.",
                 f"-- may reference: {', '.join(sorted(closure(module))) or '(nothing)'}",
                 "", f"CREATE SCHEMA IF NOT EXISTS {module} AUTHORIZATION spms_owner;", ""]
        if module == "core":
            parts.append((ROOT / "tools" / "foundation.sql").read_text())
        for t in mts:
            parts += [ddl_table(t, extra_uniques), ""]
        for t in mts:
            parts += ddl_fks(t, by_fq)
        parts.append("")
        for t in mts:
            parts += ddl_triggers(t) + ddl_partitions(t)
        for t in mts:
            if t.indexes:
                parts += [f"CREATE UNIQUE INDEX {ix[7:]};" if ix.startswith("UNIQUE ") else f"CREATE INDEX {ix};"
                          for ix in t.indexes]
            if t.extra_sql:
                parts += ["", t.extra_sql.strip()]
        files[f"schema/{i * 10:03d}_{module}.sql"] = "\n".join(parts) + "\n"

    sec = ["-- 900 security: row-level security and grants. Generated; do not edit.", ""]
    for m in dsl.MODULE_ORDER:
        role = "spms_intake" if m == "intake" else "spms_app"
        sec.append(f"GRANT USAGE ON SCHEMA {m} TO {role}, spms_erasure;")
    sec.append("GRANT USAGE ON SCHEMA reporting TO spms_reporting;")
    sec.append("GRANT USAGE ON SCHEMA core, guest, catalog, workforce, scheduling TO spms_intake;")
    sec.append("")
    for t in tables:
        sec += rls(t)
    sec.append("")
    for t in tables:
        sec += grants(t)
    sec.append((ROOT / "tools" / "security_tail.sql").read_text())
    files["schema/900_security.sql"] = "\n".join(sec) + "\n"

    catalog = {
        "generated_by": "database/tools/generate.py",
        "modules": {m: {"may_reference": sorted(closure(m))} for m in dsl.MODULE_ORDER},
        "tables": [{
            "table": t.fq, "kind": t.kind, "scope": t.scope, "handoff": t.handoff,
            "spec": t.spec, "doc": t.doc, "partitioned_by": t.partition_by,
            "statuses": t.statuses,
            "dropped_handoff_columns": [{"column": a, "reason": b} for a, b in t.dropped],
            "columns": [{"name": c.name, "type": c.type, "nullable": c.null,
                         "fk": c.fk, "restricted": c.restricted, "doc": c.doc}
                        for c in all_columns(t)],
        } for t in tables],
    }
    files["catalog.json"] = json.dumps(catalog, indent=1) + "\n"

    stale = []
    for rel, text in files.items():
        path = ROOT / rel
        if check:
            if not path.exists() or path.read_text() != text:
                stale.append(rel)
        else:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text)
    if check and stale:
        raise SystemExit("generated files are stale: " + ", ".join(stale))
    print(f"{len(tables)} tables in {len(dsl.MODULE_ORDER)} modules"
          + (" - up to date" if check else f" - wrote {len(files)} files"))


if __name__ == "__main__":
    main("--check" in sys.argv)
