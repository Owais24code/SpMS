"""reporting: saved report runs (RPT21315/RPT21316).

R1 operational reports are queries over the operational tables (and views), not a separate fact
store. A run keeps its canonical result and hash so a report can reproduce what it showed.
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
          col("result_json", "jsonb", null=True),
          col("result_sha256", HASH, null=True),
          col("error_code", "text", null=True),
          col("started_at", "timestamptz", null=True),
          col("completed_at", "timestamptz", null=True),
          col("expires_at", "timestamptz"),
          col("is_closed_snapshot", "boolean", default="false"),
      ],
      checks=[("succeeded_complete", "status <> 'Succeeded' OR (result_sha256 IS NOT NULL AND completed_at IS NOT NULL)")],
      dropped=[("report_schedule (table)", "a scheduled task in the app host"),
               ("reporting_fact (table)", "R1 reports read operational tables; a fact store is an R2 scale concern")])
