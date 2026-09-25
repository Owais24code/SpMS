"""reporting: report runs, schedules and the append-only fact stream (§Reporting and metric guide).

Facts are denormalised on purpose (no FKs outside core): a report must reproduce the numbers
it showed, whatever happens to the operational rows later. Guest erasure de-identifies them.
"""
from dsl import *

S = "reporting"

table(S, "report_run", AGGREGATE, TENANT_OPT, key=None, source_cols=False, grants="reporting",
      spec="RPT21315/RPT21316 saved results and canonical hashing",
      statuses=["Queued", "Running", "Succeeded", "Failed", "Expired"],
      cols=[
          col("report_id", "text"),
          col("definition_version", "text"),
          col("definition_sha256", HASH),
          col("filters", "jsonb", default="'{}'"),
          col("as_of", "timestamptz"),
          col("source_watermark", "timestamptz", null=True),
          col("result_json", "jsonb", null=True),
          col("result_sha256", HASH, null=True),
          col("error_code", "text", null=True),
          col("started_at", "timestamptz", null=True),
          col("completed_at", "timestamptz", null=True),
          col("expires_at", "timestamptz"),
          col("is_closed_snapshot", "boolean", default="false"),
      ],
      checks=[("succeeded_complete", "status <> 'Succeeded' OR (result_sha256 IS NOT NULL AND completed_at IS NOT NULL)")],
      dropped=[("state", "status"), ("requested_by", "created_by")])

table(S, "report_schedule", MASTER, TENANT_OPT, key=None, source_cols=False, grants="reporting",
      statuses=["Active", "Paused", "Retired"],
      cols=[
          col("report_id", "text"),
          col("owner_principal_id", "uuid", fk="core.principal"),
          col("filters", "jsonb", default="'{}'"),
          col("timezone", "text"),
          col("local_hour", "smallint", check="local_hour BETWEEN 0 AND 23"),
          col("local_minute", "smallint", check="local_minute BETWEEN 0 AND 59"),
          col("next_run_at", "timestamptz"),
          col("last_run_id", "uuid", null=True, fk="reporting.report_run"),
      ],
      dropped=[("owner_id", "owner_principal_id"), ("enabled", "status")])

table(S, "reporting_fact", LEDGER, TENANT_OPT, time_col="occurred_at", partition_by="occurred_at", grants="reporting",
      spec="REP2139 (13 internal families source-wired)",
      cols=[
          col("fact_family", "text"),
          col("metric_key", "text"),
          col("event_type", "text"),
          col("source_id", "text"),
          col("source_version", "integer"),
          col("evidence_kind", "text", default="'Operational'"),
          col("staff_id", "uuid", null=True),
          col("guest_id", "uuid", null=True),
          col("visit_id", "uuid", null=True),
          col("service_id", "uuid", null=True),
          col("inventory_item_variant_id", "uuid", null=True),
          col("location_id", "uuid", null=True),
          col("channel", "text", default="'Direct'"),
          col("member_plan", "text", null=True),
          col("fact_status", "text", null=True),
          col("quantity", "numeric(24,6)", default="0"),
          col("amount_minor", "bigint", default="0"),
          col("expected_minor", "bigint", null=True),
          col("denominator", "numeric(24,6)", default="0"),
          CURRENCY(null=True),
      ],
      indexes=["reporting_fact_family_ix ON reporting.reporting_fact (tenant_id, fact_family, occurred_at)",
               "reporting_fact_source_ix ON reporting.reporting_fact (tenant_id, source_id, source_version)"],
      dropped=[("received_at", "created_at is not needed: occurred_at + the partition"), ("group_id", "groups are R3"),
               ("status", "fact_status (a fact's reported state, not a lifecycle)"), ("currency", "currency_code")])
