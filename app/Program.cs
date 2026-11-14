// transactions-api: an ASP.NET Core (.NET 10) service that handles HTTP transactions and records
// metrics with OpenTelemetry. Metrics are aggregated IN MEMORY by the OTel SDK and pushed via OTLP
// on a schedule (PeriodicExportingMetricReader, background thread) to a Grafana Alloy agent. The
// request path only does an in-memory Counter.Add / Histogram.Record, so it never waits on the
// metrics pipeline. If Alloy is slow or down, exports fail in the background and requests stay fast.
//
// Labels on http_transactions_total: consume_type, state, product, pod (+ response_time = the bug).
// "pod" simulates ~200 pods: each real replica emits VIRTUAL_PODS pod names.
//
// Configuration (environment variables):
//   CARDINALITY_MODE        bounded | unbounded. Unbounded adds response_time (float) as a LABEL.
//   VIRTUAL_PODS            simulated pod names per real replica             (default 50)
//   POD_CHURN_SECONDS       if >0, replace 20% of pod names this often (rollouts)  (default 0 = off)
//   OTEL_CARDINALITY_LIMIT  max series per metric stream in the SDK (default 200000; SDK default 2000)
//   OTEL_ALLOWED_TAGS       comma list; if set, transaction metrics keep only these tag keys (app fix)
//   OTEL_EXPORTER_OTLP_ENDPOINT / OTEL_EXPORTER_OTLP_PROTOCOL / OTEL_METRIC_EXPORT_INTERVAL
//                           standard OTel SDK settings (set in the Deployment)

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

var mode = new CardinalityMode(cfg["CARDINALITY_MODE"] ?? "bounded");
var cardinalityLimit = cfg.GetValue("OTEL_CARDINALITY_LIMIT", 200_000);
var allowedTags = (cfg["OTEL_ALLOWED_TAGS"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var realPodName = cfg["POD_NAME"] ?? Environment.MachineName;

builder.Services.AddSingleton(mode);
builder.Services.AddSingleton(new VirtualPods(
    realPodName,
    cfg.GetValue("VIRTUAL_PODS", 50),
    TimeSpan.FromSeconds(cfg.GetValue("POD_CHURN_SECONDS", 0))));
builder.Services.AddSingleton<TransactionMetrics>();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("transactions-api", serviceInstanceId: realPodName))
    .WithMetrics(metrics => metrics
        .AddMeter(TransactionMetrics.MeterName)
        .AddAspNetCoreInstrumentation()   // http.server.request.duration: proves requests stay fast
        .AddRuntimeInstrumentation()
        .AddView(instrument =>
        {
            MetricStreamConfiguration config = instrument.Name == TransactionMetrics.DurationName
                ? new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10],
                }
                : new MetricStreamConfiguration();

            config.CardinalityLimit = cardinalityLimit;
            if (instrument.Meter.Name == TransactionMetrics.MeterName && allowedTags.Length > 0)
            {
                config.TagKeys = allowedTags;
            }
            return config;
        })
        // Endpoint, protocol and interval come from OTEL_* env vars. Cumulative temporality (the
        // default) means every series ever seen is kept in memory and re-sent on every export.
        .AddOtlpExporter());

var app = builder.Build();
_ = app.Services.GetRequiredService<TransactionMetrics>(); // create instruments eagerly

app.Logger.LogInformation(
    "Starting {Pod}: CARDINALITY_MODE={Mode} VIRTUAL_PODS={Virtual} OTEL_CARDINALITY_LIMIT={Limit} OTEL_ALLOWED_TAGS={Tags} OTLP={Endpoint}",
    realPodName, mode.Current, cfg.GetValue("VIRTUAL_PODS", 50), cardinalityLimit,
    allowedTags.Length > 0 ? string.Join(',', allowedTags) : "(all)", cfg["OTEL_EXPORTER_OTLP_ENDPOINT"]);

app.MapGet("/healthz", () => Results.Ok("ok"));

app.MapGet("/admin/mode", (CardinalityMode m) => Results.Ok(new { mode = m.Current }));
app.MapPost("/admin/mode", (string value, CardinalityMode m) =>
{
    m.Set(value);
    return Results.Ok(new { mode = m.Current, note = "affects this replica only" });
});

// One HTTP transaction: POST /api/transactions?product=...&consume_type=...
app.MapPost("/api/transactions", async (string? product, string? consumeType, HttpContext ctx,
    TransactionMetrics metrics) =>
{
    var sw = Stopwatch.StartNew();
    var state = await Transactions.Process();
    sw.Stop();

    // In-memory only; returns immediately. Never awaits the exporter.
    metrics.Record(
        Transactions.Normalize(consumeType, Transactions.ConsumeTypes),
        state,
        Transactions.Normalize(product, Transactions.Products),
        sw.Elapsed);

    var status = state switch { "completed" => 200, "pending" => 202, "declined" => 402, _ => 500 };
    return Results.Json(new { state, responseTimeSeconds = sw.Elapsed.TotalSeconds }, statusCode: status);
});

