// transactions-driver role: the load-test console.
//   GET  /                     home page (wwwroot/index.html) with the Start/Stop buttons
//   POST /api/test/start       body: TestConfig. Starts a stepped ramp of the transaction rate
//   POST /api/test/stop
//   POST /api/mode?value=...   bounded | unbounded, applied to every api replica (and re-applied
//                              to replicas that restart, e.g. after an OOMKill)
//   GET  /api/test/status?since=N   live results for the page
//
// Load model: open (arrival-rate). Requests are fired at the target rate regardless of how slow the
// system gets, as real users would. Each has a client-side timeout (default 3s). When the system
// degrades, in-flight requests pile up, latency climbs to the timeout and then errors take over.
//
// Configuration: TARGET_URL, REPLICA_DNS (headless Service of the api), PROMETHEUS_URL.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;

static class Driver
{
    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<ReplicaMonitor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ReplicaMonitor>());
        builder.Services.AddSingleton<PipelineMonitor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<PipelineMonitor>());
        builder.Services.AddSingleton<LoadTest>();
    }

    public static void Map(WebApplication app)
    {
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGet("/healthz", () => Results.Ok("ok"));

        app.MapPost("/api/test/start", (TestConfig config, LoadTest test) =>
            test.Start(config) ? Results.Ok(new { started = true }) : Results.Conflict(new { error = "a test is already running" }));

        app.MapPost("/api/test/stop", (LoadTest test) =>
        {
            test.Stop("stopped by user");
            return Results.Ok(new { stopping = true });
        });

        app.MapPost("/api/test/dryrun", (TestConfig config, LoadTest test) =>
            test.Start(DryRunPreset.Apply(config)) ? Results.Ok(new { started = true }) : Results.Conflict(new { error = "a test is already running" }));

        app.MapPost("/api/mode", async (string value, ReplicaMonitor replicas, LoadTest test) =>
        {
            if (value is not ("bounded" or "unbounded")) return Results.BadRequest(new { error = "bounded | unbounded" });
            if (test.DryRunActive && value != "bounded") return Results.Conflict(new { error = "labels are locked to bounded during a dry run" });
            var applied = await replicas.ApplyModeAsync(value);
            return Results.Ok(new { mode = value, applied });
        });

        app.MapGet("/api/test/status", (int? since, LoadTest test, ReplicaMonitor replicas, PipelineMonitor pipeline) =>
            Results.Json(new
            {
                test = test.Status(since ?? 0),
                replicas = new { desiredMode = replicas.DesiredMode, observedMode = replicas.ObservedMode, pods = replicas.Latest },
                replicaHistory = replicas.HistorySince(test.StartedAtUnix),
                pipeline = pipeline.Latest,
                pipelineHistory = pipeline.HistorySince(test.StartedAtUnix),
            }));
    }
}

sealed record TestConfig(
    int StartRate = 50,
    int StepRate = 50,
    int StepSeconds = 30,
    int MaxRate = 2000,
    int TimeoutMs = 3000,
    int DegradedP95Ms = 1000,
    double StopErrorPct = 50,
    int MaxInFlight = 6000,
    bool DryRun = false);

// Dry run: a short, safe pass (bounded labels, low rate, strict auto-stop) that exercises every hop
// and then checks the pipeline end to end, without pushing anything toward a crash.
static class DryRunPreset
{
    public static TestConfig Apply(TestConfig c) => c with
    {
        DryRun = true, StartRate = 20, StepRate = 20, StepSeconds = 12, MaxRate = 100, StopErrorPct = 5,
    };
}

sealed record Check(string Name, bool Ok, string Detail);

sealed record Point(long T, long Ts, int Target, int Sent, int Ok, int Timeouts, int HttpErrors, int ConnErrors,
    int Dropped, double P50, double P95, double P99, int InFlight);

sealed record StepResult(int Rate, long StartT, long EndT, int Ok, int Failed, double ErrorPct, double P95);

sealed record Milestone(long T, int Rate, string What);

