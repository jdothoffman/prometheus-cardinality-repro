// transactions-api role: handles HTTP transactions and records metrics with OpenTelemetry.
// Metrics are aggregated IN MEMORY by the OTel SDK and pushed via OTLP on a schedule
// (PeriodicExportingMetricReader, background thread) to a Grafana Alloy agent. The request path only
// does an in-memory Counter.Add / Histogram.Record, so it never waits on the metrics pipeline.
//
// Labels on http_transactions_total: consume_type, state, product, pod (+ response_time = the bug).
// "pod" simulates ~200 pods: each real replica emits VIRTUAL_PODS pod names.
//
// Configuration (environment variables):
//   CARDINALITY_MODE        bounded | unbounded. Unbounded adds response_time (float) as a LABEL.
//                           Also switchable at runtime: POST /admin/mode?value=unbounded
//   VIRTUAL_PODS            simulated pod names per real replica                 (default 50)
//   POD_CHURN_SECONDS       if >0, replace ~20% of pod names this often (rollouts) (default 0 = off)
//   CPU_WORK_ITERATIONS     SHA-256 passes over 4KB per transaction (simulated CPU work, default 50)
//   OTEL_CARDINALITY_LIMIT  max series per metric stream in the SDK (default 200000; SDK default 2000)
//   OTEL_ALLOWED_TAGS       comma list; if set, transaction metrics keep only these tag keys (app fix)
//   OTEL_EXPORTER_OTLP_ENDPOINT / OTEL_EXPORTER_OTLP_PROTOCOL / OTEL_METRIC_EXPORT_INTERVAL
//                           standard OTel SDK settings (set in the Deployment)

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

static class Api
{
    public static void Configure(WebApplicationBuilder builder)
    {
        var cfg = builder.Configuration;
        var cardinalityLimit = cfg.GetValue("OTEL_CARDINALITY_LIMIT", 200_000);
        var allowedTags = (cfg["OTEL_ALLOWED_TAGS"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var realPodName = cfg["POD_NAME"] ?? Environment.MachineName;

        builder.Services.AddSingleton(new CardinalityMode(cfg["CARDINALITY_MODE"] ?? "bounded"));
        builder.Services.AddSingleton(new VirtualPods(
            realPodName,
            cfg.GetValue("VIRTUAL_PODS", 50),
            TimeSpan.FromSeconds(cfg.GetValue("POD_CHURN_SECONDS", 0))));
        builder.Services.AddSingleton<TransactionMetrics>();
        builder.Services.AddSingleton<ReplicaStats>();

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("transactions-api", serviceInstanceId: realPodName))
            .WithMetrics(metrics => metrics
                .AddMeter(TransactionMetrics.MeterName)
                .AddAspNetCoreInstrumentation()
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
    }

    public static void Map(WebApplication app)
    {
        var cfg = app.Configuration;
        var cpuIterations = cfg.GetValue("CPU_WORK_ITERATIONS", 50);
        var mode = app.Services.GetRequiredService<CardinalityMode>();
        _ = app.Services.GetRequiredService<TransactionMetrics>(); // create instruments eagerly

        app.Logger.LogInformation(
            "transactions-api {Pod}: CARDINALITY_MODE={Mode} VIRTUAL_PODS={Virtual} OTEL_CARDINALITY_LIMIT={Limit} OTEL_ALLOWED_TAGS={Tags} CPU_WORK_ITERATIONS={Cpu} OTLP={Endpoint}",
            cfg["POD_NAME"] ?? Environment.MachineName, mode.Current, cfg.GetValue("VIRTUAL_PODS", 50),
            cfg.GetValue("OTEL_CARDINALITY_LIMIT", 200_000), cfg["OTEL_ALLOWED_TAGS"] ?? "(all)", cpuIterations,
            cfg["OTEL_EXPORTER_OTLP_ENDPOINT"]);

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
            <code>GET|POST /admin/mode</code>, <code>GET /healthz</code>.</p></body></html>
            """, "text/html"));

        app.MapGet("/healthz", () => Results.Ok("ok"));

        app.MapGet("/admin/mode", (CardinalityMode m) => Results.Ok(new { mode = m.Current }));
        app.MapPost("/admin/mode", (string value, CardinalityMode m) =>
        {
            m.Set(value);
            return Results.Ok(new { mode = m.Current });
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

    public bool IsUnbounded => _mode.IsUnbounded;

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

// Point-in-time resource stats for this replica, read by the driver (GET /admin/stats).
// Reads cgroup v2 files so the numbers match what the kubelet / OOM killer see.
sealed class ReplicaStats(CardinalityMode mode, VirtualPods pods, IConfiguration cfg)
{
    private readonly long _startedMs = Environment.TickCount64;
    private readonly string _pod = cfg["POD_NAME"] ?? Environment.MachineName;
    private readonly int _cardinalityLimit = cfg.GetValue("OTEL_CARDINALITY_LIMIT", 200_000);
    private long _transactions, _unboundedRecords, _cancelled;

    public void OnTransaction(bool cancelled)
    {
        Interlocked.Increment(ref _transactions);
        if (mode.IsUnbounded) Interlocked.Increment(ref _unboundedRecords);
        if (cancelled) Interlocked.Increment(ref _cancelled);
    }

    public ReplicaSnapshot Snapshot()
    {
        var gc = GC.GetGCMemoryInfo();
        var cpu = ReadKeyValues("/sys/fs/cgroup/cpu.stat");
        return new ReplicaSnapshot
        {
            Pod = _pod,
            Mode = mode.Current,
            SampledAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UptimeSeconds = (Environment.TickCount64 - _startedMs) / 1000.0,
            MemoryCurrentBytes = ReadLong("/sys/fs/cgroup/memory.current") ?? Environment.WorkingSet,
            MemoryLimitBytes = ReadLong("/sys/fs/cgroup/memory.max"),
            GcHeapBytes = gc.HeapSizeBytes,
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
            VirtualPods = pods.Count,
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
    public long SampledAtUnixMs { get; set; }
    public double UptimeSeconds { get; set; }
    public long MemoryCurrentBytes { get; set; }
    public long? MemoryLimitBytes { get; set; }
    public long GcHeapBytes { get; set; }
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
    public int VirtualPods { get; set; }
}
