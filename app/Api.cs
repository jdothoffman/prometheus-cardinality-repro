// transactions-api role: handles HTTP transactions and records metrics with OpenTelemetry.
// Metrics are aggregated IN MEMORY by the OTel SDK and pushed via OTLP on a schedule
// (PeriodicExportingMetricReader, background thread) to a Grafana Alloy agent. The request path only
// does an in-memory Counter.Add / Histogram.Record, so it never waits on the metrics pipeline. But the
// SDK state, the serialization of every export, and any retries all share this pod's CPU and heap with
// the request handling: that is the contention this repro is about.
//
// Labels on http_transactions_total (each can be switched on/off live, see LabelSet):
//   consume_type, state, product, pod (~200 simulated), response_time (float as a LABEL = the bug,
//   with configurable precision: 6 decimals ≈ unique per request ... 1 decimal ≈ one value).
//
// Configuration (environment variables):
//   CARDINALITY_MODE             bounded | unbounded: initial label set (unbounded = + response_time)
//   VIRTUAL_PODS                 simulated pod names per real replica                 (default 50)
//   POD_CHURN_SECONDS            if >0, replace ~20% of pod names this often (rollouts) (default 0 = off)
//   CPU_WORK_ITERATIONS          SHA-256 passes over 4KB per transaction (simulated CPU, default 50)
//   OTEL_CARDINALITY_LIMIT       max series per metric stream in the SDK (default 200000; SDK default 2000)
//   OTEL_CARDINALITY_LIMIT_SCOPE transactions (default): limit applies to http.transactions only
//                                all: limit applies to every instrument. The SDK pre-allocates storage
//                                sized by the limit per stream, so "all" reserves most of the heap.
//   OTEL_ALLOWED_TAGS            comma list; if set, transaction metrics keep only these tag keys (app fix)
//   APPENDED_LABELS              0-8 identity labels (k8s node, pod uid, container id, ...) added to every
//                                transaction series, as an observability team might append them (default 0)
//   OTEL_EXPORTER_OTLP_ENDPOINT / OTEL_METRIC_EXPORT_INTERVAL / OTEL_METRIC_EXPORT_TIMEOUT
//
// Traces and exemplars (docs/tracing.md):
//   TRACE_SAMPLE_RATIO           share of transactions head-sampled in the SDK (spans → Alloy → Tempo).
//                                0 (default) to 0.75: HARD CAP at 75%, higher values are clamped (see Sampling.cs).
//                                Changeable live: POST /admin/sampling?ratio=…, or the console.
//   TRACING_ENABLED              true | false; default: TRACE_SAMPLE_RATIO > 0. false = no tracing pipeline at
//                                all, as in every run before tracing existed (the ratio can't be changed live).
//   METRIC_EXEMPLARS             trace_based (default) | off. With tracing on, each histogram bucket / counter
//                                series keeps the trace id of a recent SAMPLED request as an exemplar. An
//                                exemplar is side data on a sample, not a label, so it adds no series. It
//                                does cost SDK heap per metric point (one reservoir per series), so it
//                                scales with the cardinality you already have.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

static class Api
{
    // Spans this app creates itself, under the ASP.NET Core server span of each transaction.
    public static readonly ActivitySource Tracer = new("Transactions.Api");

