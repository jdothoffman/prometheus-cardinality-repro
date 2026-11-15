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
//   OTEL_EXPORTER_OTLP_ENDPOINT / OTEL_METRIC_EXPORT_INTERVAL / OTEL_METRIC_EXPORT_TIMEOUT

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

static class Api
{
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

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("transactions-api", serviceInstanceId: realPodName))
            .WithMetrics(metrics =>
            {
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
        _ = app.Services.GetRequiredService<TransactionMetrics>(); // create instruments eagerly

        app.Logger.LogInformation(
            "transactions-api {Pod}: labels={Labels} VIRTUAL_PODS={Virtual} OTEL_CARDINALITY_LIMIT={Limit} scope={Scope} OTEL_ALLOWED_TAGS={Tags} CPU_WORK_ITERATIONS={Cpu} OTLP={Endpoint}",
            cfg["POD_NAME"] ?? Environment.MachineName, labels.Describe(), cfg.GetValue("VIRTUAL_PODS", 50),
            cfg.GetValue("OTEL_CARDINALITY_LIMIT", 200_000), cfg["OTEL_CARDINALITY_LIMIT_SCOPE"] ?? "transactions",
            cfg["OTEL_ALLOWED_TAGS"] ?? "(all)", cpuIterations, cfg["OTEL_EXPORTER_OTLP_ENDPOINT"]);

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
            <code>GET|POST /admin/labels</code>, <code>GET|POST /admin/mode</code>, <code>GET /healthz</code>.</p></body></html>
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

            // In-memory only; returns immediately. Never awaits the exporter.
            metrics.Record(
                Transactions.Normalize(consumeType, Transactions.ConsumeTypes),
                state,
                Transactions.Normalize(product, Transactions.Products),
                sw.Elapsed);
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
        Span<byte> hash = stackalloc byte[32];
        for (var i = 0; i < cpuIterations; i++) SHA256.HashData(Payload, hash);

        await Task.Delay(Random.Shared.Next(3, 60), ct); // simulated downstream call
        return Random.Shared.Next(100) switch
        {
            < 85 => "completed",
            < 92 => "pending",
            < 97 => "declined",
            _ => "failed",
        };
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

    public TransactionMetrics(IMeterFactory meterFactory, LabelSet labels, VirtualPods pods, ExportStats export)
    {
        _labels = labels;
        _pods = pods;
        var meter = meterFactory.Create(MeterName);
        // -> http_transactions_total, http_transaction_duration_seconds_*, transactions_cardinality_unbounded
        _transactions = meter.CreateCounter<long>(TransactionsName, description: "HTTP transactions processed");
        _duration = meter.CreateHistogram<double>(DurationName, unit: "s", description: "Transaction response time");
        meter.CreateObservableGauge("transactions.cardinality.unbounded", () => _labels.IsUnbounded ? 1 : 0,
            description: "1 when response_time is being emitted as a label");
        meter.CreateObservableGauge("transactions.virtual.pods", () => _pods.Count,
            description: "Simulated pod names emitted by this replica");

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

    public void Record(string consumeType, string state, string product, TimeSpan elapsed)
    {
        // Response time as a proper value: bounded histogram (pod comes from the resource/instance).
        _duration.Record(elapsed.TotalSeconds,
            new TagList { { "consume_type", consumeType }, { "state", state }, { "product", product } });

        var (enabled, rtDecimals, extra, pools) = _labels.Current();
        bool Has(string l) => Array.IndexOf(enabled, l) >= 0;
        var tags = new TagList();
        if (Has("consume_type")) tags.Add("consume_type", consumeType);
        if (Has("state")) tags.Add("state", state);
        if (Has("product")) tags.Add("product", product);
        if (Has("pod")) tags.Add("pod", _pods.Pick());
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
            else _lastSuccessUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _lastExportMs = elapsed.TotalMilliseconds;
            _maxExportMsSinceRead = Math.Max(_maxExportMsSinceRead, _lastExportMs);
            _totalExportMicros += (long)(elapsed.TotalMilliseconds * 1000);
            _lastPoints = points;
        }
    }

    public void OnHttpAttempt(long bytes, bool ok)
    {
        lock (_lock)
        {
            _httpRequests++;
            if (!ok) _httpFailures++;
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
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var bytes = request.Content?.Headers.ContentLength ?? 0;
        try
        {
            var resp = await base.SendAsync(request, ct);
            stats.OnHttpAttempt(bytes, resp.IsSuccessStatusCode);
            return resp;
        }
        catch
        {
            stats.OnHttpAttempt(bytes, false);
            throw;
        }
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
    {
        var bytes = request.Content?.Headers.ContentLength ?? 0;
        try
        {
            var resp = base.Send(request, ct);
            stats.OnHttpAttempt(bytes, resp.IsSuccessStatusCode);
            return resp;
        }
        catch
        {
            stats.OnHttpAttempt(bytes, false);
            throw;
        }
    }
}

// Point-in-time resource stats for this replica, read by the driver (GET /admin/stats).
// Reads cgroup v2 files so the numbers match what the kubelet / OOM killer see.
sealed class ReplicaStats(LabelSet labels, VirtualPods pods, ExportStats export, IConfiguration cfg)
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
}
