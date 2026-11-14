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
        builder.Services.AddSingleton<RunStore>();
        builder.Services.AddSingleton<KubeClient>();
        builder.Services.AddSingleton<EvidenceCollector>();
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

        app.MapGet("/api/runs", (RunStore store) => Results.Json(store.Summaries()));

        // Self-contained HTML report for a run. ?download=1 saves it as a file.
        app.MapGet("/api/runs/{id}/report", (string id, bool? download, RunStore store) =>
        {
            var path = store.ReportPath(id);
            if (path is null) return Results.NotFound(new { error = "no report for that run" });
            return download == true
                ? Results.File(path, "text/html; charset=utf-8", $"{id}.html")
                : Results.File(path, "text/html; charset=utf-8");
        });

        // Hypothesis presets: reset everything, apply labels, then start the given test.
        app.MapPost("/api/test/prepared", (PreparedRequest req, LoadTest test) =>
            test.Prepare(req.Config, req.Labels) ? Results.Ok(new { preparing = true }) : Results.Conflict(new { error = "a test is already running or preparing" }));

        // Reset everything without starting a test (labels default to the current ones).
        app.MapPost("/api/reset", (LabelConfig? labels, LoadTest test, ReplicaMonitor replicas) =>
            test.Prepare(null, labels ?? replicas.CurrentLabels) ? Results.Ok(new { preparing = true }) : Results.Conflict(new { error = "a test is already running or preparing" }));

        // Re-run the analysis (findings, headline, report) of a stored run with the current rules.
        app.MapPost("/api/runs/{id}/reanalyze", (string id, LoadTest test) =>
            test.Reanalyze(id) is { } h ? Results.Ok(new { id, headline = h }) : Results.NotFound(new { error = "no such run" }));

        app.MapPost("/api/test/dryrun", (TestConfig config, LoadTest test) =>
            test.Start(DryRunPreset.Apply(config)) ? Results.Ok(new { started = true }) : Results.Conflict(new { error = "a test is already running" }));

        // Presets: bounded (no response_time) | unbounded (all labels, response_time at 6 decimals).
        app.MapPost("/api/mode", async (string value, ReplicaMonitor replicas, LoadTest test) =>
        {
            if (value is not ("bounded" or "unbounded")) return Results.BadRequest(new { error = "bounded | unbounded" });
            if (test.DryRunActive && value != "bounded") return Results.Conflict(new { error = "labels are locked to bounded during a dry run" });
            var applied = await replicas.ApplyLabelsAsync(Labels.ForMode(value));
            return Results.Ok(new { mode = value, applied });
        });

        // Fine-grained: which labels http_transactions_total carries, and response_time precision.
        app.MapPost("/api/labels", async (LabelConfig config, ReplicaMonitor replicas, LoadTest test) =>
        {
            var c = Labels.Normalize(config);
            if (test.DryRunActive && Labels.IsHighCardinality(c))
                return Results.Conflict(new { error = "response_time and GUID labels are locked off during a dry run" });
            var applied = await replicas.ApplyLabelsAsync(c);
            return Results.Ok(new { labels = c, description = Labels.Describe(c), applied });
        });

        // Restart the api replicas (rollout restart). Clears the SDK's in-memory series, which removing a
        // label does not: cumulative state keeps every series already created until the process restarts.
        app.MapPost("/api/replicas/restart", async (KubeClient kube, LoadTest test) =>
        {
            if (test.DryRunActive) return Results.Conflict(new { error = "not during a dry run" });
            if (!kube.Available) return Results.Problem("Kubernetes API not available to the driver");
            try
            {
                await kube.RolloutRestartAsync("demo", "transactions-api");
                return Results.Ok(new { restarting = true });
            }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        });

        app.MapGet("/api/test/status", (int? since, LoadTest test, ReplicaMonitor replicas, PipelineMonitor pipeline) =>
            Results.Json(new
            {
                test = test.Status(since ?? 0),
                replicas = new
                {
                    desiredLabels = replicas.DesiredLabels,
                    observed = replicas.ObservedDescription,
                    current = replicas.CurrentLabels,
                    currentDescription = Labels.Describe(replicas.CurrentLabels),
                    pods = replicas.Latest,
                },
                replicaHistory = replicas.HistorySince(test.StartedAtUnix),
                pipeline = pipeline.Latest,
                pipelineHistory = pipeline.HistorySince(test.StartedAtUnix),
            }));
    }
}

