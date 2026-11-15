// APP_ROLE=fleet: simulates many more application pods than the cluster can run.
//
// Each simulated pod behaves like a real transactions-api pod as far as the metrics pipeline can tell:
// it has its own OpenTelemetry MeterProvider (own resource: service.name=transactions-api,
// service.instance.id=<pod name>), records the same instruments with production-style labels (the pod
// label is its OWN name, response_time is bucketed), and pushes its own OTLP export to Alloy every
// export interval, at its own phase. So Alloy sees N independent pods, as it would in production, while
// the real replicas under test stay few enough to measure precisely.
//
// The simulated pods do not serve HTTP traffic: they record a synthetic transaction stream at
// FLEET_RATE_PER_POD. Their series carry otel_scope_name="Transactions.Api.sim.<pod>" (one meter per pod,
// because a MeterProvider subscribes to meters by name), so the PROTECT_PROM agent configs can drop them
// before the (small, local) Prometheus while Alloy still receives and converts all of them.
//
// Configuration (environment):
//   FLEET_PODS              simulated pods in this fleet replica              (default 150)
//   FLEET_RATE_PER_POD      synthetic transactions/s per simulated pod        (default 6)
//   FLEET_RESPONSE_TIME     band | off | <decimals 0-6>: response_time label  (default band)
//   FLEET_EXTRA_SERIES      extra gauge series per pod, standing in for the runtime/HTTP/dependency
//                           metrics a real pod also exports                    (default 100)
//   FLEET_CHURN_SECONDS     if >0, replace ~20% of the simulated pods this often (rollouts)  (default 0)
//   FLEET_APPENDED_LABELS   0-8 identity labels per series (k8s node, pod uid, container id, ...), as an
//                           observability team might append them; same as APPENDED_LABELS on the api (default 0)
//   OTEL_EXPORTER_OTLP_ENDPOINT / OTEL_METRIC_EXPORT_INTERVAL / OTEL_METRIC_EXPORT_TIMEOUT, and
//   OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY as for the api role.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

static class Fleet
{
    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<FleetSimulator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<FleetSimulator>());
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/", () => Results.Text("transactions-fleet: simulated pods for the metrics pipeline. GET /admin/stats"));
        app.MapGet("/healthz", () => Results.Ok("ok"));
        app.MapGet("/admin/stats", (FleetSimulator f) => Results.Ok(f.Stats()));
    }
}

sealed record FleetStats(int Pods, double RatePerPod, string ResponseTime, int ExtraSeries, int ChurnSeconds,
    long Exports, long ExportFailures, long HttpRetries, double MaxLastExportMs, long PointsPerPod, long Churned,
    int AppendedLabels = 0, Dictionary<string, long>? FailureReasons = null);