sealed record RunSummary(string StartedAt, string Mode, long DurationSeconds, int MaxTargetRate, int PeakOkPerSec,
    Milestone? Degraded, Milestone? FirstTimeout, Milestone? Errors5, Milestone? Errors50, string? StopReason, TestConfig Config);

sealed class Bucket
{
    public int Target, Sent, Ok, Timeouts, HttpErrors, ConnErrors, Dropped;
    public readonly ConcurrentQueue<float> Latencies = new();
}

sealed class LoadTest
{
    private readonly HttpClient _http;
    private readonly string _target;
    private readonly ReplicaMonitor _replicas;
    private readonly PipelineMonitor _pipeline;
    private readonly ILogger<LoadTest> _log;
    private List<Check> _checks = [];
    private bool _checksForDryRun;
    private readonly ConcurrentDictionary<long, Bucket> _buckets = new();
    private readonly List<Point> _points = [];
    private readonly List<StepResult> _steps = [];
    private readonly List<RunSummary> _history = [];
    private readonly Lock _lock = new();
    private readonly Timer _finalizer;

    private CancellationTokenSource? _cts;
    private TestConfig? _config;
    private volatile int _targetRate;
    private int _inFlight;
    private long _startedTs;
    private volatile string _state = "idle";
    private string? _stopReason;
    private string _modeAtStart = "?";
    private Milestone? _degraded, _firstTimeout, _errors5, _errors50;