sealed record PreparedRequest(TestConfig Config, LabelConfig Labels);

sealed record TestConfig(
    int StartRate = 50,
    int StepRate = 50,
    int StepSeconds = 30,
    int MaxRate = 2000,
    int TimeoutMs = 3000,
    int DegradedP95Ms = 1000,
    double StopErrorPct = 50,
    int MaxInFlight = 6000,
    bool DryRun = false,
    string? Name = null,
    string? Notes = null);

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
    private readonly KubeClient _kube;
    private volatile string? _prepStatus;
    private string? _prepError;
    private CancellationTokenSource? _prepCts;
    private readonly ILogger<LoadTest> _log;
    private List<Check> _checks = [];
    private bool _checksForDryRun;
    private readonly ConcurrentDictionary<long, Bucket> _buckets = new();
    private readonly List<Point> _points = [];
    private readonly List<StepResult> _steps = [];
    private readonly RunStore _store;
    private readonly EvidenceCollector _evidence;
    private readonly Lock _lock = new();
    private string _name = "";
    private string? _notes;
    private readonly Timer _finalizer;

    private CancellationTokenSource? _cts;
    private TestConfig? _config;
    private volatile int _targetRate;
    private int _inFlight;
    private long _startedTs;
    private volatile string _state = "idle";
    private string? _stopReason;
    private string _modeAtStart = "?"; // human-readable label set at the start of the run
    private LabelConfig _labelsAtStart = Labels.ForMode("bounded");
    private string _agentProfile = "unknown";
    private Milestone? _degraded, _firstTimeout, _errors5, _errors50;

    public LoadTest(IConfiguration cfg, ReplicaMonitor replicas, PipelineMonitor pipeline, RunStore store,
        EvidenceCollector evidence, KubeClient kube, ILogger<LoadTest> log)
    {
        _kube = kube;
        _replicas = replicas;
        _pipeline = pipeline;
        _store = store;
        _evidence = evidence;
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
    public bool DryRunActive => _state is not ("idle" or "preparing") && _config?.DryRun == true;

    public bool Start(TestConfig c)
    {
        lock (_lock)
        {
            if (_state != "idle") return false;
            _config = c with
            {
                StartRate = Math.Clamp(c.StartRate, 1, 20_000),
                StepRate = Math.Clamp(c.StepRate, 0, 5_000),
                StepSeconds = Math.Clamp(c.StepSeconds, 5, 3600),
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
            var current = _replicas.CurrentLabels;
            // A dry run keeps the chosen labels but never response_time, so it can't explode anything.
            _labelsAtStart = _config.DryRun
                ? new LabelConfig(current.Labels!.Where(l => l != "response_time").ToArray(), current.ResponseTimeDecimals, 0, 0)
                : current;
            _modeAtStart = Labels.Describe(_labelsAtStart);
            _agentProfile = _pipeline.AgentProfile;
            var name = (c.Name ?? "").Trim();
            _name = name.Length > 0 ? name[..Math.Min(80, name.Length)]
                : _config.DryRun ? "Dry run"
                : $"{(Labels.IsHighCardinality(_labelsAtStart) ? "high-cardinality" : "bounded")} · {_agentProfile} agent · {_config.StartRate}→{_config.MaxRate} req/s";
            _notes = string.IsNullOrWhiteSpace(c.Notes) ? null : c.Notes.Trim()[..Math.Min(2000, c.Notes.Trim().Length)];
            _state = "running";
            _cts = new CancellationTokenSource();
            var config = _config;
            var ct = _cts.Token;
            _ = Task.Run(() => RunAsync(config, ct));
            _log.LogInformation("Load test started: {Config} labels={Labels} agent={Agent}", config, _modeAtStart, _agentProfile);
            return true;
        }
    }

    public void Stop(string reason)
    {
        lock (_lock)
        {
            if (_state == "preparing") { _prepCts?.Cancel(); return; }
            if (_state != "running") return;
            _stopReason ??= reason;
            _cts?.Cancel();
        }
    }

    // "Reset everything" and, optionally, start a test afterwards: restart the app replicas (clears SDK
    // series), restart Alloy (clears its WAL/series cache), delete the Prometheus pod (wipes the TSDB),
    // wait for all three, apply the requested labels, let one export cycle pass, then start.
    public bool Prepare(TestConfig? then, LabelConfig labels)
    {
        lock (_lock)
        {
            if (_state != "idle") return false;
            _state = "preparing";
            _prepError = null;
            _prepStatus = "Starting…";
            _prepCts = new CancellationTokenSource();
            var ct = _prepCts.Token;
            _ = Task.Run(() => PrepareAsync(then, Labels.Normalize(labels), ct));
            return true;
        }
    }

    private async Task PrepareAsync(TestConfig? then, LabelConfig labels, CancellationToken ct)
    {
        try
        {
            if (!_kube.Available) throw new InvalidOperationException("Kubernetes API not available to the driver");
            var started = DateTimeOffset.UtcNow;
            _replicas.DesiredLabels = labels; // restarted replicas pick these up from the reconciler
            _prepStatus = "Restarting app replicas, Alloy and Prometheus…";
            await _kube.RolloutRestartAsync("demo", "transactions-api");
            await _kube.RolloutRestartAsync("alloy", "alloy");
            await _kube.DeletePodAsync("monitoring", "prometheus-kps-prometheus-0");
            await Task.Delay(3000, ct);

            _prepStatus = "Waiting for app replicas…";
            await _kube.WaitDeploymentReadyAsync("demo", "transactions-api", TimeSpan.FromMinutes(4), ct);
            _prepStatus = "Waiting for Alloy…";
            await _kube.WaitDeploymentReadyAsync("alloy", "alloy", TimeSpan.FromMinutes(3), ct);
            _prepStatus = "Waiting for Prometheus…";
            await _kube.WaitPodReadyAsync("monitoring", "prometheus-kps-prometheus-0", started, TimeSpan.FromMinutes(4), ct);

            _prepStatus = "Applying labels to the new replicas…";
            var dep = await _kube.GetJsonAsync("/apis/apps/v1/namespaces/demo/deployments/transactions-api");
            var expected = dep.GetProperty("spec").GetProperty("replicas").GetInt32();
            var key = Labels.Key(labels);
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var age = (DateTimeOffset.UtcNow - started).TotalSeconds;
                var fresh = _replicas.Latest.Where(v => v.Reachable && v.Stats is not null && v.Stats.UptimeSeconds < age + 5).ToList();
                if (fresh.Count >= expected && fresh.All(v => v.Stats!.LabelKey == key)) break;
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"only {fresh.Count}/{expected} fresh replicas with the requested labels");
                await _replicas.ApplyLabelsAsync(labels);
                await Task.Delay(2000, ct);
            }

            _prepStatus = "Settling (one export cycle)…";
            await Task.Delay(15_000, ct);
        }
        catch (OperationCanceledException)
        {
            lock (_lock) { _prepError = "Preparation cancelled"; _prepStatus = null; _state = "idle"; }
            return;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Preparation failed");
            lock (_lock) { _prepError = "Preparation failed: " + ex.Message; _prepStatus = null; _state = "idle"; }
            return;
        }

        lock (_lock) { _prepStatus = null; _state = "idle"; }
        _log.LogInformation("Reset complete{Then}", then is null ? "" : "; starting test");
        if (then is not null && !Start(then)) lock (_lock) _prepError = "Reset complete, but the test could not start";
    }

    private async Task RunAsync(TestConfig cfg, CancellationToken ct)
    {
        if (cfg.DryRun) await _replicas.ApplyLabelsAsync(_labelsAtStart); // never explode during a dry run
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

        // Report: let the pipeline catch up (OTLP every 10s + Alloy batch + scrape), then collect evidence
        // while it is fresh. Kubernetes events expire and crashed containers' logs vanish on next restart.
        _state = "reporting";
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(cfg.DryRun ? 5 : 25));
            var record = BuildRecord(cfg);
            record.Evidence = await _evidence.CollectAsync(record, _replicas.Latest);
            (record.Headline, record.Findings) = Analyzer.Analyze(record, FindControl(record));
            _store.Save(record, ReportRenderer.Render(record));
            _log.LogInformation("Report saved: {Id} — {Headline}", record.Id, record.Headline);
        }
        catch (Exception ex) { _log.LogError(ex, "Report generation failed"); }

        lock (_lock) _state = "idle";
        _log.LogInformation("Load test finished: {Reason}", _stopReason);
    }

    public string? Reanalyze(string id)
    {
        var record = _store.Load(id);
        if (record is null) return null;
        (record.Headline, record.Findings) = Analyzer.Analyze(record, FindControl(record));
        _store.Save(record, ReportRenderer.Render(record));
        return record.Headline;
    }

    // The most recent bounded-label run at the same constant rate and duration on the same agent profile:
    // the control a high-cardinality soak should be compared against.
    private RunSummary? FindControl(RunRecord r)
    {
        if (!Labels.IsHighCardinality(new LabelConfig(r.Labels, r.ResponseTimeDecimals, r.Mode.Contains("GUID") ? 1 : 0, 0)) || r.Config.StartRate != r.Config.MaxRate)
            return null;
        return _store.Summaries().FirstOrDefault(s => !s.DryRun && s.Id != r.Id
            && !s.Labels.Contains("response_time") && !s.Mode.Contains("GUID")
            && s.MaxTargetRate == r.Config.MaxRate && Math.Abs(s.DurationSeconds - r.Config.StepSeconds) <= 15
            && s.AgentProfile == r.AgentProfile);
    }

    private RunRecord BuildRecord(TestConfig cfg)
    {
        List<Point> pts;
        lock (_points) pts = [.. _points];
        lock (_lock)
        {
            return new RunRecord
            {
                Id = RunStore.MakeId(_startedTs, _name),
                Name = _name,
                Notes = _notes,
                Mode = _modeAtStart,
                Labels = _labelsAtStart.Labels ?? [],
                ResponseTimeDecimals = _labelsAtStart.ResponseTimeDecimals ?? 6,
                AgentProfile = _agentProfile,
                DryRun = cfg.DryRun,
                Config = cfg,
                StartedAtUnix = _startedTs,
                EndedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                StopReason = _stopReason,
                Milestones = new() { ["degraded"] = _degraded, ["firstTimeout"] = _firstTimeout, ["errors5"] = _errors5, ["errors50"] = _errors50 },
                Steps = [.. _steps],
                Points = pts,
                Checks = [.. _checks],
                ReplicaSamples = _replicas.HistorySince(_startedTs - 10),
                PipelineSamples = _pipeline.HistorySince(_startedTs - 10),
            };
        }
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
        var unbounded = reps.Where(r => r.Reachable && r.Stats is { } s && (s.Labels.Contains("response_time") || s.ExtraLabels > 0)).Select(r => r.Stats?.Pod).ToList();

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
                prepStatus = _prepStatus,
                prepError = _prepError,
                dryRun = _config?.DryRun == true,
                checks = _checks,
                checksForDryRun = _checksForDryRun,
                startedAtUnix = _startedTs,
                config = _config,
                mode = _modeAtStart,
                agentProfile = _agentProfile,
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
                name = _name,
                notes = _notes,
                history = _store.Summaries().Take(25).ToList(),
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
    // Export path, derived between two polls (~2s).
    public double? ExportAvgMs { get; set; }
    public double? ExportMaxMs { get; set; }
    public long ExportFailuresDelta { get; set; }
    public long ExportRetriesDelta { get; set; }
    public long ExportFailuresTotal { get; set; }
    public long ExportRetriesTotal { get; set; }
    public double? SecondsSinceExportSuccess { get; set; }
    // GC, derived between two polls: % of wall time the runtime was paused for GC, and gen2 collections.
    public double? GcPausePct { get; set; }
    public long Gen2Delta { get; set; }
    public ReplicaSnapshot? Stats { get; set; }
    public long LastSeenUnix { get; set; }
}

