"""
Table-definition DSL for the SpMS R1 schema.

The model files in database/model/ describe every table once. generate.py turns
that description into DDL, row-level security, grants and a machine-readable
catalogue. Conventions are applied here, in one place, so no table can quietly
opt out of tenancy, RLS or the audit columns.

Kinds decide the standard columns a table carries ("columns per entity"):

  MASTER     configuration / reference data, effective-dated, versioned
  AGGREGATE  operational records changed through commands, versioned
  CHILD      rows owned by an aggregate and changed with it (the aggregate
             carries the version)
  LEDGER     append-only facts; UPDATE and DELETE raise

Scopes decide tenancy columns and RLS:

  GLOBAL     no tenant (a shared reference catalogue)
  TENANT     tenant_id only (tenant-wide records, e.g. a guest - IDN-005)
  TENANT_OPT tenant_id + nullable property_id (NULL = applies tenant-wide)
  PROPERTY   tenant_id + property_id, both required
"""
from __future__ import annotations
from dataclasses import dataclass, field

MASTER, AGGREGATE, CHILD, LEDGER = "master", "aggregate", "child", "ledger"
GLOBAL, TENANT, TENANT_OPT, PROPERTY = "global", "tenant", "tenant_opt", "property"

# Module dependency DAG. A module may hold foreign keys only into the modules
# listed for it (transitively closed below). Upward or sideways references are
# a generator error, not a code-review comment.
DEPENDS = {
    "core":       [],
    "catalog":    ["core"],
    "resources":  ["core", "catalog"],
    "workforce":  ["core", "catalog"],
    "guest":      ["core"],
    "scheduling": ["core", "catalog", "resources", "workforce", "guest"],
    "intake":     ["core", "catalog", "guest", "workforce", "scheduling"],
    "visit":      ["core", "guest", "workforce", "resources", "scheduling"],
    "inventory":  ["core", "catalog", "resources", "scheduling"],
    "commerce":   ["core", "catalog", "resources", "guest", "scheduling", "visit", "inventory"],
    "messaging":  ["core", "catalog", "guest", "scheduling"],
    "reporting":  ["core"],
}

MODULE_ORDER = ["core", "catalog", "resources", "workforce", "guest", "scheduling",
                "intake", "visit", "inventory", "commerce", "messaging", "reporting"]


@dataclass
class Col:
    name: str
    type: str
    null: bool = False
    default: str | None = None
    fk: str | None = None            # "schema.table" (tenant-composite) or "schema.table.column"
    same_property: bool = False       # FK also pins property_id (target must be PROPERTY-scoped)
    on_delete: str = "RESTRICT"
    check: str | None = None          # column-level CHECK expression
    restricted: bool = False          # carries restricted/encrypted content (SEC-008)
    doc: str = ""


def col(name, type, null=False, default=None, fk=None, same_property=False,
        on_delete="RESTRICT", check=None, restricted=False, doc=""):
    return Col(name, type, null, default, fk, same_property, on_delete, check, restricted, doc)


@dataclass
class Table:
    schema: str
    name: str
    kind: str
    scope: str
    cols: list[Col]
    pk: str | None = None             # defaults to <name>_id
    pk_type: str = "uuid"
    time_col: str = "created_at"      # LEDGER: name of the record-time column (e.g. occurred_at)
    omit: list[str] = field(default_factory=list)   # standard columns this entity does not have
    key: str | None = "record_key"    # business key column; None = no business key
    key_nullable: bool = False
    statuses: list[str] | None = None
    status_default: str | None = None
    effective: bool | None = None     # effective_from/to; default True for MASTER
    checks: list[tuple[str, str]] = field(default_factory=list)
    uniques: list[tuple[str, str]] = field(default_factory=list)   # (name, "cols")
    indexes: list[str] = field(default_factory=list)               # raw CREATE INDEX bodies
    partition_by: str | None = None   # column for monthly RANGE partitioning
    source_cols: bool | None = None   # source_system/source_key; default True for MASTER/AGGREGATE
    extra_sql: str = ""
    grants: str = "app"               # app | intake | reporting | none
    spec: str = ""
    doc: str = ""
    handoff: str = "kept"             # kept | completed | new | renamed:<old>
    dropped: list[tuple[str, str]] = field(default_factory=list)  # (handoff column, reason)

    @property
    def pk_col(self) -> str:
        return self.pk or f"{self.name}_id"

    @property
    def fq(self) -> str:
        return f"{self.schema}.{self.name}"

    @property
    def has_tenant(self) -> bool:
        return self.scope != GLOBAL

    @property
    def has_property(self) -> bool:
        return self.scope in (TENANT_OPT, PROPERTY)


REGISTRY: list[Table] = []


def table(schema, name, kind, scope, cols, **kw) -> Table:
    t = Table(schema, name, kind, scope, cols, **kw)
    REGISTRY.append(t)
    return t


def money(prefix: str, null: bool = False, doc: str = "") -> list[Col]:
    """Settled money: integer minor units + ISO currency (spec 'Six decimal places')."""
    return [col(f"{prefix}_minor", "bigint", null=null, doc=doc or f"{prefix} in integer minor units"),]


CURRENCY = lambda null=False: col("currency_code", "char(3)", null=null,
                                  check="currency_code ~ '^[A-Z]{3}$'")
QTY = "numeric(18,6)"
RATE = "numeric(9,6)"
HASH = "char(64)"