sealed class FleetSimulator(IConfiguration cfg, ILogger<FleetSimulator> log) : BackgroundService
{
    private readonly int _count = Math.Clamp(cfg.GetValue("FLEET_PODS", 150), 0, 2_000);
    private readonly double _rate = Math.Clamp(cfg.GetValue("FLEET_RATE_PER_POD", 6.0), 0, 1_000);
    private readonly int _rt = ParseResponseTime(cfg["FLEET_RESPONSE_TIME"]);
    private readonly int _extra = Math.Clamp(cfg.GetValue("FLEET_EXTRA_SERIES", 100), 0, 10_000);
    private readonly int _churnSeconds = Math.Max(0, cfg.GetValue("FLEET_CHURN_SECONDS", 0));
    private readonly int _appended = Math.Clamp(cfg.GetValue("FLEET_APPENDED_LABELS", 0), 0, AppendedLabels.Names.Length);
    private int _churning; // 1 while a churn is retiring pods, so timer ticks never overlap
    private readonly string _endpoint = (cfg["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? "http://localhost:4318").TrimEnd('/');
    private readonly int _intervalMs = cfg.GetValue("OTEL_METRIC_EXPORT_INTERVAL", 10_000);
    private readonly int _timeoutMs = cfg.GetValue("OTEL_METRIC_EXPORT_TIMEOUT", 8_000);
    // One fake ReplicaSet hash per fleet replica, so names look like real pods: transactions-api-<rs>-<5>.
    private readonly string _rs = RandomChars(10);
    private readonly Lock _lock = new();
    private readonly List<SimPod> _pods = [];
    private long _churned, _retiredExports, _retiredFailures, _retiredRetries;
    private readonly Dictionary<string, long> _retiredReasons = [];

    // -2 = no response_time label, -1 = band (LabelSet.Band), 0..6 = decimals.
    private static int ParseResponseTime(string? v) => (v ?? "band").Trim().ToLowerInvariant() switch
    {
        "band" => LabelSet.Band,
        "off" or "none" or "" => -2,
        var s when int.TryParse(s, out var d) => Math.Clamp(d, 0, 6),
        _ => LabelSet.Band,
    };

    private static string RandomChars(int n)
    {
        const string alphabet = "bcdfghjklmnpqrstvwxz2456789";
        return new string(Enumerable.Range(0, n).Select(_ => alphabet[Random.Shared.Next(alphabet.Length)]).ToArray());
    }

    public FleetStats Stats()
    {
        SimPod[] pods;
        long churned, exports, failures, retries;
        Dictionary<string, long> reasons;
        lock (_lock)
        {
            pods = [.. _pods];
            (churned, exports, failures, retries) = (_churned, _retiredExports, _retiredFailures, _retiredRetries);
            reasons = new(_retiredReasons);
        }
        double maxMs = 0;
        long points = 0;
        foreach (var p in pods)
        {
            var (e, f, r, ms, pts) = p.Export();
            exports += e; failures += f; retries += r;
            maxMs = Math.Max(maxMs, ms);
            points = Math.Max(points, pts);
            foreach (var (k, v) in p.FailureReasons()) reasons[k] = reasons.GetValueOrDefault(k) + v;
        }
        var rt = _rt == -2 ? "off" : LabelSet.RtText(_rt);
        return new FleetStats(pods.Length, _rate, rt, _extra, _churnSeconds, exports, failures, retries, maxMs, points, churned,
            _appended, reasons);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("fleet: {Count} simulated pods, {Rate}/s each, response_time={Rt}, extra series={Extra}, churn={Churn}s, OTLP={Endpoint}",
            _count, _rate, _rt, _extra, _churnSeconds, _endpoint);

        // Start the pods spread over one export interval, so their exports are out of phase like real pods.
        var startTask = Task.Run(async () =>
        {
            for (var i = 0; i < _count && !ct.IsCancellationRequested; i++)
            {
                var pod = NewPod();
                lock (_lock) _pods.Add(pod);
                await Task.Delay(Math.Max(1, _intervalMs / Math.Max(1, _count)), ct);
            }
        }, ct);

        using var churn = _churnSeconds > 0
            ? new Timer(_ => Churn(), null, TimeSpan.FromSeconds(_churnSeconds), TimeSpan.FromSeconds(_churnSeconds))
            : null;

        // Synthetic transaction stream: every 100 ms, each pod records rate × elapsed transactions.
        var sw = Stopwatch.StartNew();
        var last = sw.Elapsed;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct);
                var now = sw.Elapsed;
                var dt = (now - last).TotalSeconds;
                last = now;
                SimPod[] pods;
                lock (_lock) pods = [.. _pods];
                foreach (var p in pods) p.Tick(_rate * dt);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { await startTask; } catch (OperationCanceledException) { }
            lock (_lock) { foreach (var p in _pods) p.Dispose(); _pods.Clear(); }
        }
    }

    private SimPod NewPod() => new($"transactions-api-{_rs}-{RandomChars(5)}", _rt, _extra, _appended, _endpoint, _intervalMs, _timeoutMs);

    // A rollout: ~20% of pods go away and new ones (new names, new series) take their place. Retired pods
    // are disposed in parallel in the background (each dispose flushes a final export, up to the export
    // timeout), and a tick is skipped while the previous churn is still retiring pods.
    private void Churn()
    {
        if (Interlocked.Exchange(ref _churning, 1) == 1) return;
        List<SimPod> retired = [];
        lock (_lock)
        {
            for (var i = 0; i < _pods.Count; i++)
            {
                if (Random.Shared.Next(5) != 0) continue;
                retired.Add(_pods[i]);
                _pods[i] = NewPod();
                _churned++;
            }
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(retired.Select(p => Task.Run(() =>
                {
                    p.Dispose();
                    var (e, f, r, _, _) = p.Export();
                    var reasons = p.FailureReasons();
                    lock (_lock)
                    {
                        _retiredExports += e; _retiredFailures += f; _retiredRetries += r;
                        foreach (var (k, v) in reasons) _retiredReasons[k] = _retiredReasons.GetValueOrDefault(k) + v;
                    }
                })));
            }
            catch (Exception ex) { log.LogWarning(ex, "fleet: retiring churned pods failed"); }
            finally { Volatile.Write(ref _churning, 0); }
        });
    }
}

// One simulated pod: its own Meter + MeterProvider + OTLP exporter, as in a real pod.
sealed class SimPod : IDisposable
{
    private static readonly string[] Extras = Enumerable.Range(0, 10_000).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();