    public LoadTest(IConfiguration cfg, ReplicaMonitor replicas, PipelineMonitor pipeline, ILogger<LoadTest> log)
    {
        _replicas = replicas;
        _pipeline = pipeline;
        _log = log;
        _target = (cfg["TARGET_URL"] ?? "http://localhost:8080").TrimEnd('/');
        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromSeconds(15), // re-spread connections across replicas
            ConnectTimeout = TimeSpan.FromSeconds(2),
            MaxConnectionsPerServer = 20_000,
        })
        { Timeout = Timeout.InfiniteTimeSpan }; // per-request timeout below
        _finalizer = new Timer(_ => FinalizeBuckets(false), null, 500, 500);
    }

    public long StartedAtUnix => _startedTs;
    public bool DryRunActive => _state != "idle" && _config?.DryRun == true;

    public bool Start(TestConfig c)
    {
        lock (_lock)
        {
            if (_state != "idle") return false;
            _config = c with
            {
                StartRate = Math.Clamp(c.StartRate, 1, 20_000),
                StepRate = Math.Clamp(c.StepRate, 0, 5_000),
                StepSeconds = Math.Clamp(c.StepSeconds, 5, 600),
                MaxRate = Math.Clamp(c.MaxRate, 1, 20_000),
                TimeoutMs = Math.Clamp(c.TimeoutMs, 100, 60_000),
                DegradedP95Ms = Math.Clamp(c.DegradedP95Ms, 10, 60_000),
                StopErrorPct = Math.Clamp(c.StopErrorPct, 1, 100),
                MaxInFlight = Math.Clamp(c.MaxInFlight, 10, 50_000),
            };
            _buckets.Clear();
            _points.Clear();
            _steps.Clear();
            _degraded = _firstTimeout = _errors5 = _errors50 = null;
            _stopReason = null;
            _checks = [];
            _checksForDryRun = _config.DryRun;
            _startedTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _modeAtStart = _config.DryRun ? "bounded" : _replicas.DesiredMode ?? _replicas.ObservedMode;
            _state = "running";
            _cts = new CancellationTokenSource();
            var config = _config;
            var ct = _cts.Token;
            _ = Task.Run(() => RunAsync(config, ct));
            _log.LogInformation("Load test started: {Config} mode={Mode}", config, _modeAtStart);
            return true;
        }
    }

    public void Stop(string reason)
    {
        lock (_lock)
        {
            if (_state != "running") return;
            _stopReason ??= reason;
            _cts?.Cancel();
        }
    }

    private async Task RunAsync(TestConfig cfg, CancellationToken ct)
    {
        if (cfg.DryRun) await _replicas.ApplyModeAsync("bounded"); // never explode during a dry run
        var rate = cfg.StartRate;
        _targetRate = rate;
        var sw = Stopwatch.StartNew();
        double last = 0, carry = 0, nextStepAt = cfg.StepSeconds;
        var stepStartT = 0L;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = sw.Elapsed.TotalSeconds;
                carry += rate * (now - last);
                last = now;
                var n = (int)carry;
                carry -= n;

                var bucket = BucketFor(NowUnix());
                bucket.Target = rate;
                for (var i = 0; i < n; i++)
                {
                    if (Volatile.Read(ref _inFlight) >= cfg.MaxInFlight)
                    {
                        Interlocked.Increment(ref bucket.Dropped); // driver can't keep up: counts as failure
                        continue;
                    }
                    Interlocked.Increment(ref _inFlight);
                    Interlocked.Increment(ref bucket.Sent);
                    _ = SendOneAsync(cfg.TimeoutMs);
                }

                if (now >= nextStepAt)
                {
                    var endT = NowUnix() - _startedTs;
                    var step = EvaluateStep(rate, stepStartT, endT);
                    if (step.ErrorPct >= cfg.StopErrorPct)
                    {
                        Stop($"error rate {step.ErrorPct:F0}% ≥ {cfg.StopErrorPct:F0}% at {rate} req/s");
                        break;
                    }
                    if (rate >= cfg.MaxRate)
                    {
                        Stop($"completed the {cfg.MaxRate} req/s step (max rate)");
                        break;
                    }
                    rate = Math.Min(cfg.MaxRate, rate + Math.Max(1, cfg.StepRate));
                    _targetRate = rate;
                    stepStartT = endT;
                    nextStepAt += cfg.StepSeconds;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Load test loop failed");
            lock (_lock) _stopReason ??= "driver error: " + ex.Message;
        }

        _state = "draining";
        _targetRate = 0;
        var deadline = DateTime.UtcNow.AddMilliseconds(cfg.TimeoutMs + 2000);
        while (Volatile.Read(ref _inFlight) > 0 && DateTime.UtcNow < deadline) await Task.Delay(100);
        await Task.Delay(1100);
        FinalizeBuckets(true);

        if (cfg.DryRun)
        {
            _state = "checking";
            try
            {
                var checks = await RunChecksAsync(cfg);
                lock (_lock) _checks = checks;
            }
            catch (Exception ex)
            {
                lock (_lock) _checks = [new Check("Dry-run checks", false, "check runner failed: " + ex.Message)];
            }
        }

        lock (_lock)
        {
            List<Point> pts;
            lock (_points) pts = [.. _points];
            _history.Insert(0, new RunSummary(
                DateTimeOffset.FromUnixTimeSeconds(_startedTs).ToString("u", CultureInfo.InvariantCulture),
                cfg.DryRun ? $"dry run ({_checks.Count(c => c.Ok)}/{_checks.Count} checks)" : _modeAtStart,
                pts.Count == 0 ? 0 : pts[^1].T,
                pts.Count == 0 ? 0 : pts.Max(p => p.Target),
                pts.Count == 0 ? 0 : pts.Max(p => p.Ok),
                _degraded, _firstTimeout, _errors5, _errors50, _stopReason, cfg));
            if (_history.Count > 20) _history.RemoveAt(_history.Count - 1);
            _state = "idle";
        }
        _log.LogInformation("Load test finished: {Reason}", _stopReason);
    }

    private async Task<List<Check>> RunChecksAsync(TestConfig cfg)
    {
        List<Point> pts;
        lock (_points) pts = [.. _points];
        var ok = pts.Sum(p => p.Ok);
        var failed = pts.Sum(Failed);
        var errPct = ok + failed == 0 ? 100 : 100.0 * failed / (ok + failed);
        var p95 = pts.Count == 0 ? 0 : pts.Max(p => p.P95);
        var reps = _replicas.Latest;
        var reachable = reps.Count(r => r.Reachable);
        var unbounded = reps.Where(r => r.Reachable && r.Stats?.Mode != "bounded").Select(r => r.Stats?.Pod).ToList();

        var checks = new List<Check>
        {
            new("Driver → transactions-api", ok > 0 && errPct < 1,
                $"{ok:N0} succeeded, {failed:N0} failed ({errPct:F2}%)"),
            new("Latency healthy", ok > 0 && p95 < cfg.DegradedP95Ms,
                $"worst 1s p95 {p95:F0} ms (threshold {cfg.DegradedP95Ms} ms)"),
            new("Replicas reachable", reps.Count > 0 && reachable == reps.Count,
                $"{reachable}/{reps.Count} replicas answering /admin/stats"),
            new("Labels bounded", reps.Count > 0 && unbounded.Count == 0,
                unbounded.Count == 0 ? "no replica is emitting response_time" : "still unbounded: " + string.Join(", ", unbounded)),
        };
        checks.AddRange(await _pipeline.RunChecksAsync());
        return checks;
    }

    private async Task SendOneAsync(int timeoutMs)
    {
        var start = Stopwatch.GetTimestamp();
        var url = $"{_target}/api/transactions?product={Transactions.Products[Random.Shared.Next(Transactions.Products.Length)]}" +
                  $"&consumeType={Transactions.ConsumeTypes[Random.Shared.Next(Transactions.ConsumeTypes.Length)]}";
        using var cts = new CancellationTokenSource(timeoutMs);
        int outcome; // 0 ok, 1 timeout, 2 http error, 3 connection error
        try
        {
            using var resp = await _http.PostAsync(url, null, cts.Token);
            await resp.Content.CopyToAsync(Stream.Null, cts.Token);
            outcome = resp.IsSuccessStatusCode ? 0 : 2;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { outcome = 1; }
        catch (Exception) { outcome = 3; } // refused / reset (pod OOMKilled, not ready, ...)
        finally { Interlocked.Decrement(ref _inFlight); }

        var ms = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var b = BucketFor(NowUnix());
        switch (outcome)
        {
            case 0: Interlocked.Increment(ref b.Ok); b.Latencies.Enqueue(ms); break;
            case 1: Interlocked.Increment(ref b.Timeouts); b.Latencies.Enqueue(timeoutMs); break;
            case 2: Interlocked.Increment(ref b.HttpErrors); b.Latencies.Enqueue(ms); break;
            default: Interlocked.Increment(ref b.ConnErrors); break;
        }
    }

    private Bucket BucketFor(long unixSecond) => _buckets.GetOrAdd(unixSecond, _ => new Bucket());

    private static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private void FinalizeBuckets(bool all)
    {
        if (_startedTs == 0) return;
        var cutoff = all ? long.MaxValue : NowUnix() - 1;
        foreach (var key in _buckets.Keys.Where(k => k < cutoff).Order().ToList())
        {
            if (!_buckets.TryRemove(key, out var b)) continue;
            var lat = b.Latencies.ToArray();
            Array.Sort(lat);
            var target = b.Target > 0 ? b.Target : _targetRate;
            var p = new Point(key - _startedTs, key, target, b.Sent, b.Ok, b.Timeouts, b.HttpErrors, b.ConnErrors, b.Dropped,
                Pct(lat, 0.50), Pct(lat, 0.95), Pct(lat, 0.99), Volatile.Read(ref _inFlight));
            lock (_points) _points.Add(p);
            CheckMilestones(p);
        }
    }

    private static double Pct(float[] sorted, double q) =>
        sorted.Length == 0 ? 0 : Math.Round(sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)], 1);

    private static int Failed(Point p) => p.Timeouts + p.HttpErrors + p.ConnErrors + p.Dropped;

    private void CheckMilestones(Point p)
    {
        var cfg = _config;
        if (cfg is null) return;
        var done = p.Ok + Failed(p);
        var errPct = done == 0 ? 0 : 100.0 * Failed(p) / done;
        lock (_lock)
        {
            if (_degraded is null && p.P95 >= cfg.DegradedP95Ms && done > 0)
                _degraded = new Milestone(p.T, p.Target, $"p95 {p.P95:F0} ms ≥ {cfg.DegradedP95Ms} ms");
            if (_firstTimeout is null && p.Timeouts > 0)
                _firstTimeout = new Milestone(p.T, p.Target, $"{p.Timeouts} request(s) exceeded {cfg.TimeoutMs} ms");
            if (_errors5 is null && errPct >= 5)
                _errors5 = new Milestone(p.T, p.Target, $"{errPct:F1}% failed");
            if (_errors50 is null && errPct >= 50)
                _errors50 = new Milestone(p.T, p.Target, $"{errPct:F1}% failed");
        }
    }

    private StepResult EvaluateStep(int rate, long startT, long endT)
    {
        List<Point> pts;
        lock (_points) pts = _points.Where(p => p.T >= startT && p.T < endT).ToList();
        var ok = pts.Sum(p => p.Ok);
        var failed = pts.Sum(Failed);
        var step = new StepResult(rate, startT, endT, ok, failed,
            ok + failed == 0 ? 0 : Math.Round(100.0 * failed / (ok + failed), 2),
            pts.Count == 0 ? 0 : pts.Max(p => p.P95));
        lock (_lock) _steps.Add(step);
        return step;
    }

    public object Status(int since)
    {
        List<Point> pts;
        lock (_points) pts = since < _points.Count ? _points.GetRange(Math.Max(0, since), _points.Count - Math.Max(0, since)) : [];
        List<Point> recent;
        lock (_points) recent = _points.TakeLast(5).ToList();
        var ok = recent.Sum(p => p.Ok);
        var failed = recent.Sum(Failed);
        lock (_lock)
        {
            return new
            {
                state = _state,
                dryRun = _config?.DryRun == true,
                checks = _checks,
                checksForDryRun = _checksForDryRun,
                startedAtUnix = _startedTs,
                config = _config,
                mode = _modeAtStart,
                targetRate = _targetRate,
                inFlight = Volatile.Read(ref _inFlight),
                stopReason = _stopReason,
                current = new
                {
                    okPerSec = recent.Count == 0 ? 0 : ok / (double)recent.Count,
                    failedPerSec = recent.Count == 0 ? 0 : failed / (double)recent.Count,
                    errorPct = ok + failed == 0 ? 0 : 100.0 * failed / (ok + failed),
                    p95 = recent.Count == 0 ? 0 : recent.Max(p => p.P95),
                    p99 = recent.Count == 0 ? 0 : recent.Max(p => p.P99),
                },
                milestones = new { degraded = _degraded, firstTimeout = _firstTimeout, errors5 = _errors5, errors50 = _errors50 },
                steps = _steps.ToList(),
                history = _history.ToList(),
                pointsFrom = Math.Max(0, since),
                points = pts,
            };
        }
    }
}