    public static void Configure(WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
        var cardinalityLimit = cfg.GetValue("OTEL_CARDINALITY_LIMIT", 200_000);
        var limitAll = string.Equals(cfg["OTEL_CARDINALITY_LIMIT_SCOPE"], "all", StringComparison.OrdinalIgnoreCase);
        var allowedTags = (cfg["OTEL_ALLOWED_TAGS"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var realPodName = cfg["POD_NAME"] ?? Environment.MachineName;
        var endpoint = (cfg["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? "http://localhost:4318").TrimEnd('/');
        var intervalMs = cfg.GetValue("OTEL_METRIC_EXPORT_INTERVAL", 10_000);
        var timeoutMs = cfg.GetValue("OTEL_METRIC_EXPORT_TIMEOUT", 8_000);
        var requestedRatio = cfg.GetValue("TRACE_SAMPLE_RATIO", 0.0);
        var tracing = cfg.GetValue("TRACING_ENABLED", requestedRatio > 0);
        var sampling = new TraceSampling(tracing, requestedRatio, out var samplingWarning);
        var exemplars = tracing && !string.Equals(cfg["METRIC_EXEMPLARS"], "off", StringComparison.OrdinalIgnoreCase);
        builder.Services.AddSingleton(sampling);
        if (samplingWarning is not null) Console.Error.WriteLine("warning: " + samplingWarning);

        var labels = new LabelSet(cfg["CARDINALITY_MODE"] ?? "bounded");
        var exportStats = new ExportStats(intervalMs, timeoutMs);
        builder.Services.AddSingleton(labels);
        builder.Services.AddSingleton(exportStats);
        builder.Services.AddSingleton(new VirtualPods(
            realPodName,
            cfg.GetValue("VIRTUAL_PODS", 50),
            TimeSpan.FromSeconds(cfg.GetValue("POD_CHURN_SECONDS", 0))));
        builder.Services.AddSingleton<TransactionMetrics>();
        builder.Services.AddSingleton<ReplicaStats>();

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("transactions-api", serviceInstanceId: realPodName));

        if (tracing)
        {
            otel.WithTracing(traces => traces
                // Head sampling in the SDK: the decision is made before the request runs, so every exemplar
                // (which is only taken from sampled requests) points at a trace that really was exported.
                // Tail sampling in the agent would break that guarantee: see docs/tracing.md.
                // Capped at 75%, changeable live, and not overridable by a caller's traceparent (Sampling.cs).
                .SetSampler(new CappedRatioSampler(sampling))
                .AddSource(Tracer.Name)
                .AddAspNetCoreInstrumentation(o =>
                    o.Filter = ctx => ctx.Request.Path.StartsWithSegments("/api")) // not /healthz, /admin/*
                .AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri(endpoint + "/v1/traces");
                    o.Protocol = OtlpExportProtocol.HttpProtobuf;
                }));
        }

        otel.WithMetrics(metrics =>
            {
                // trace_based: only measurements taken inside a sampled span can become exemplars.
                metrics.SetExemplarFilter(exemplars ? ExemplarFilterType.TraceBased : ExemplarFilterType.AlwaysOff);
                metrics
                    .AddMeter(TransactionMetrics.MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddView(instrument =>
                    {
                        MetricStreamConfiguration config = instrument.Name == TransactionMetrics.DurationName
                            ? new ExplicitBucketHistogramConfiguration
                            {
                                Boundaries = TransactionMetrics.DurationBoundaries,
                            }
                            : new MetricStreamConfiguration();

                        if (limitAll || instrument.Name == TransactionMetrics.TransactionsName)
                        {
                            config.CardinalityLimit = cardinalityLimit;
                        }
                        if (instrument.Meter.Name == TransactionMetrics.MeterName && allowedTags.Length > 0)
                        {
                            config.TagKeys = allowedTags;
                        }
                        return config;
                    });

                // OTLP/HTTP exporter, wrapped so every export (serialize + send + retries) is measured, and
                // with an HttpClient whose handler records bytes, status codes and retry attempts.
                // Cumulative temporality (the default): every series ever seen is kept in memory and
                // re-sent on every export, so the export cost grows with cardinality.
                var otlp = new OtlpMetricExporter(new OtlpExporterOptions
                {
                    Endpoint = new Uri(endpoint + "/v1/metrics"),
                    Protocol = OtlpExportProtocol.HttpProtobuf,
                    TimeoutMilliseconds = timeoutMs,
                    HttpClientFactory = () => new HttpClient(new ExportHttpHandler(exportStats) { InnerHandler = new HttpClientHandler() })
                    {
                        Timeout = TimeSpan.FromMilliseconds(timeoutMs),
                    },
                });
                metrics.AddReader(new PeriodicExportingMetricReader(new MeasuredExporter(otlp, exportStats), intervalMs, timeoutMs)
                {
                    TemporalityPreference = MetricReaderTemporalityPreference.Cumulative,
                });
            });
    }

    public static void Map(WebApplication app)
    {
        var cfg = app.Configuration;
        var cpuIterations = cfg.GetValue("CPU_WORK_ITERATIONS", 50);
        var labels = app.Services.GetRequiredService<LabelSet>();
        var sampling = app.Services.GetRequiredService<TraceSampling>();
        _ = app.Services.GetRequiredService<TransactionMetrics>(); // create instruments eagerly

        app.Logger.LogInformation(
            "transactions-api {Pod}: labels={Labels} VIRTUAL_PODS={Virtual} OTEL_CARDINALITY_LIMIT={Limit} scope={Scope} OTEL_ALLOWED_TAGS={Tags} CPU_WORK_ITERATIONS={Cpu} OTLP={Endpoint} tracing={Tracing} sampling={Ratio} (max {Max}) METRIC_EXEMPLARS={Exemplars}",
            cfg["POD_NAME"] ?? Environment.MachineName, labels.Describe(), cfg.GetValue("VIRTUAL_PODS", 50),
            cfg.GetValue("OTEL_CARDINALITY_LIMIT", 200_000), cfg["OTEL_CARDINALITY_LIMIT_SCOPE"] ?? "transactions",
            cfg["OTEL_ALLOWED_TAGS"] ?? "(all)", cpuIterations, cfg["OTEL_EXPORTER_OTLP_ENDPOINT"],
            sampling.Enabled, sampling.Ratio, TraceSampling.MaxRatio, cfg["METRIC_EXEMPLARS"] ?? "trace_based");

        // Trace sampling, live. Above the 75% cap → 400 and nothing changes (Sampling.cs).
        app.MapGet("/admin/sampling", () => Results.Ok(sampling.Describe()));
        app.MapPost("/admin/sampling", (double ratio) =>
            sampling.TrySet(ratio, out var error)
                ? Results.Ok(sampling.Describe())
                : sampling.Enabled ? Results.BadRequest(new { error }) : Results.Conflict(new { error }));

        app.MapGet("/", () => Results.Content(
            """
            <!doctype html><html lang="en"><head><meta charset="utf-8"><title>transactions-api</title>
            <meta name="viewport" content="width=device-width, initial-scale=1"></head>
            <body style="font-family:system-ui,sans-serif;max-width:40rem;margin:3rem auto;padding:0 1rem">
            <h1>transactions-api</h1>
            <p>This is a replica under test. The load-test console (with the Start button) runs on the
            <b>transactions-driver</b> Deployment: run <code>make ui</code> and open
            <a href="http://localhost:8080">http://localhost:8080</a>.</p>
            <p>Endpoints: <code>POST /api/transactions</code>, <code>GET /admin/stats</code>,
            <code>GET|POST /admin/labels</code>, <code>GET|POST /admin/mode</code>,
            <code>GET|POST /admin/sampling?ratio=0..0.75</code>, <code>GET /healthz</code>.</p></body></html>
            """, "text/html"));

        app.MapGet("/healthz", () => Results.Ok("ok"));

        // Legacy two-state switch: bounded | unbounded (= all labels incl. response_time at 6 decimals).
        app.MapGet("/admin/mode", (LabelSet l) => Results.Ok(new { mode = l.Mode, labels = l.Enabled, rtDecimals = l.ResponseTimeDecimals }));
        app.MapPost("/admin/mode", (string value, LabelSet l) =>
        {
            l.SetMode(value);
            return Results.Ok(new { mode = l.Mode, labels = l.Enabled });
        });

        // Fine-grained label control: which labels are recorded, and response_time precision.
        app.MapGet("/admin/labels", (LabelSet l) => Results.Ok(l.ToConfig()));
        app.MapPost("/admin/labels", (LabelConfig config, LabelSet l) =>
        {
            l.Set(config);
            return Results.Ok(l.ToConfig());
        });

        app.MapGet("/admin/stats", (ReplicaStats stats) => Results.Json(stats.Snapshot()));

        // One HTTP transaction: POST /api/transactions?product=...&consumeType=...
        app.MapPost("/api/transactions", async (string? product, string? consumeType, HttpContext ctx,
            TransactionMetrics metrics, ReplicaStats stats) =>
        {
            var sw = Stopwatch.StartNew();
            string state;
            try
            {
                state = await Transactions.Process(cpuIterations, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                state = "cancelled"; // client gave up (e.g. its 3s timeout) before we finished
            }
            sw.Stop();
            var normalizedConsumeType = Transactions.Normalize(consumeType, Transactions.ConsumeTypes);
            var normalizedProduct = Transactions.Normalize(product, Transactions.Products);

            // Per-request detail belongs on the span, not on a metric label: Tempo stores it per trace
            // and TraceQL can search it, at no cost to Prometheus. This is where an exact response time,
            // a transaction id or a tenant id can go (compare the response_time/GUID labels under test).
            // The same keys as the metric labels let Grafana jump from a span to its metric slice.
            if (Activity.Current is { IsAllDataRequested: true } span)
            {
                span.SetTag("transaction.id", Guid.NewGuid().ToString("N"));
                span.SetTag("transaction.product", normalizedProduct);
                span.SetTag("transaction.consume_type", normalizedConsumeType);
                span.SetTag("transaction.state", state);
                span.SetTag("transaction.response_time_ms", Math.Round(sw.Elapsed.TotalMilliseconds, 3));
                if (state == "failed") span.SetStatus(ActivityStatusCode.Error, "transaction failed");
            }

            // In-memory only; returns immediately. Never awaits the exporter. Runs inside the request's
            // span, so with exemplars on, a sampled request can leave its trace id on the bucket it hit.
            metrics.Record(normalizedConsumeType, state, normalizedProduct, sw.Elapsed,
                traced: Activity.Current?.Recorded == true);
            stats.OnTransaction(state == "cancelled");

            // Business outcomes (completed/pending/declined/failed) are all HTTP 200; HTTP errors and
            // timeouts seen by the driver therefore come only from the system being overloaded.
            return state == "cancelled"
                ? Results.Empty
                : Results.Json(new { state, responseTimeSeconds = sw.Elapsed.TotalSeconds });
        });
    }
}

static class Transactions
{
    public static readonly string[] ConsumeTypes = ["api", "web", "mobile", "batch"];
    public static readonly string[] Products =
    [
        "checking", "savings", "credit-card", "mortgage", "auto-loan",
        "personal-loan", "brokerage", "retirement", "insurance", "payments",
    ];

    private static readonly byte[] Payload = RandomNumberGenerator.GetBytes(4096);

    public static string Normalize(string? value, string[] allowed) =>
        value is not null && allowed.Contains(value) ? value : allowed[Random.Shared.Next(allowed.Length)];

    public static async Task<string> Process(int cpuIterations, CancellationToken ct)
    {
        // Simulated CPU work (validation, serialization, crypto). Under a CPU limit this is what
        // queues up when the pod is starved.
        // Spans are only created for sampled requests (StartActivity returns null otherwise).
        using (Api.Tracer.StartActivity("transaction.validate"))
        {
            Validate(cpuIterations);
        }

        using var downstream = Api.Tracer.StartActivity("ledger.post", ActivityKind.Client);
        downstream?.SetTag("peer.service", "ledger");
        await Task.Delay(Random.Shared.Next(3, 60), ct); // simulated downstream call
        var state = Random.Shared.Next(100) switch
        {
            < 85 => "completed",
            < 92 => "pending",
            < 97 => "declined",
            _ => "failed",
        };
        if (state == "failed") downstream?.SetStatus(ActivityStatusCode.Error, "ledger rejected the posting");
        return state;
    }

    // Not inlined into the async method: a stackalloc can't live across an await.
    private static void Validate(int cpuIterations)
    {
        Span<byte> hash = stackalloc byte[32];
        for (var i = 0; i < cpuIterations; i++) SHA256.HashData(Payload, hash);
    }
}

sealed record LabelConfig(string[]? Labels = null, int? ResponseTimeDecimals = null, int? ExtraLabels = null, int? ExtraPoolSize = null);

// Which labels http_transactions_total carries, switchable at runtime. Removing a label only affects new
// measurements: series the SDK already created stay in memory (cumulative) until the process restarts.
//
// Extra GUID labels (0-5): request_id, trace_id, session_id, correlation_id, tenant_id, the kind of labels
// observability tooling appends. Values are random GUIDs, either unique per request (ExtraPoolSize = 0) or
// drawn from a pool of N GUIDs per label. They add ~40 bytes per label to every series, so they grow the
// export payload (and its serialization cost) as well as the series count. Changing them regenerates the GUIDs.
sealed class LabelSet
{
    public static readonly string[] All = ["consume_type", "state", "product", "pod", "response_time"];
    public static readonly string[] Bounded = ["consume_type", "state", "product", "pod"];
    public static readonly string[] ExtraNames = ["request_id", "trace_id", "session_id", "correlation_id", "tenant_id"];
    public const int MaxPool = 50_000;

    private sealed record State(string[] Enabled, int RtDecimals, int ExtraCount, int ExtraPool, string[][] Pools);
    private volatile State _s;

    public LabelSet(string initialMode) => _s = new State(ModeLabels(initialMode), 6, 0, 0, []);

    public string[] Enabled => _s.Enabled;
    public int ResponseTimeDecimals => _s.RtDecimals;
    public int ExtraLabels => _s.ExtraCount;
    public int ExtraPoolSize => _s.ExtraPool;
    public bool Has(string label) => Array.IndexOf(_s.Enabled, label) >= 0;
    public bool IsUnbounded => Has("response_time") || (_s.ExtraCount > 0 && (_s.ExtraPool == 0 || _s.ExtraPool > 1000));
    public string Mode => Has("response_time") ? "unbounded" : "bounded";
    public string Key => KeyOf(_s.Enabled, _s.RtDecimals, _s.ExtraCount, _s.ExtraPool);

    // response_time "precision" Band (-1): instead of a rounded float, a label with a fixed set of latency
    // bands. The bounded alternative for teams that want latency as a label (cardinality x5 at most).
    public const int Band = -1;
    public static readonly string[] BandValues = ["0-50ms", "50-250ms", "250ms-1s", "1-3s", "3s+"];
    public static string BandOf(double seconds) => seconds switch
    {
        < 0.05 => BandValues[0],
        < 0.25 => BandValues[1],
        < 1 => BandValues[2],
        < 3 => BandValues[3],
        _ => BandValues[4],
    };
    public static string RtText(int rtDecimals) => rtDecimals == Band ? "band" : $"{rtDecimals}dp";

    public static string KeyOf(string[] enabled, int rtDecimals, int extra, int pool) =>
        string.Join(",", enabled) + (enabled.Contains("response_time") ? $"@{rtDecimals}" : "") + (extra > 0 ? $"+{extra}x{pool}" : "");

    public string Describe()
    {
        var s = _s;
        var d = string.Join(", ", s.Enabled.Select(l => l == "response_time" ? $"response_time({RtText(s.RtDecimals)})" : l));
        return s.ExtraCount > 0 ? $"{d}, +{s.ExtraCount} GUID labels ({(s.ExtraPool == 0 ? "unique" : $"pool {s.ExtraPool:N0}")})" : d;
    }

    public LabelConfig ToConfig() { var s = _s; return new(s.Enabled, s.RtDecimals, s.ExtraCount, s.ExtraPool); }

    public void SetMode(string mode) => _s = new State(ModeLabels(mode), 6, 0, 0, []);

    public void Set(LabelConfig c)
    {
        var cur = _s;
        var enabled = c.Labels is { } labels ? All.Where(l => labels.Contains(l, StringComparer.OrdinalIgnoreCase)).ToArray() : cur.Enabled;
        var rt = c.ResponseTimeDecimals is { } d ? Math.Clamp(d, Band, 6) : cur.RtDecimals;
        var n = c.ExtraLabels is { } x ? Math.Clamp(x, 0, ExtraNames.Length) : cur.ExtraCount;
        var k = c.ExtraPoolSize is { } y ? Math.Clamp(y, 0, MaxPool) : cur.ExtraPool;
        var pools = n == cur.ExtraCount && k == cur.ExtraPool && c.ExtraLabels is null ? cur.Pools
            : Enumerable.Range(0, n).Select(_ => Enumerable.Range(0, k).Select(_ => Guid.NewGuid().ToString()).ToArray()).ToArray();
        _s = new State(enabled, rt, n, k, pools);
    }

    // Snapshot used by one Record call, so a concurrent change can't produce a half-applied label set.
    public (string[] Enabled, int RtDecimals, int ExtraCount, string[][] Pools) Current() { var s = _s; return (s.Enabled, s.RtDecimals, s.ExtraCount, s.Pools); }

    private static string[] ModeLabels(string mode) =>
        string.Equals(mode?.Trim(), "unbounded", StringComparison.OrdinalIgnoreCase) ? All : Bounded;
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
        // VIRTUAL_PODS=1 is the production-like setting: the pod label is this replica's own name.
        _names = count <= 1 ? [realPodName] : Enumerable.Range(0, count).Select(_ => NewName()).ToArray();
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
    // Histogram buckets (seconds), shaped around where this app's latencies and percentiles actually are
    // (p50 ≈ 35 ms, p95 ≈ 63 ms): fine resolution from 25 to 100 ms, then coarse out to the 3 s timeout.
    // The earlier [5, 10, 25, 50, 100, 250 ms, …] put p95 and p99 inside one 50–100 ms bucket and
    // overstated p95 by ~40% (89 vs 63 ms). Same bucket count as before (11 boundaries).
    public static readonly double[] DurationBoundaries = [0.01, 0.025, 0.04, 0.05, 0.06, 0.07, 0.08, 0.1, 0.25, 1, 3];
    public const string TransactionsName = "http.transactions";
    public const string DurationName = "http.transaction.duration";

    private readonly LabelSet _labels;
    private readonly VirtualPods _pods;
    private readonly Counter<long> _transactions;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _traced;
    private readonly KeyValuePair<string, object?>[] _appended;

    public TransactionMetrics(IMeterFactory meterFactory, LabelSet labels, VirtualPods pods, ExportStats export, IConfiguration cfg,
        TraceSampling sampling)
    {
        _labels = labels;
        _pods = pods;
        _appended = AppendedLabels.For(cfg["POD_NAME"] ?? Environment.MachineName, cfg.GetValue("APPENDED_LABELS", 0));
        var meter = meterFactory.Create(MeterName);
        // -> http_transactions_total, http_transaction_duration_seconds_*, transactions_cardinality_unbounded
        _transactions = meter.CreateCounter<long>(TransactionsName, description: "HTTP transactions processed");
        _duration = meter.CreateHistogram<double>(DurationName, unit: "s", description: "Transaction response time");
        // No labels: observed sampled share = rate(transactions_traced_total) / rate(http_transactions_total).
        _traced = meter.CreateCounter<long>("transactions.traced", description: "Transactions whose trace was sampled (exported to Tempo)");
        meter.CreateObservableGauge("transactions.cardinality.unbounded", () => _labels.IsUnbounded ? 1 : 0,
            description: "1 when response_time is being emitted as a label");
        meter.CreateObservableGauge("transactions.virtual.pods", () => _pods.Count,
            description: "Simulated pod names emitted by this replica");
        // Run settings as one series per pod each, so dashboards can show what a run is configured with.
        var cardinalityLimit = cfg.GetValue("OTEL_CARDINALITY_LIMIT", 200_000);
        var limitAll = string.Equals(cfg["OTEL_CARDINALITY_LIMIT_SCOPE"], "all", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var exemplarsOn = sampling.Enabled && !string.Equals(cfg["METRIC_EXEMPLARS"], "off", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var allowList = string.IsNullOrWhiteSpace(cfg["OTEL_ALLOWED_TAGS"]) ? 0 : 1;
        meter.CreateObservableGauge("transactions.config.cardinality.limit", () => cardinalityLimit,
            description: "OTEL_CARDINALITY_LIMIT this replica runs with");
        meter.CreateObservableGauge("transactions.config.cardinality.limit.all", () => limitAll,
            description: "1 when the cardinality limit applies to every instrument (OTEL_CARDINALITY_LIMIT_SCOPE=all)");
        meter.CreateObservableGauge("transactions.config.tag.allowlist", () => allowList,
            description: "1 when the OTEL_ALLOWED_TAGS View (fix F1) is on");
        meter.CreateObservableGauge("transactions.config.appended.labels", () => _appended.Length,
            description: "Identity labels appended to every transaction series (APPENDED_LABELS)");
        meter.CreateObservableGauge("transactions.config.trace.sample.ratio", () => sampling.Ratio,
            description: "Configured trace sampling ratio, live (0 = off); never above the cap");
        meter.CreateObservableGauge("transactions.config.trace.sample.ratio.max", () => TraceSampling.MaxRatio,
            description: "Hard cap on the trace sampling ratio");
        meter.CreateObservableGauge("transactions.config.exemplars", () => exemplarsOn,
            description: "1 when sampled requests leave exemplars (trace ids) on the metrics");

        // The exporter's own cost, reported through the same pipeline (so it has gaps when that breaks;
        // the driver also polls these directly from /admin/stats).
        meter.CreateObservableGauge("otlp.export.last.duration", () => export.LastExportMs / 1000.0, unit: "s",
            description: "Duration of the last OTLP export (serialize + send + retries)");
        meter.CreateObservableGauge("otlp.export.last.points", () => export.LastPoints,
            description: "Metric points in the last OTLP export");
        meter.CreateObservableGauge("otlp.export.last.bytes", () => export.LastBytes, unit: "By",
            description: "Request body size of the last OTLP HTTP request");
        meter.CreateObservableCounter("otlp.export.failures", () => export.Failures,
            description: "OTLP exports that did not succeed");
        meter.CreateObservableCounter("otlp.export.http.retries", () => export.HttpRetries,
            description: "OTLP HTTP requests beyond the first per export (retries after 429/5xx/timeouts)");
    }

    public void Record(string consumeType, string state, string product, TimeSpan elapsed, bool traced = false)
    {
        if (traced) _traced.Add(1);
        // Response time as a proper value: bounded histogram (pod comes from the resource/instance).
        var durationTags = new TagList { { "consume_type", consumeType }, { "state", state }, { "product", product } };
        foreach (var a in _appended) durationTags.Add(a);
        _duration.Record(elapsed.TotalSeconds, durationTags);

        var (enabled, rtDecimals, extra, pools) = _labels.Current();
        bool Has(string l) => Array.IndexOf(enabled, l) >= 0;
        var tags = new TagList();
        if (Has("consume_type")) tags.Add("consume_type", consumeType);
        if (Has("state")) tags.Add("state", state);
        if (Has("product")) tags.Add("product", product);
        if (Has("pod")) tags.Add("pod", _pods.Pick());
        foreach (var a in _appended) tags.Add(a);
        // THE BUG: response time as a float LABEL. At 6 decimals nearly every request creates a new series;
        // lowering the precision buckets it (3 dp ≈ 1ms buckets, 2 dp ≈ 10ms, 1 dp ≈ 100ms). Band (-1) uses
        // 5 fixed latency bands instead: bounded no matter how latency spreads under load.
        if (Has("response_time"))
            tags.Add("response_time", rtDecimals == LabelSet.Band
                ? LabelSet.BandOf(elapsed.TotalSeconds)
                : Math.Round(elapsed.TotalSeconds, rtDecimals).ToString("F" + rtDecimals, CultureInfo.InvariantCulture));
        // Extra GUID labels: unique per request, or from a fixed pool per label.
        for (var i = 0; i < extra; i++)
        {
            var pool = pools[i];
            tags.Add(LabelSet.ExtraNames[i], pool.Length == 0 ? Guid.NewGuid().ToString() : pool[Random.Shared.Next(pool.Length)]);
        }
        _transactions.Add(1, tags);
    }
}

// Cost of the metrics pipeline as seen from inside the app: duration of each export (on the exporter's
// background thread, but on this pod's CPU), points per export, bytes and status per HTTP attempt.
sealed class ExportStats(int intervalMs, int timeoutMs)
{
    private readonly Lock _lock = new();
    private long _exports, _failures, _httpRequests, _httpFailures, _httpRetries, _totalExportMicros;
    private double _lastExportMs, _maxExportMsSinceRead;
    private long _lastPoints, _lastBytes, _lastSuccessUnixMs;
    private int _attemptsThisExport;
    // Why exports failed: per failed HTTP attempt "HTTP <code>", "timeout / cancelled" or "connection: <error>",
    // and per export that failed without any HTTP request (e.g. the exporter gave up before sending).
    private readonly Dictionary<string, long> _failureReasons = [];

    public int IntervalMs => intervalMs;
    public int TimeoutMs => timeoutMs;
    public double LastExportMs { get { lock (_lock) return _lastExportMs; } }
    public long LastPoints => Interlocked.Read(ref _lastPoints);
    public long LastBytes => Interlocked.Read(ref _lastBytes);
    public long Failures => Interlocked.Read(ref _failures);
    public long HttpRetries => Interlocked.Read(ref _httpRetries);

    public void OnExportStart() => Interlocked.Exchange(ref _attemptsThisExport, 0);

    public void OnExport(TimeSpan elapsed, long points, bool ok)
    {
        lock (_lock)
        {
            _exports++;
            if (!ok) _failures++;
            if (!ok && Volatile.Read(ref _attemptsThisExport) == 0) Count(NotSent);
            if (ok) _lastSuccessUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _lastExportMs = elapsed.TotalMilliseconds;
            _maxExportMsSinceRead = Math.Max(_maxExportMsSinceRead, _lastExportMs);
            _totalExportMicros += (long)(elapsed.TotalMilliseconds * 1000);
            _lastPoints = points;
        }
    }

    public const string NotSent = "not sent (export failed before any HTTP request)";

    private void Count(string reason) => _failureReasons[reason] = _failureReasons.GetValueOrDefault(reason) + 1;

    public Dictionary<string, long> FailureReasons() { lock (_lock) return new(_failureReasons); }

    public void OnHttpAttempt(long bytes, bool ok, string? failure = null)
    {
        lock (_lock)
        {
            _httpRequests++;
            if (!ok) { _httpFailures++; Count(failure ?? "failed"); }
            if (Interlocked.Increment(ref _attemptsThisExport) > 1) _httpRetries++;
            if (bytes > 0) _lastBytes = bytes;
        }
    }

    public (long Exports, long Failures, long HttpRequests, long HttpFailures, long HttpRetries, double TotalExportMs,
        double LastExportMs, double MaxExportMsSinceRead, long LastPoints, long LastBytes, long LastSuccessUnixMs) ReadAndReset()
    {
        lock (_lock)
        {
            var r = (_exports, _failures, _httpRequests, _httpFailures, _httpRetries, _totalExportMicros / 1000.0,
                _lastExportMs, _maxExportMsSinceRead, _lastPoints, _lastBytes, _lastSuccessUnixMs);
            _maxExportMsSinceRead = 0;
            return r;
        }
    }
}

sealed class MeasuredExporter(BaseExporter<Metric> inner, ExportStats stats) : BaseExporter<Metric>
{
    // The SDK sets ParentProvider only on the exporter attached to the reader (this wrapper). The OTLP
    // exporter reads the Resource (service.name -> job, service.instance.id -> instance) from its own
    // ParentProvider, so pass it through; its setter is internal in OpenTelemetry 1.21.
    private static readonly System.Reflection.MethodInfo? SetParent =
        typeof(BaseExporter<Metric>).GetProperty(nameof(ParentProvider))?.GetSetMethod(nonPublic: true);
    private bool _parentSet;

    public override ExportResult Export(in Batch<Metric> batch)
    {
        if (!_parentSet && ParentProvider is not null)
        {
            SetParent?.Invoke(inner, [ParentProvider]);
            _parentSet = true;
        }
        long points = 0;
        foreach (var metric in batch)
        {
            foreach (ref readonly var _ in metric.GetMetricPoints()) points++;
        }
        stats.OnExportStart();
        var sw = Stopwatch.StartNew();
        var result = ExportResult.Failure;
        try { result = inner.Export(batch); }
        finally { stats.OnExport(sw.Elapsed, points, result == ExportResult.Success); }
        return result;
    }

    protected override bool OnForceFlush(int timeoutMilliseconds) => inner.ForceFlush(timeoutMilliseconds);
    protected override bool OnShutdown(int timeoutMilliseconds) => inner.Shutdown(timeoutMilliseconds);
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

sealed class ExportHttpHandler(ExportStats stats) : DelegatingHandler
{
    // Records each attempt's outcome: a status code means the request reached the agent (and how it answered);
    // an exception means it did not complete (timeout, connection refused/reset, ...).
    private static string Reason(HttpResponseMessage r) => $"HTTP {(int)r.StatusCode}";
    private static string Reason(Exception e) => e switch
    {
        TaskCanceledException or OperationCanceledException => "timeout / cancelled",
        HttpRequestException h => $"connection: {h.HttpRequestError}",
        _ => e.GetType().Name,
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var bytes = request.Content?.Headers.ContentLength ?? 0;
        try
        {
            var resp = await base.SendAsync(request, ct);
            stats.OnHttpAttempt(bytes, resp.IsSuccessStatusCode, resp.IsSuccessStatusCode ? null : Reason(resp));
            return resp;
        }
        catch (Exception e)
        {
            stats.OnHttpAttempt(bytes, false, Reason(e));
            throw;
        }
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
    {
        var bytes = request.Content?.Headers.ContentLength ?? 0;
        try
        {
            var resp = base.Send(request, ct);
            stats.OnHttpAttempt(bytes, resp.IsSuccessStatusCode, resp.IsSuccessStatusCode ? null : Reason(resp));
            return resp;
        }
        catch (Exception e)
        {
            stats.OnHttpAttempt(bytes, false, Reason(e));
            throw;
        }
    }
}

// Identity labels an observability team might append to every series (Kubernetes attributes promoted to
// metric labels). One value per pod, so they don't multiply the series count; they make every series
// heavier, and some (pod uid, container id) change on every restart.
static class AppendedLabels
{
    public static readonly string[] Names =
        ["k8s_namespace_name", "k8s_node_name", "k8s_replicaset_name", "k8s_pod_uid", "container_id", "k8s_pod_ip", "cluster", "region"];

    public static KeyValuePair<string, object?>[] For(string podName, int count)
    {
        var n = Math.Clamp(count, 0, Names.Length);
        if (n == 0) return [];
        var parts = podName.Split('-');
        var replicaSet = parts.Length >= 3 ? string.Join('-', parts[..^1]) : podName;
        var values = new[]
        {
            "demo",
            $"aks-nodepool1-{Random.Shared.Next(10_000_000, 99_999_999)}-vmss{Random.Shared.Next(0, 20):D6}",
            replicaSet,
            Guid.NewGuid().ToString(),
            "containerd://" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            $"10.244.{Random.Shared.Next(0, 255)}.{Random.Shared.Next(2, 254)}",
            "cluster-a",
            "eastus",
        };
        return Names.Take(n).Select((name, i) => new KeyValuePair<string, object?>(name, values[i])).ToArray();
    }
}

// Point-in-time resource stats for this replica, read by the driver (GET /admin/stats).
// Reads cgroup v2 files so the numbers match what the kubelet / OOM killer see.
sealed class ReplicaStats(LabelSet labels, VirtualPods pods, ExportStats export, IConfiguration cfg, TraceSampling sampling)
{
    private readonly long _startedMs = Environment.TickCount64;
    private readonly string _pod = cfg["POD_NAME"] ?? Environment.MachineName;
    private readonly int _cardinalityLimit = cfg.GetValue("OTEL_CARDINALITY_LIMIT", 200_000);
    private readonly string _limitScope = cfg["OTEL_CARDINALITY_LIMIT_SCOPE"] ?? "transactions";
    private long _transactions, _unboundedRecords, _cancelled;

    public void OnTransaction(bool cancelled)
    {
        Interlocked.Increment(ref _transactions);
        if (labels.IsUnbounded) Interlocked.Increment(ref _unboundedRecords);
        if (cancelled) Interlocked.Increment(ref _cancelled);
    }

    public ReplicaSnapshot Snapshot()
    {
        var gc = GC.GetGCMemoryInfo();
        var cpu = ReadKeyValues("/sys/fs/cgroup/cpu.stat");
        var ex = export.ReadAndReset();
        return new ReplicaSnapshot
        {
            Pod = _pod,
            Mode = labels.Mode,
            Labels = labels.Enabled,
            ResponseTimeDecimals = labels.ResponseTimeDecimals,
            ExtraLabels = labels.ExtraLabels,
            ExtraPoolSize = labels.ExtraPoolSize,
            LabelKey = labels.Key,
            TracingEnabled = sampling.Enabled,
            TraceSampleRatio = sampling.Ratio,
            SampledAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UptimeSeconds = (Environment.TickCount64 - _startedMs) / 1000.0,
            MemoryCurrentBytes = ReadLong("/sys/fs/cgroup/memory.current") ?? Environment.WorkingSet,
            MemoryLimitBytes = ReadLong("/sys/fs/cgroup/memory.max"),
            GcHeapBytes = gc.HeapSizeBytes,
            GcCommittedBytes = gc.TotalCommittedBytes,
            // In a container .NET sets a GC heap hard limit (default 75% of the memory limit). When the
            // heap reaches it, allocations throw OutOfMemoryException even if the pod looks far from OOM.
            GcHeapLimitBytes = gc.TotalAvailableMemoryBytes,
            GcPauseTimePercent = gc.PauseTimePercentage,
            GcTotalPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds,
            Gen2Collections = GC.CollectionCount(2),
            ThreadPoolThreads = ThreadPool.ThreadCount,
            ThreadPoolQueue = ThreadPool.PendingWorkItemCount,
            CpuUsageUsec = cpu.GetValueOrDefault("usage_usec",
                (long)(Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds * 1000)),
            CpuPeriods = cpu.GetValueOrDefault("nr_periods"),
            CpuThrottledPeriods = cpu.GetValueOrDefault("nr_throttled"),
            CpuLimitCores = ReadCpuLimit(),
            Transactions = Interlocked.Read(ref _transactions),
            UnboundedRecords = Interlocked.Read(ref _unboundedRecords),
            Cancelled = Interlocked.Read(ref _cancelled),
            CardinalityLimit = _cardinalityLimit,
            CardinalityLimitScope = _limitScope,
            VirtualPods = pods.Count,
            ExportIntervalMs = export.IntervalMs,
            ExportTimeoutMs = export.TimeoutMs,
            Exports = ex.Exports,
            ExportFailures = ex.Failures,
            ExportTotalMs = ex.TotalExportMs,
            ExportLastMs = ex.LastExportMs,
            ExportMaxMsSinceLastRead = ex.MaxExportMsSinceRead,
            ExportLastPoints = ex.LastPoints,
            ExportLastBytes = ex.LastBytes,
            ExportHttpRequests = ex.HttpRequests,
            ExportHttpFailures = ex.HttpFailures,
            ExportHttpRetries = ex.HttpRetries,
            ExportLastSuccessUnixMs = ex.LastSuccessUnixMs,
            ExportFailureReasons = export.FailureReasons(),
            AppendedLabels = Math.Clamp(cfg.GetValue("APPENDED_LABELS", 0), 0, AppendedLabels.Names.Length),
        };
    }

    private static long? ReadLong(string path)
    {
        try { return long.TryParse(File.ReadAllText(path).Trim(), out var v) ? v : null; }
        catch { return null; }
    }

    private static double? ReadCpuLimit()
    {
        try
        {
            var parts = File.ReadAllText("/sys/fs/cgroup/cpu.max").Trim().Split(' ');
            return parts[0] == "max" ? null : double.Parse(parts[0]) / double.Parse(parts[1]);
        }
        catch { return null; }
    }

    private static Dictionary<string, long> ReadKeyValues(string path)
    {
        var result = new Dictionary<string, long>();
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var p = line.Split(' ');
                if (p.Length == 2 && long.TryParse(p[1], out var v)) result[p[0]] = v;
            }
        }
        catch { /* not on Linux / no cgroup v2 */ }
        return result;
    }
}

sealed class ReplicaSnapshot
{
    public string Pod { get; set; } = "";
    public string Mode { get; set; } = "";
    public string[] Labels { get; set; } = [];
    public int ResponseTimeDecimals { get; set; }
    public int ExtraLabels { get; set; }
    public int ExtraPoolSize { get; set; }
    public string LabelKey { get; set; } = "";
    public bool TracingEnabled { get; set; }
    public double TraceSampleRatio { get; set; }
    public long SampledAtUnixMs { get; set; }
    public double UptimeSeconds { get; set; }
    public long MemoryCurrentBytes { get; set; }
    public long? MemoryLimitBytes { get; set; }
    public long GcHeapBytes { get; set; }
    public long GcCommittedBytes { get; set; }
    public long GcHeapLimitBytes { get; set; }
    public double GcPauseTimePercent { get; set; }
    public double GcTotalPauseMs { get; set; }
    public int Gen2Collections { get; set; }
    public int ThreadPoolThreads { get; set; }
    public long ThreadPoolQueue { get; set; }
    public long CpuUsageUsec { get; set; }
    public long CpuPeriods { get; set; }
    public long CpuThrottledPeriods { get; set; }
    public double? CpuLimitCores { get; set; }
    public long Transactions { get; set; }
    public long UnboundedRecords { get; set; }
    public long Cancelled { get; set; }
    public int CardinalityLimit { get; set; }
    public string CardinalityLimitScope { get; set; } = "";
    public int VirtualPods { get; set; }
    public int ExportIntervalMs { get; set; }
    public int ExportTimeoutMs { get; set; }
    public long Exports { get; set; }
    public long ExportFailures { get; set; }
    public double ExportTotalMs { get; set; }
    public double ExportLastMs { get; set; }
    public double ExportMaxMsSinceLastRead { get; set; }
    public long ExportLastPoints { get; set; }
    public long ExportLastBytes { get; set; }
    public long ExportHttpRequests { get; set; }
    public long ExportHttpFailures { get; set; }
    public long ExportHttpRetries { get; set; }
    public long ExportLastSuccessUnixMs { get; set; }
    public Dictionary<string, long> ExportFailureReasons { get; set; } = [];
    public int AppendedLabels { get; set; }
}
