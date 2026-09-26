using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Spms.SharedKernel;

/// <summary>
/// The process's own instruments (System.Diagnostics, no vendor SDK): the
/// host exposes them at /metrics in Prometheus text and any OpenTelemetry
/// collector can read the same Meter and ActivitySource. Tags are bounded
/// (route templates, outcomes, job names) — never an id, a name or a value.
/// </summary>
public static class Telemetry
{
    public const string Name = "Spms";

    public static readonly Meter Meter = new(Name, "1.0");
    public static readonly ActivitySource Activities = new(Name, "1.0");

    public static readonly Counter<long> HttpRequests = Meter.CreateCounter<long>("spms_http_requests", description: "HTTP requests by route template, method and status class.");
    public static readonly Histogram<double> HttpDuration = Meter.CreateHistogram<double>("spms_http_request_duration_seconds", "s", "HTTP request duration.");
    public static readonly Counter<long> JobChanges = Meter.CreateCounter<long>("spms_job_records_changed", description: "Records changed by housekeeping jobs.");
    public static readonly Counter<long> JobFailures = Meter.CreateCounter<long>("spms_job_failures", description: "Housekeeping job runs that failed at a property.");
    public static readonly Counter<long> Payments = Meter.CreateCounter<long>("spms_payments", description: "Payment and deposit attempts by outcome.");
    public static readonly Counter<long> Messages = Meter.CreateCounter<long>("spms_messages", description: "Guest messages dispatched by outcome.");
    public static readonly Counter<long> OutboxHandled = Meter.CreateCounter<long>("spms_outbox_events_handled", description: "Outbox events handled by handler and result.");
}