sealed class ReplicaView
{
    public string Ip { get; init; } = "";
    public bool Reachable { get; set; }
    public string? LastError { get; set; }
    public int RestartsSeen { get; set; }
    public double? CpuCores { get; set; }
    public double? ThrottledPct { get; set; }
    public ReplicaSnapshot? Stats { get; set; }
    public long LastSeenUnix { get; set; }
}

sealed record ReplicaSample(long Ts, string Pod, long MemoryBytes, double? CpuCores, double? ThrottledPct, bool Reachable);

// Polls every api replica directly (headless Service DNS) every 2s. Works even when Prometheus is down.
sealed class ReplicaMonitor(IConfiguration cfg, ILogger<ReplicaMonitor> log) : BackgroundService
{
    private readonly string _dns = cfg["REPLICA_DNS"] ?? "localhost";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(1.5) };
    private readonly ConcurrentDictionary<string, ReplicaView> _views = new();
    private readonly List<ReplicaSample> _history = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public volatile string? DesiredMode;

    public List<ReplicaView> Latest => _views.Values.OrderBy(v => v.Stats?.Pod ?? v.Ip).ToList();

    public string ObservedMode
    {
        get
        {
            var modes = _views.Values.Where(v => v.Reachable && v.Stats is not null).Select(v => v.Stats!.Mode).Distinct().ToList();
            return modes.Count switch { 0 => "unknown", 1 => modes[0], _ => "mixed" };
        }
    }

    public List<ReplicaSample> HistorySince(long ts)
    {
        lock (_history) return ts == 0 ? [] : _history.Where(s => s.Ts >= ts).ToList();
    }

    public async Task<int> ApplyModeAsync(string mode)
    {
        DesiredMode = mode;
        var results = await Task.WhenAll(_views.Keys.Select(ip => SetModeAsync(ip, mode)));
        return results.Count(ok => ok);
    }

    private async Task<bool> SetModeAsync(string ip, string mode)
    {
        try
        {
            using var r = await _http.PostAsync($"http://{ip}:8080/admin/mode?value={mode}", null);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PollAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning(ex, "Replica poll failed"); }
            await Task.Delay(2000, ct);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        string[] ips;
        try { ips = (await Dns.GetHostAddressesAsync(_dns, ct)).Select(a => a.ToString()).ToArray(); }
        catch { ips = []; }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var gone in _views.Keys.Except(ips).ToList())
        {
            if (_views.TryGetValue(gone, out var v)) { v.Reachable = false; v.LastError = "not in DNS"; }
            if (v is not null && now - v.LastSeenUnix > 60) _views.TryRemove(gone, out _);
        }
        await Task.WhenAll(ips.Select(ip => PollOneAsync(ip, ct)));

        lock (_history)
        {
            foreach (var v in _views.Values)
                _history.Add(new ReplicaSample(now, v.Stats?.Pod ?? v.Ip, v.Reachable ? v.Stats?.MemoryCurrentBytes ?? 0 : 0,
                    v.Reachable ? v.CpuCores : null, v.Reachable ? v.ThrottledPct : null, v.Reachable));
            if (_history.Count > 20_000) _history.RemoveRange(0, _history.Count - 20_000);
        }
    }

    private async Task PollOneAsync(string ip, CancellationToken ct)
    {
        var view = _views.GetOrAdd(ip, _ => new ReplicaView { Ip = ip });
        try
        {
            var snap = await _http.GetFromJsonAsync<ReplicaSnapshot>($"http://{ip}:8080/admin/stats", Json, ct)
                       ?? throw new InvalidOperationException("empty stats");
            var prev = view.Stats;
            if (prev is not null && prev.Pod == snap.Pod)
            {
                if (snap.UptimeSeconds < prev.UptimeSeconds) view.RestartsSeen++; // container restarted (e.g. OOMKilled)
                var dt = (snap.SampledAtUnixMs - prev.SampledAtUnixMs) / 1000.0;
                if (dt > 0 && snap.CpuUsageUsec >= prev.CpuUsageUsec)
                {
                    view.CpuCores = Math.Round((snap.CpuUsageUsec - prev.CpuUsageUsec) / 1e6 / dt, 3);
                    var periods = snap.CpuPeriods - prev.CpuPeriods;
                    view.ThrottledPct = periods > 0 ? Math.Round(100.0 * (snap.CpuThrottledPeriods - prev.CpuThrottledPeriods) / periods, 1) : 0;
                }
            }
            view.Stats = snap;
            view.Reachable = true;
            view.LastError = null;
            view.LastSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Restarted replicas come back with the env default; keep the chosen mode applied.
            var desired = DesiredMode;
            if (desired is not null && snap.Mode != desired) await SetModeAsync(ip, desired);
        }
        catch (Exception ex)
        {
            view.Reachable = false;
            view.LastError = ex is TaskCanceledException ? "timeout" : ex.GetType().Name.Replace("Exception", "");
        }
    }
}