    private readonly string _name;
    private readonly int _rt;
    private readonly Meter _meter;
    private readonly MeterProvider _provider;
    private readonly ExportStats _stats;
    private readonly Counter<long> _tx;
    private readonly Histogram<double> _duration;
    private readonly Lock _lock = new();
    private double _due;

    private readonly KeyValuePair<string, object?>[] _appended;

    public SimPod(string name, int responseTime, int extraSeries, int appendedLabels, string endpoint, int intervalMs, int timeoutMs)
    {
        _name = name;
        _rt = responseTime;
        _appended = AppendedLabels.For(name, appendedLabels);
        _stats = new ExportStats(intervalMs, timeoutMs);
        var meterName = $"{TransactionMetrics.MeterName}.sim.{name}";
        _meter = new Meter(meterName);
        _tx = _meter.CreateCounter<long>(TransactionMetrics.TransactionsName, description: "Transactions processed");
        _duration = _meter.CreateHistogram<double>(TransactionMetrics.DurationName, unit: "s", description: "Transaction response time");
        if (extraSeries > 0)
        {
            var tags = Extras[..extraSeries].Select(s => new KeyValuePair<string, object?>("series", s)).ToArray();
            _meter.CreateObservableGauge("sim.other.metrics", () =>
                tags.Select(t => new Measurement<double>(Random.Shared.NextDouble(), t)),
                description: "Stand-in for the other metrics (runtime, HTTP server/client, dependencies) a real pod exports");
        }

        var exporter = new OtlpMetricExporter(new OtlpExporterOptions
        {
            Endpoint = new Uri(endpoint + "/v1/metrics"),
            Protocol = OtlpExportProtocol.HttpProtobuf,
            TimeoutMilliseconds = timeoutMs,
            HttpClientFactory = () => new HttpClient(new ExportHttpHandler(_stats) { InnerHandler = new HttpClientHandler() })
            {
                Timeout = TimeSpan.FromMilliseconds(timeoutMs),
            },
        });
        _provider = Sdk.CreateMeterProviderBuilder()
            .ConfigureResource(r => r.AddService("transactions-api", serviceInstanceId: name))
            .AddMeter(meterName)
            .AddView(TransactionMetrics.DurationName, new ExplicitBucketHistogramConfiguration { Boundaries = TransactionMetrics.DurationBoundaries })
            .AddReader(new PeriodicExportingMetricReader(new MeasuredExporter(exporter, _stats), intervalMs, timeoutMs)
            {
                TemporalityPreference = MetricReaderTemporalityPreference.Cumulative,
            })
            .Build()!;
    }

    public (long Exports, long Failures, long Retries, double LastExportMs, long Points) Export()
    {
        var s = _stats.ReadAndReset();
        return (s.Exports, s.Failures, s.HttpRetries, s.LastExportMs, s.LastPoints);
    }

    public Dictionary<string, long> FailureReasons() => _stats.FailureReasons();

    // Record the transactions that are due (fractional rates accumulate across ticks).
    public void Tick(double transactions)
    {
        int n;
        lock (_lock)
        {
            _due += transactions;
            n = (int)_due;
            _due -= n;
        }
        for (var i = 0; i < n; i++) RecordOne();
    }

    private void RecordOne()
    {
        var consumeType = Transactions.ConsumeTypes[Random.Shared.Next(Transactions.ConsumeTypes.Length)];
        var product = Transactions.Products[Random.Shared.Next(Transactions.Products.Length)];
        var state = Random.Shared.Next(100) switch { < 85 => "completed", < 92 => "pending", < 97 => "declined", _ => "failed" };
        // Same shape as the real replicas: 3–60 ms downstream delay plus ~1.7 ms of CPU.
        var seconds = (Random.Shared.Next(3, 60) + 1.7 + Random.Shared.NextDouble()) / 1000.0;

        var durationTags = new TagList { { "consume_type", consumeType }, { "state", state }, { "product", product } };
        foreach (var a in _appended) durationTags.Add(a);
        _duration.Record(seconds, durationTags);
        var tags = new TagList { { "consume_type", consumeType }, { "state", state }, { "product", product }, { "pod", _name } };
        foreach (var a in _appended) tags.Add(a);
        if (_rt == LabelSet.Band) tags.Add("response_time", LabelSet.BandOf(seconds));
        else if (_rt >= 0) tags.Add("response_time", Math.Round(seconds, _rt).ToString("F" + _rt, CultureInfo.InvariantCulture));
        _tx.Add(1, tags);
    }

    public void Dispose()
    {
        _provider.Dispose(); // flushes a final export, like a pod shutting down
        _meter.Dispose();
    }
}