sealed record ReplicaSample(long Ts, string Pod, long MemoryBytes, double? CpuCores, double? ThrottledPct, bool Reachable,
    long? GcHeapBytes = null, long? GcHeapLimitBytes = null, double? ExportMaxMs = null, long? ExportPoints = null,
    long? ExportBytes = null, long ExportFailures = 0, long ExportRetries = 0, double? GcPausePct = null, long Gen2 = 0);

static class Labels
{
    public static LabelConfig Normalize(LabelConfig c) => new(
        LabelSet.All.Where(l => (c.Labels ?? []).Contains(l, StringComparer.OrdinalIgnoreCase)).ToArray(),
        Math.Clamp(c.ResponseTimeDecimals ?? 6, 0, 6),
        Math.Clamp(c.ExtraLabels ?? 0, 0, LabelSet.ExtraNames.Length),
        Math.Clamp(c.ExtraPoolSize ?? 0, 0, LabelSet.MaxPool));

    public static LabelConfig ForMode(string mode) =>
        new(mode == "unbounded" ? LabelSet.All : LabelSet.Bounded, 6, 0, 0);

    public static string Key(LabelConfig c) =>
        LabelSet.KeyOf(c.Labels ?? [], c.ResponseTimeDecimals ?? 6, c.ExtraLabels ?? 0, c.ExtraPoolSize ?? 0);