sealed record PipelineSample(long Ts, double? HeadSeries, double? TxSeries, double? PromMemory, double? AlloyMemory);

// Polls Prometheus every 10s. When Prometheus is down (OOMKilled), that is itself the signal.
sealed class PipelineMonitor(IConfiguration cfg, ILogger<PipelineMonitor> log) : BackgroundService
{
    private readonly string _url = (cfg["PROMETHEUS_URL"] ?? "http://localhost:9090").TrimEnd('/');
    private readonly string _grafana = (cfg["GRAFANA_URL"] ?? "http://localhost:3000").TrimEnd('/');
    private readonly string _grafanaAuth = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
        $"{cfg["GRAFANA_USER"] ?? "admin"}:{cfg["GRAFANA_PASSWORD"] ?? "admin"}"));
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };

    // End-to-end checks for the dry run: app -> OTLP -> Alloy -> remote_write -> Prometheus -> Grafana.
    public async Task<List<Check>> RunChecksAsync()
    {
        var ct = CancellationToken.None;
        var checks = new List<Check>();
        async Task Add(string name, Func<Task<(bool ok, string detail)>> run)
        {
            try { var (ok, detail) = await run(); checks.Add(new Check(name, ok, detail)); }
            catch (Exception ex) { checks.Add(new Check(name, false, ex is TaskCanceledException ? "timed out" : ex.Message)); }
        }

        await Add("Prometheus reachable", async () =>
            (await ScalarAsync("vector(1)", ct) == 1, _url));
        await Add("Alloy receiving OTLP from the app", async () =>
        {
            var r = await ScalarAsync("sum(rate(otelcol_receiver_accepted_metric_points_total[1m]))", ct) ?? 0;
            var refused = await ScalarAsync("sum(rate(otelcol_receiver_refused_metric_points_total[1m]))", ct) ?? 0;
            return (r > 0 && refused == 0, $"{r:N0} points/s accepted, {refused:N0}/s refused");
        });
        await Add("Alloy → Prometheus remote_write", async () =>
        {
            var sent = await ScalarAsync("sum(rate(prometheus_remote_storage_samples_total[1m]))", ct) ?? 0;
            var failed = await ScalarAsync("sum(rate(prometheus_remote_storage_samples_failed_total[2m]))", ct) ?? 0;
            return (sent > 0 && failed == 0, $"{sent:N0} samples/s sent, {failed:N0}/s failed");
        });
        await Add("Transactions visible in Prometheus", async () =>
        {
            var age = await ScalarAsync("time() - max(timestamp(http_transactions_total))", ct);
            var series = await ScalarAsync("count(http_transactions_total)", ct);
            var pods = await ScalarAsync("count(count by (pod) (http_transactions_total))", ct);
            return (age is < 45, age is null ? "no http_transactions_total data" :
                $"newest sample {age:F0}s old · {series:N0} series · {pods:N0} pod label values");
        });
        await Add("Prometheus memory headroom", async () =>
        {
            const string sel = "namespace=\"monitoring\",container=\"prometheus\"";
            var mem = await ScalarAsync($"max(container_memory_working_set_bytes{{{sel}}})", ct);
            var limit = await ScalarAsync($"max(kube_pod_container_resource_limits{{{sel},resource=\"memory\"}})", ct);
            if (mem is null) return (false, "no memory data");
            if (limit is null) return (true, $"{mem / 1048576:N0} MiB used, no limit (starvation profile)");
            var pct = 100 * mem.Value / limit.Value;
            return (pct < 70, $"{mem / 1048576:N0} / {limit / 1048576:N0} MiB ({pct:F0}%)");
        });
        await Add("Grafana dashboard", async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_grafana}/api/dashboards/uid/cardinality-repro");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", _grafanaAuth);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                return (false, "Grafana is up, but the driver's login was rejected (admin password changed?). " +
                               "Set GRAFANA_PASSWORD on deploy/transactions-driver, or reset the Grafana admin password.");
            return (resp.IsSuccessStatusCode, resp.IsSuccessStatusCode
                ? "\"Cardinality Repro\" is provisioned" : $"HTTP {(int)resp.StatusCode} from Grafana");
        });
        return checks;
    }
    private readonly List<PipelineSample> _history = [];

    public object? Latest { get; private set; }

    public List<PipelineSample> HistorySince(long ts)
    {
        lock (_history) return ts == 0 ? [] : _history.Where(s => s.Ts >= ts).ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PollAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Latest = new { up = false, error = ex is TaskCanceledException ? "timeout" : ex.Message, sampledAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                lock (_history) _history.Add(new PipelineSample(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, null, null, null));
                log.LogDebug(ex, "Prometheus poll failed");
            }
            await Task.Delay(10_000, ct);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        const string prom = "namespace=\"monitoring\",container=\"prometheus\"";
        const string alloy = "namespace=\"alloy\",container=\"alloy\"";
        var head = await ScalarAsync("max(prometheus_tsdb_head_series)", ct);
        var tx = await ScalarAsync("count(http_transactions_total)", ct);
        var promMem = await ScalarAsync($"max(container_memory_working_set_bytes{{{prom}}})", ct);
        var promLimit = await ScalarAsync($"max(kube_pod_container_resource_limits{{{prom},resource=\"memory\"}})", ct);
        var promRestarts = await ScalarAsync($"max(kube_pod_container_status_restarts_total{{{prom}}})", ct);
        var alloyMem = await ScalarAsync($"max(container_memory_working_set_bytes{{{alloy}}})", ct);
        var alloyLimit = await ScalarAsync($"max(kube_pod_container_resource_limits{{{alloy},resource=\"memory\"}})", ct);
        var promNode = await LabelAsync("max by (node) (kube_pod_info{namespace=\"monitoring\",pod=~\"prometheus-.*\"})", "node", ct);
        var nodes = await VectorAsync(
            "sum by (node) (container_memory_working_set_bytes{container!=\"\",container!=\"POD\"}) / on (node) max by (node) (kube_node_status_capacity{resource=\"memory\"})",
            "node", ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Latest = new
        {
            up = true, sampledAt = now, headSeries = head, txSeries = tx,
            prometheus = new { memory = promMem, limit = promLimit, restarts = promRestarts, node = promNode },
            alloy = new { memory = alloyMem, limit = alloyLimit },
            nodeMemoryPct = nodes,
        };
        lock (_history)
        {
            _history.Add(new PipelineSample(now, head, tx, promMem, alloyMem));
            if (_history.Count > 5_000) _history.RemoveRange(0, _history.Count - 5_000);
        }
    }

    private async Task<JsonElement> QueryAsync(string q, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{_url}/api/v1/query?query={Uri.EscapeDataString(q)}", ct));
        return doc.RootElement.GetProperty("data").GetProperty("result").Clone();
    }

    private async Task<double?> ScalarAsync(string q, CancellationToken ct)
    {
        var r = await QueryAsync(q, ct);
        return r.GetArrayLength() == 0 ? null : double.Parse(r[0].GetProperty("value")[1].GetString()!, CultureInfo.InvariantCulture);
    }

    private async Task<string?> LabelAsync(string q, string label, CancellationToken ct)
    {
        var r = await QueryAsync(q, ct);
        return r.GetArrayLength() == 0 ? null : r[0].GetProperty("metric").TryGetProperty(label, out var v) ? v.GetString() : null;
    }

    private async Task<Dictionary<string, double>> VectorAsync(string q, string label, CancellationToken ct)
    {
        var r = await QueryAsync(q, ct);
        var d = new Dictionary<string, double>();
        foreach (var e in r.EnumerateArray())
            if (e.GetProperty("metric").TryGetProperty(label, out var l))
                d[l.GetString()!] = Math.Round(100 * double.Parse(e.GetProperty("value")[1].GetString()!, CultureInfo.InvariantCulture), 1);
        return d;
    }
}