app.Run();

static class Transactions
{
    public static readonly string[] ConsumeTypes = ["api", "web", "mobile", "batch"];
    public static readonly string[] States = ["completed", "pending", "declined", "failed"];
    public static readonly string[] Products =
    [
        "checking", "savings", "credit-card", "mortgage", "auto-loan",
        "personal-loan", "brokerage", "retirement", "insurance", "payments",
    ];

    public static string Normalize(string? value, string[] allowed) =>
        value is not null && allowed.Contains(value) ? value : allowed[Random.Shared.Next(allowed.Length)];

    public static async Task<string> Process()
    {
        await Task.Delay(Random.Shared.Next(3, 60)); // simulated downstream work
        return Random.Shared.Next(100) switch
        {
            < 85 => "completed",
            < 92 => "pending",
            < 97 => "declined",
            _ => "failed",
        };
    }
}

sealed class CardinalityMode(string initial)
{
    private volatile bool _unbounded = Parse(initial);

    public bool IsUnbounded => _unbounded;
    public string Current => _unbounded ? "unbounded" : "bounded";
    public void Set(string value) => _unbounded = Parse(value);

    private static bool Parse(string value) =>
        string.Equals(value?.Trim(), "unbounded", StringComparison.OrdinalIgnoreCase);
}

// Simulates many pods behind few real replicas. Names look like real ReplicaSet pods:
// transactions-api-<template-hash>-<5 chars>. The template hash comes from the real pod name.
sealed class VirtualPods
{
    private const string Alphabet = "bcdfghjklmnpqrstvwxz2456789";
    private readonly string _prefix;
    private readonly Timer? _churnTimer;
    private volatile string[] _names;

    public VirtualPods(string realPodName, int count, TimeSpan churnEvery)
    {
        var parts = realPodName.Split('-');
        _prefix = parts.Length >= 3 ? string.Join('-', parts[..^1]) : "transactions-api-local";
        _names = Enumerable.Range(0, Math.Max(1, count)).Select(_ => NewName()).ToArray();
        if (churnEvery > TimeSpan.Zero)
        {
            _churnTimer = new Timer(_ => Churn(), null, churnEvery, churnEvery);
        }
    }

    public int Count => _names.Length;
    public string Pick() { var n = _names; return n[Random.Shared.Next(n.Length)]; }

    private void Churn()
    {
        var next = (string[])_names.Clone();
        for (var i = 0; i < next.Length; i++)
        {
            if (Random.Shared.Next(5) == 0) next[i] = NewName(); // ~20% of pods "rescheduled"
        }
        _names = next;
    }

    private string NewName() =>
        _prefix + "-" + new string(Enumerable.Range(0, 5).Select(_ => Alphabet[Random.Shared.Next(Alphabet.Length)]).ToArray());
}

sealed class TransactionMetrics
{
    public const string MeterName = "Transactions.Api";
    public const string DurationName = "http.transaction.duration";

    private readonly CardinalityMode _mode;
    private readonly VirtualPods _pods;
    private readonly Counter<long> _transactions;
    private readonly Histogram<double> _duration;

    public TransactionMetrics(IMeterFactory meterFactory, CardinalityMode mode, VirtualPods pods)
    {
        _mode = mode;
        _pods = pods;
        var meter = meterFactory.Create(MeterName);
        // -> http_transactions_total, http_transaction_duration_seconds_*, transactions_cardinality_unbounded
        _transactions = meter.CreateCounter<long>("http.transactions", description: "HTTP transactions processed");
        _duration = meter.CreateHistogram<double>(DurationName, unit: "s", description: "Transaction response time");
        meter.CreateObservableGauge("transactions.cardinality.unbounded", () => _mode.IsUnbounded ? 1 : 0,
            description: "1 when response_time is being emitted as a label");
        meter.CreateObservableGauge("transactions.virtual.pods", () => _pods.Count,
            description: "Simulated pod names emitted by this replica");
    }

    public void Record(string consumeType, string state, string product, TimeSpan elapsed)
    {
        var tags = new TagList
        {
            { "consume_type", consumeType },
            { "state", state },
            { "product", product },
            { "pod", _pods.Pick() },
        };

        // Response time as a proper value: bounded histogram (pod comes from the resource/instance).
        _duration.Record(elapsed.TotalSeconds,
            new TagList { { "consume_type", consumeType }, { "state", state }, { "product", product } });

        // THE BUG: response time as a float LABEL. Nearly every request creates a brand-new series.
        if (_mode.IsUnbounded)
        {
            tags.Add("response_time", elapsed.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture));
        }
        _transactions.Add(1, tags);
    }
}