    public static string Describe(LabelConfig c)
    {
        var d = (c.Labels ?? []).Length == 0 ? "(no labels)"
            : string.Join(", ", (c.Labels ?? []).Select(l => l == "response_time" ? $"response_time({c.ResponseTimeDecimals ?? 6}dp)" : l));
        var n = c.ExtraLabels ?? 0;
        return n > 0 ? $"{d}, +{n} GUID labels ({((c.ExtraPoolSize ?? 0) == 0 ? "unique" : $"pool {c.ExtraPoolSize:N0}")})" : d;
    }

    public static string Describe(ReplicaSnapshot s) => Describe(new LabelConfig(s.Labels, s.ResponseTimeDecimals, s.ExtraLabels, s.ExtraPoolSize));

    public static bool IsHighCardinality(LabelConfig c) => (c.Labels ?? []).Contains("response_time") || (c.ExtraLabels ?? 0) > 0;
}

// Polls every api replica directly (headless Service DNS) every 2s. Works even when Prometheus is down.
sealed class ReplicaMonitor(IConfiguration cfg, ILogger<ReplicaMonitor> log) : BackgroundService
{
    private readonly string _dns = cfg["REPLICA_DNS"] ?? "localhost";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(1.5) };
    private readonly ConcurrentDictionary<string, ReplicaView> _views = new();
    private readonly List<ReplicaSample> _history = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Label set chosen in the console; re-applied to replicas that restart with the env default.
    public volatile LabelConfig? DesiredLabels;

    public List<ReplicaView> Latest => _views.Values.OrderBy(v => v.Stats?.Pod ?? v.Ip).ToList();

    public LabelConfig? ObservedLabels
    {
        get
        {
            var snaps = _views.Values.Where(v => v.Reachable && v.Stats is not null).Select(v => v.Stats!).ToList();
            var keys = snaps.Select(s => s.LabelKey).Distinct().ToList();
            return keys.Count == 1 ? new LabelConfig(snaps[0].Labels, snaps[0].ResponseTimeDecimals, snaps[0].ExtraLabels, snaps[0].ExtraPoolSize) : null;
        }
    }

    public string ObservedDescription
    {
        get
        {
            var keys = _views.Values.Where(v => v.Reachable && v.Stats is not null).Select(v => v.Stats!.LabelKey).Distinct().Count();
            return keys switch { 0 => "unknown", 1 => Labels.Describe(ObservedLabels!), _ => "mixed" };
        }
    }

    public LabelConfig CurrentLabels => DesiredLabels ?? ObservedLabels ?? Labels.ForMode("bounded");

    public List<ReplicaSample> HistorySince(long ts)
    {
        lock (_history) return ts == 0 ? [] : _history.Where(s => s.Ts >= ts).ToList();
    }

    public async Task<int> ApplyLabelsAsync(LabelConfig config)
    {
        var c = Labels.Normalize(config);
        DesiredLabels = c;
        var results = await Task.WhenAll(_views.Keys.Select(ip => SetLabelsAsync(ip, c)));
        return results.Count(ok => ok);
    }

    private async Task<bool> SetLabelsAsync(string ip, LabelConfig c)
    {
        try
        {
            using var r = await _http.PostAsJsonAsync($"http://{ip}:8080/admin/labels", c, Json);
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
            {
                var s = v.Stats;
                _history.Add(new ReplicaSample(now, s?.Pod ?? v.Ip, v.Reachable ? s?.MemoryCurrentBytes ?? 0 : 0,
                    v.Reachable ? v.CpuCores : null, v.Reachable ? v.ThrottledPct : null, v.Reachable,
                    v.Reachable ? s?.GcHeapBytes : null, s?.GcHeapLimitBytes,
                    v.Reachable ? v.ExportMaxMs : null, v.Reachable ? s?.ExportLastPoints : null, v.Reachable ? s?.ExportLastBytes : null,
                    v.Reachable ? v.ExportFailuresDelta : 0, v.Reachable ? v.ExportRetriesDelta : 0,
                    v.Reachable ? v.GcPausePct : null, v.Reachable ? v.Gen2Delta : 0));
            }
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
            view.ExportFailuresDelta = view.ExportRetriesDelta = 0;
            view.Gen2Delta = 0;
            if (prev is not null && prev.Pod == snap.Pod && snap.UptimeSeconds >= prev.UptimeSeconds)
            {
                var dt = (snap.SampledAtUnixMs - prev.SampledAtUnixMs) / 1000.0;
                if (dt > 0 && snap.CpuUsageUsec >= prev.CpuUsageUsec)
                {
                    view.CpuCores = Math.Round((snap.CpuUsageUsec - prev.CpuUsageUsec) / 1e6 / dt, 3);
                    var periods = snap.CpuPeriods - prev.CpuPeriods;
                    view.ThrottledPct = periods > 0 ? Math.Round(100.0 * (snap.CpuThrottledPeriods - prev.CpuThrottledPeriods) / periods, 1) : 0;
                }
                var exports = snap.Exports - prev.Exports;
                if (exports > 0) view.ExportAvgMs = Math.Round((snap.ExportTotalMs - prev.ExportTotalMs) / exports, 1);
                view.ExportFailuresDelta = Math.Max(0, snap.ExportFailures - prev.ExportFailures);
                if (dt > 0) view.GcPausePct = Math.Round(Math.Clamp(100 * (snap.GcTotalPauseMs - prev.GcTotalPauseMs) / 1000.0 / dt, 0, 100), 1);
                view.Gen2Delta = Math.Max(0, snap.Gen2Collections - prev.Gen2Collections);
                view.ExportRetriesDelta = Math.Max(0, snap.ExportHttpRetries - prev.ExportHttpRetries);
            }
            else if (prev is not null && prev.Pod == snap.Pod)
            {
                view.RestartsSeen++; // uptime went backwards: the container restarted (e.g. OOMKilled)
            }
            // The replica resets its max-export-duration on every read, so this is the max since the last poll.
            view.ExportMaxMs = snap.Exports > 0 ? Math.Round(Math.Max(snap.ExportMaxMsSinceLastRead, 0), 1) : null;
            view.ExportFailuresTotal = snap.ExportFailures;
            view.ExportRetriesTotal = snap.ExportHttpRetries;
            view.SecondsSinceExportSuccess = snap.ExportLastSuccessUnixMs > 0
                ? Math.Round((snap.SampledAtUnixMs - snap.ExportLastSuccessUnixMs) / 1000.0, 1) : null;
            view.Stats = snap;
            view.Reachable = true;
            view.LastError = null;
            view.LastSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Restarted replicas come back with the env default; keep the chosen labels applied.
            var desired = DesiredLabels;
            if (desired is not null && snap.LabelKey != Labels.Key(desired)) await SetLabelsAsync(ip, desired);
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
    public string AgentProfile { get; private set; } = "unknown";
    public bool ProtectPrometheus { get; private set; }

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
        var alloyCpuLimit = await ScalarAsync($"max(kube_pod_container_resource_limits{{{alloy},resource=\"cpu\"}})", ct);
        var alloyCpu = await ScalarAsync($"sum(rate(container_cpu_usage_seconds_total{{{alloy}}}[1m]))", ct);
        var alloyRefused = await ScalarAsync("sum(rate(otelcol_receiver_refused_metric_points_total[1m]))", ct);
        // Is Alloy running a PROTECT_PROM config (drops response_time/GUID series before Prometheus)?
        ProtectPrometheus = (await ScalarAsync("count({component_id=\"prometheus.relabel.protect_prometheus\"})", ct) ?? 0) > 0;
        // "constrained" = the make agent-constrained profile (CPU-limited Alloy with a memory_limiter).
        AgentProfile = alloyMem is null && alloyLimit is null ? "unknown"
            : alloyCpuLimit is { } cl && cl <= 0.5 ? "constrained" : "normal";
        var promNode = await LabelAsync("max by (node) (kube_pod_info{namespace=\"monitoring\",pod=~\"prometheus-.*\"})", "node", ct);
        var nodes = await VectorAsync(
            "sum by (node) (container_memory_working_set_bytes{container!=\"\",container!=\"POD\"}) / on (node) max by (node) (kube_node_status_capacity{resource=\"memory\"})",
            "node", ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Latest = new
        {
            up = true, sampledAt = now, headSeries = head, txSeries = tx,
            prometheus = new { memory = promMem, limit = promLimit, restarts = promRestarts, node = promNode },
            alloy = new { memory = alloyMem, limit = alloyLimit, cpu = alloyCpu, cpuLimit = alloyCpuLimit, refusedPerSec = alloyRefused, profile = AgentProfile, protectPrometheus = ProtectPrometheus },
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
