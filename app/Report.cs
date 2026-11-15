// Run records and HTML reports for the load-test driver.
//
// At the end of every run the driver waits for the metrics pipeline to catch up, then collects evidence
// while it is still fresh (Kubernetes events expire after ~1h and a crashed container's logs are lost on
// its next restart):
//   - client timeline (from the driver itself)
//   - per-replica samples polled directly from the pods (cgroup + .NET GC)
//   - Prometheus range queries for the run window (CPU, memory, .NET GC heap, Prometheus/Alloy health)
//   - Kubernetes pod terminations, warning events, and exception lines from crashed containers' logs
// It then runs a small rule-based analysis and stores everything as JSON plus a self-contained HTML
// report on the driver's volume (DATA_DIR, default /data).

using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

sealed record Finding(string Severity, string Title, string Detail);
sealed record RefLine(double Y, string Label);
sealed record ChartSeries(string Name, List<double?[]> Pts);

sealed class ChartSpec
{
    public string Title { get; set; } = "";
    public string Unit { get; set; } = "short"; // short | bytes | ms | cores | pct
    public string? Note { get; set; }
    public List<ChartSeries> Series { get; set; } = [];
    public List<RefLine> Refs { get; set; } = [];
}

sealed class PodFacts
{
    public string Pod { get; set; } = "";
    public double? HeapStartBytes { get; set; }
    public double? HeapPeakBytes { get; set; }
    public double? GcLimitBytes { get; set; }
    public double? CpuPeakCores { get; set; }
    public double? CpuLimitCores { get; set; }
    public double? MemPeakBytes { get; set; }
    public double? MemLimitBytes { get; set; }
    public int? CardinalityLimit { get; set; }
}

sealed record Termination(string Namespace, string Pod, string Container, string? Reason, int? ExitCode, string? FinishedAt, int RestartCount);
sealed record EventRow(string Time, string Namespace, string Type, string Reason, string Object, int Count, string Message);
sealed record LogExcerpt(string Pod, string Container, List<string> TopLines, List<string> FirstTrace);

sealed class Evidence
{
    public bool PrometheusUp { get; set; }
    public string? PrometheusError { get; set; }
    public string? KubeError { get; set; }
    public List<ChartSpec> Charts { get; set; } = [];
    public Dictionary<string, double?> Facts { get; set; } = [];
    public List<PodFacts> Pods { get; set; } = [];
    public List<Termination> Terminations { get; set; } = [];
    public List<EventRow> Events { get; set; } = [];
    public List<LogExcerpt> Logs { get; set; } = [];
    // Why the real replicas' exports failed (summed over replicas, since pod start): "HTTP 413",
    // "timeout / cancelled", "connection: ConnectionError", "not sent (...)".
    public Dictionary<string, long> ExportFailureReasons { get; set; } = [];
}

sealed class RunRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Notes { get; set; }
    public string Mode { get; set; } = "";        // human-readable label set
    public string[] Labels { get; set; } = [];
    public int ResponseTimeDecimals { get; set; } = 6;
    public string AgentProfile { get; set; } = "unknown";
    // Simulated pods (transactions-fleet) sending to the same agent during the run, and their export
    // counters at start and end. 0 / null for runs without the fleet.
    public int FleetPods { get; set; }
    public FleetStats? FleetStart { get; set; }
    public FleetStats? FleetEnd { get; set; }
    public bool DryRun { get; set; }
    public TestConfig Config { get; set; } = new();
    public long StartedAtUnix { get; set; }
    public long EndedAtUnix { get; set; }
    public string? StopReason { get; set; }
    public Dictionary<string, Milestone?> Milestones { get; set; } = [];
    public List<StepResult> Steps { get; set; } = [];
    public List<Point> Points { get; set; } = [];
    public List<Check> Checks { get; set; } = [];
    public List<ReplicaSample> ReplicaSamples { get; set; } = [];
    public List<PipelineSample> PipelineSamples { get; set; } = [];
    public Evidence Evidence { get; set; } = new();
    public string Headline { get; set; } = "";
    public List<Finding> Findings { get; set; } = [];

    public long DurationSeconds => Points.Count == 0 ? 0 : Points[^1].T;
}

sealed record RunSummary(string Id, string Name, string StartedAt, string Mode, string[] Labels, string AgentProfile, bool DryRun, long DurationSeconds,
    int MaxTargetRate, int PeakOkPerSec, Milestone? Degraded, Milestone? FirstTimeout, Milestone? Errors5,
    Milestone? Errors50, string? StopReason, string Headline, int ChecksPassed, int ChecksTotal, int FleetPods = 0);

static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };
}

// Persists runs to DATA_DIR/runs/<id>.json and DATA_DIR/reports/<id>.html.
sealed class RunStore
{
    private readonly string _runs, _reports;
    private readonly ILogger<RunStore> _log;
    private readonly Lock _lock = new();
    private readonly List<RunSummary> _summaries = [];

    public RunStore(IConfiguration cfg, ILogger<RunStore> log)
    {
        _log = log;
        var root = cfg["DATA_DIR"] ?? "/data";
        _runs = Path.Combine(root, "runs");
        _reports = Path.Combine(root, "reports");
        try
        {
            Directory.CreateDirectory(_runs);
            Directory.CreateDirectory(_reports);
            foreach (var f in Directory.GetFiles(_runs, "*.json"))
            {
                try { _summaries.Add(Summarize(JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(f), Json.Options)!)); }
                catch (Exception ex) { _log.LogWarning(ex, "Skipping unreadable run file {File}", f); }
            }
            _summaries.Sort((a, b) => string.CompareOrdinal(b.Id, a.Id));
            _log.LogInformation("Loaded {Count} past runs from {Dir}", _summaries.Count, root);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Run storage unavailable at {Dir}; runs will not survive a restart", root);
        }
    }

    public List<RunSummary> Summaries() { lock (_lock) return [.. _summaries]; }

    public void Save(RunRecord r, string html)
    {
        lock (_lock)
        {
            _summaries.RemoveAll(s => s.Id == r.Id);
            _summaries.Insert(0, Summarize(r));
        }
        try
        {
            File.WriteAllText(Path.Combine(_runs, r.Id + ".json"), JsonSerializer.Serialize(r, Json.Options));
            File.WriteAllText(Path.Combine(_reports, r.Id + ".html"), html);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not persist run {Id}", r.Id); }
    }

    public RunRecord? Load(string id)
    {
        if (!Regex.IsMatch(id, "^[A-Za-z0-9._-]+$")) return null;
        var p = Path.Combine(_runs, id + ".json");
        return File.Exists(p) ? JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(p), Json.Options) : null;
    }

    public string? ReportPath(string id)
    {
        if (!Regex.IsMatch(id, "^[A-Za-z0-9._-]+$")) return null;
        var p = Path.Combine(_reports, id + ".html");
        return File.Exists(p) ? p : null;
    }

    private static RunSummary Summarize(RunRecord r) => new(
        r.Id, r.Name, DateTimeOffset.FromUnixTimeSeconds(r.StartedAtUnix).ToString("u", CultureInfo.InvariantCulture),
        r.Mode, r.Labels, r.AgentProfile, r.DryRun, r.DurationSeconds,
        r.Points.Count == 0 ? 0 : r.Points.Max(p => p.Target),
        r.Points.Count == 0 ? 0 : r.Points.Max(p => p.Ok),
        r.Milestones.GetValueOrDefault("degraded"), r.Milestones.GetValueOrDefault("firstTimeout"),
        r.Milestones.GetValueOrDefault("errors5"), r.Milestones.GetValueOrDefault("errors50"),
        r.StopReason, r.Headline, r.Checks.Count(c => c.Ok), r.Checks.Count, r.FleetPods);

    public static string MakeId(long startedAtUnix, string name)
    {
        var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40].Trim('-');
        return DateTimeOffset.FromUnixTimeSeconds(startedAtUnix).UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
               + (slug.Length > 0 ? "-" + slug : "");
    }
}

// Minimal in-cluster Kubernetes API client (service-account token + cluster CA). Read-only use.
sealed class KubeClient
{
    private const string SaDir = "/var/run/secrets/kubernetes.io/serviceaccount";
    private readonly HttpClient? _http;

    public KubeClient(ILogger<KubeClient> log)
    {
        try
        {
            var host = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
            var port = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT") ?? "443";
            if (host is null || !File.Exists(Path.Combine(SaDir, "token"))) return;
            var ca = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(SaDir, "ca.crt"));
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                {
                    if (cert is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(ca);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    return chain.Build(cert);
                },
            };
            var h = host.Contains(':') ? $"[{host}]" : host;
            _http = new HttpClient(handler) { BaseAddress = new Uri($"https://{h}:{port}"), Timeout = TimeSpan.FromSeconds(10) };
        }
        catch (Exception ex) { log.LogWarning(ex, "Kubernetes API client unavailable"); }
    }

    public bool Available => _http is not null;

    private HttpRequestMessage Req(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            File.ReadAllText(Path.Combine(SaDir, "token")).Trim()); // re-read: projected tokens rotate
        return req;
    }

    public async Task<JsonElement> GetJsonAsync(string path)
    {
        using var resp = await _http!.SendAsync(Req(path));
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    // Equivalent of `kubectl rollout restart deployment/<name>`: bump a pod-template annotation.
    public async Task RolloutRestartAsync(string ns, string deployment)
    {
        var patch = JsonSerializer.Serialize(new
        {
            spec = new { template = new { metadata = new { annotations = new Dictionary<string, string>
            {
                ["kubectl.kubernetes.io/restartedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            } } } },
        });
        using var req = Req($"/apis/apps/v1/namespaces/{ns}/deployments/{deployment}");
        req.Method = HttpMethod.Patch;
        req.Content = new StringContent(patch, Encoding.UTF8, "application/strategic-merge-patch+json");
        using var resp = await _http!.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"rollout restart failed: HTTP {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
    }

    public async Task DeletePodAsync(string ns, string pod)
    {
        using var req = Req($"/api/v1/namespaces/{ns}/pods/{pod}");
        req.Method = HttpMethod.Delete;
        using var resp = await _http!.SendAsync(req);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"delete pod {ns}/{pod} failed: HTTP {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
    }

    // Waits until a Deployment's rollout is complete (all replicas updated and available).
    public async Task WaitDeploymentReadyAsync(string ns, string name, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var d = await GetJsonAsync($"/apis/apps/v1/namespaces/{ns}/deployments/{name}");
            var gen = d.GetProperty("metadata").GetProperty("generation").GetInt64();
            var spec = d.GetProperty("spec").GetProperty("replicas").GetInt32();
            var st = d.GetProperty("status");
            int I(string k) => st.TryGetProperty(k, out var v) ? v.GetInt32() : 0;
            var observed = st.TryGetProperty("observedGeneration", out var og) ? og.GetInt64() : 0;
            // Same logic as `kubectl rollout status`: the new ReplicaSet is fully rolled out and no old pods remain.
            var rolledOut = st.TryGetProperty("conditions", out var conds) && conds.EnumerateArray().Any(c =>
                c.GetProperty("type").GetString() == "Progressing" && c.GetProperty("reason").GetString() == "NewReplicaSetAvailable");
            if (observed >= gen && rolledOut && I("updatedReplicas") == spec && I("availableReplicas") == spec && I("replicas") == spec
                && I("unavailableReplicas") == 0) return;
            await Task.Delay(2000, ct);
        }
        throw new TimeoutException($"{ns}/{name} did not become ready within {timeout.TotalSeconds:N0}s");
    }

    // Waits until a pod exists, is newer than `after`, and is Ready.
    public async Task WaitPodReadyAsync(string ns, string pod, DateTimeOffset after, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var p = await GetJsonAsync($"/api/v1/namespaces/{ns}/pods/{pod}");
                var created = DateTimeOffset.Parse(p.GetProperty("metadata").GetProperty("creationTimestamp").GetString()!, CultureInfo.InvariantCulture);
                var ready = p.GetProperty("status").TryGetProperty("conditions", out var conds)
                    && conds.EnumerateArray().Any(c => c.GetProperty("type").GetString() == "Ready" && c.GetProperty("status").GetString() == "True");
                if (created > after.AddSeconds(-1) && ready) return;
            }
            catch (HttpRequestException) { /* pod not recreated yet */ }
            await Task.Delay(2000, ct);
        }
        throw new TimeoutException($"{ns}/{pod} did not become ready within {timeout.TotalSeconds:N0}s");
    }

    public async Task<string?> GetTextAsync(string path)
    {
        using var resp = await _http!.SendAsync(Req(path));
        return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync() : null;
    }
}

sealed class EvidenceCollector(IConfiguration cfg, KubeClient kube, ILogger<EvidenceCollector> log)
{
    private static readonly string[] Namespaces = ["demo", "monitoring", "alloy"];
    private readonly string _prom = (cfg["PROMETHEUS_URL"] ?? "http://localhost:9090").TrimEnd('/');
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<Evidence> CollectAsync(RunRecord r, IReadOnlyList<ReplicaView> replicas)
    {
        var ev = new Evidence();
        AddExportEvidence(ev, r, replicas);
        var from = r.StartedAtUnix - 30;
        var to = Math.Min(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), r.EndedAtUnix + 30);
        var step = Math.Max(5, (to - from) / 300);

        try { await CollectPrometheusAsync(ev, r, from, to, step, replicas); ev.PrometheusUp = true; }
        catch (Exception ex)
        {
            ev.PrometheusError = ex is TaskCanceledException ? "Prometheus timed out" : ex.Message;
            log.LogWarning(ex, "Prometheus evidence collection failed");
        }

        if (!kube.Available) ev.KubeError = "Kubernetes API not available to the driver (no service account?)";
        else
        {
            try { await CollectKubeAsync(ev, r); }
            catch (Exception ex)
            {
                ev.KubeError = ex.Message;
                log.LogWarning(ex, "Kubernetes evidence collection failed");
            }
        }
        return ev;
    }

    // ---------- Export path (from the driver's direct 2s polls of each replica; independent of the pipeline) ----------
    private static void AddExportEvidence(Evidence ev, RunRecord r, IReadOnlyList<ReplicaView> replicas)
    {
        var t0 = r.StartedAtUnix;
        // Traffic window only: EndedAtUnix also covers draining and the wait before the report is built.
        var trafficEnd = t0 + (r.Points.Count == 0 ? 0 : r.Points.Max(p => p.T));
        var during = r.ReplicaSamples.Where(s => s.Ts >= t0 && s.Ts <= trafficEnd).ToList();
        foreach (var v in replicas)
            foreach (var (k, n) in v.Stats?.ExportFailureReasons ?? [])
                ev.ExportFailureReasons[k] = ev.ExportFailureReasons.GetValueOrDefault(k) + n;
        var interval = replicas.Select(v => v.Stats?.ExportIntervalMs ?? 0).FirstOrDefault(v => v > 0);
        var timeout = replicas.Select(v => v.Stats?.ExportTimeoutMs ?? 0).FirstOrDefault(v => v > 0);
        if (interval == 0) interval = 10_000;
        if (timeout == 0) timeout = 8_000;
        static string Short(string pod) => pod.Split('-')[^1];
        var pods = r.ReplicaSamples.Select(s => s.Pod).Distinct().Order().ToList();
        List<double?[]> Series(string pod, Func<ReplicaSample, double?> f) =>
            r.ReplicaSamples.Where(s => s.Pod == pod).Select(s => new double?[] { s.Ts - t0, f(s) }).ToList();

        ev.Charts.Add(new ChartSpec
        {
            Title = "OTLP export duration per replica (serialize + send + retries)", Unit = "ms",
            Note = "Max per 2s poll. Exports run on a background thread but on the pod's own CPU. Approaching the timeout = the agent is pushing back.",
            Series = pods.Select(p => new ChartSeries(Short(p), Series(p, s => s.ExportMaxMs))).ToList(),
            Refs = [new RefLine(timeout, "export timeout"), new RefLine(interval, "export interval")],
        });
        ev.Charts.Add(new ChartSpec
        {
            Title = "OTLP export payload per replica (metric points)", Unit = "short",
            Note = "Cumulative temporality: every series the SDK has ever seen is re-sent on every export.",
            Series = pods.Select(p => new ChartSeries(Short(p), Series(p, s => s.ExportPoints))).ToList(),
        });
        ev.Charts.Add(new ChartSpec
        {
            Title = "OTLP export failures + HTTP retries per replica", Unit = "short",
            Note = "Retries follow 429/5xx (e.g. Alloy's memory_limiter refusing data) or timeouts; each retry re-sends the payload.",
            Series = pods.Select(p => new ChartSeries(Short(p), Series(p, s => s.ExportFailures + s.ExportRetries))).ToList(),
        });

        ev.Charts.Add(new ChartSpec
        {
            Title = "GC pause per replica (% of wall time, direct polls)", Unit = "pct",
            Note = "Time the .NET runtime was paused for garbage collection between two 2s polls. Polled from the pods, so it survives a broken metrics pipeline.",
            Series = pods.Select(p => new ChartSeries(Short(p), Series(p, s => s.GcPausePct))).ToList(),
        });
        ev.Charts.Add(new ChartSpec
        {
            Title = "Gen2 (full) GC collections per replica, per 2s poll", Unit = "short",
            Series = pods.Select(p => new ChartSeries(Short(p), Series(p, s => s.Gen2))).ToList(),
        });
        var gcp = during.Where(s => s.GcPausePct.HasValue).Select(s => s.GcPausePct!.Value).ToList();
        ev.Facts["gcPausePeakPct"] = gcp.Count == 0 ? null : gcp.Max();
        ev.Facts["gcPauseAvgPct"] = gcp.Count == 0 ? null : Math.Round(gcp.Average(), 2);
        ev.Facts["gen2Total"] = during.Sum(s => s.Gen2);
        var degr0 = r.Milestones.GetValueOrDefault("degraded") ?? r.Milestones.GetValueOrDefault("firstTimeout");
        if (degr0 is not null)
        {
            var win = during.Where(s => s.Ts - t0 >= degr0.T - 15 && s.Ts - t0 <= degr0.T + 15).ToList();
            var wp = win.Where(s => s.GcPausePct.HasValue).Select(s => s.GcPausePct!.Value).ToList();
            ev.Facts["gcPauseAtDegradationPct"] = wp.Count == 0 ? null : wp.Max();
            ev.Facts["gen2AtDegradation"] = win.Sum(s => s.Gen2);
            ev.Facts["unreachableAtDegradation"] = win.Where(s => !s.Reachable).Select(s => s.Pod).Distinct().Count();
        }

        // Facts for the verdict.
        var exportMax = during.Select(s => s.ExportMaxMs).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        ev.Facts["exportIntervalMs"] = interval;
        ev.Facts["exportTimeoutMs"] = timeout;
        ev.Facts["exportMaxMs"] = exportMax.Count == 0 ? null : exportMax.Max();
        ev.Facts["exportPointsPeak"] = during.Select(s => (double?)s.ExportPoints).Max();
        ev.Facts["exportBytesPeak"] = during.Select(s => (double?)s.ExportBytes).Max();
        ev.Facts["exportFailures"] = during.Sum(s => s.ExportFailures);
        ev.Facts["exportRetries"] = during.Sum(s => s.ExportRetries);
        var thr = during.Where(s => s.Reachable && s.ThrottledPct.HasValue).Select(s => s.ThrottledPct!.Value).ToList();
        ev.Facts["throttlePeakPct"] = thr.Count == 0 ? null : thr.Max();
        ev.Facts["throttleAvgPct"] = thr.Count == 0 ? null : Math.Round(thr.Average(), 1);

        // Early vs late export cost (first vs last quarter of the run), to show growth.
        var dur = Math.Max(1, trafficEnd - t0);
        double? Q(long from, long to) { var v = during.Where(s => s.Ts - t0 >= from && s.Ts - t0 < to && s.ExportMaxMs.HasValue).Select(s => s.ExportMaxMs!.Value).ToList(); return v.Count == 0 ? null : v.Average(); }
        ev.Facts["exportEarlyMs"] = Q(0, dur / 4);
        ev.Facts["exportLateMs"] = Q(dur * 3 / 4, dur + 1);

        // Does client latency move with export cost? Pearson r between (max export ms across replicas per poll)
        // and (worst client p95 in the 2s before that poll).
        var byTs = during.GroupBy(s => s.Ts).Where(g => g.Any(s => s.ExportMaxMs.HasValue))
            .Select(g => (T: g.Key - t0, E: g.Max(s => s.ExportMaxMs ?? 0))).ToList();
        var xs = new List<double>(); var ys = new List<double>();
        foreach (var (t, e) in byTs)
        {
            var pts = r.Points.Where(p => p.T > t - 2 && p.T <= t && p.Ok > 0).ToList();
            if (pts.Count == 0) continue;
            xs.Add(e); ys.Add(pts.Max(p => p.P95));
        }
        ev.Facts["exportLatencyCorrelation"] = Pearson(xs, ys);

        // ---- Load model: how much CPU do requests alone cost? ----
        // Total CPU across replicas per poll vs the achieved request rate, from the early part of the run
        // (low cardinality, no failures). Projected to the rate where the run degraded, this tells whether
        // request work alone would have hit the CPU limit (load saturation) regardless of metrics.
        var expected = replicas.Count(v => v.Stats is not null);
        var cpuLimitTotal = replicas.Where(v => v.Stats?.CpuLimitCores is not null).Sum(v => v.Stats!.CpuLimitCores!.Value);
        double OkRate(long t) { var w = r.Points.Where(p => p.T > t - 3 && p.T <= t).ToList(); return w.Count == 0 ? 0 : w.Average(p => p.Ok); }
        double FailRate(long t) => r.Points.Where(p => p.T > t - 3 && p.T <= t).Sum(p => p.Timeouts + p.HttpErrors + p.ConnErrors + p.Dropped);
        var cpuByT = during.GroupBy(s => s.Ts - t0)
            .Where(g => g.Count(s => s.Reachable && s.CpuCores.HasValue) >= Math.Max(1, expected))
            // Thr = the MOST throttled replica in each poll (sensitive to one pod); ThrMean = the mean across replicas.
            .Select(g => (T: g.Key, Cpu: g.Where(s => s.Reachable).Sum(s => s.CpuCores ?? 0), Thr: g.Where(s => s.Reachable).Max(s => s.ThrottledPct ?? 0),
                          ThrMean: g.Where(s => s.Reachable).Average(s => s.ThrottledPct ?? 0),
                          Exp: g.Max(s => s.ExportMaxMs ?? 0)))
            .OrderBy(x => x.T).ToList();
        var early = cpuByT.Where(x => x.T >= 10 && x.T <= dur * 0.4 && FailRate(x.T) == 0 && OkRate(x.T) > 0).ToList();
        double? cpuPerReq = early.Count >= 3 ? early.Sum(x => x.Cpu) / early.Sum(x => OkRate(x.T)) : null;
        ev.Facts["cpuLimitTotal"] = cpuLimitTotal > 0 ? cpuLimitTotal : null;
        ev.Facts["cpuPerRequestMs"] = cpuPerReq * 1000;
        if (cpuPerReq is { } cpr && cpuLimitTotal > 0)
        {
            ev.Facts["capacityEstimateRps"] = Math.Round(cpuLimitTotal / cpr);
            var degr = r.Milestones.GetValueOrDefault("degraded") ?? r.Milestones.GetValueOrDefault("firstTimeout");
            if (degr is not null) ev.Facts["projectedCpuAtDegradation"] = Math.Round(cpr * degr.Rate, 2);
        }

        // ---- When did export pressure start? (first poll with export >= 25% of timeout, or any failure/retry) ----
        var onset = during.Where(s => (s.ExportMaxMs ?? 0) >= 0.25 * timeout || s.ExportFailures + s.ExportRetries > 0)
            .Select(s => (long?)(s.Ts - t0)).Min();
        ev.Facts["exportPressureOnsetT"] = onset;

        // ---- Soak (constant rate): early vs late, the cleanest evidence at fixed load ----
        if (r.Config.StartRate == r.Config.MaxRate)
        {
            ev.Facts["isSoak"] = 1;
            var warm = Math.Min(60, dur / 10);
            var e = cpuByT.Where(x => x.T >= warm && x.T < warm + (dur - warm) / 4).ToList();
            var l = cpuByT.Where(x => x.T >= dur - (dur - warm) / 4).ToList();
            var pe = r.Points.Where(p => p.T >= warm && p.T < warm + (dur - warm) / 4 && p.Ok > 0).ToList();
            var pl = r.Points.Where(p => p.T >= dur - (dur - warm) / 4 && p.Ok > 0).ToList();
            static double? Avg(IEnumerable<double> v) { var a = v.ToList(); return a.Count == 0 ? null : Math.Round(a.Average(), 2); }
            ev.Facts["soakCpuEarly"] = Avg(e.Select(x => x.Cpu));
            ev.Facts["soakCpuLate"] = Avg(l.Select(x => x.Cpu));
            ev.Facts["soakThrottleEarly"] = Avg(e.Select(x => x.Thr));
            ev.Facts["soakThrottleLate"] = Avg(l.Select(x => x.Thr));
            ev.Facts["soakExportEarly"] = Avg(e.Select(x => x.Exp));
            ev.Facts["soakExportLate"] = Avg(l.Select(x => x.Exp));
            ev.Facts["soakP95Early"] = Avg(pe.Select(p => p.P95));
            ev.Facts["soakP95Late"] = Avg(pl.Select(p => p.P95));
            // Mean throttling across replicas, and the median of per-second p95: robust to one pod and to a
            // short stall, which the worst-pod and mean figures above are not (review 2026-11-15, 3.1 and 3.2).
            ev.Facts["soakThrottleMeanEarly"] = Avg(e.Select(x => x.ThrMean));
            ev.Facts["soakThrottleMeanLate"] = Avg(l.Select(x => x.ThrMean));
            static double? Median(IEnumerable<double> v) { var a = v.Order().ToList(); return a.Count == 0 ? null : Math.Round(a[a.Count / 2], 2); }
            ev.Facts["soakP95MedianEarly"] = Median(pe.Select(p => p.P95));
            ev.Facts["soakP95MedianLate"] = Median(pl.Select(p => p.P95));
            ev.Facts["soakFailuresLate"] = r.Points.Where(p => p.T >= dur / 2).Sum(p => p.Timeouts + p.HttpErrors + p.ConnErrors + p.Dropped);
        }
    }

    private static double? Pearson(List<double> x, List<double> y)
    {
        if (x.Count < 8) return null;
        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < x.Count; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); syy += (y[i] - my) * (y[i] - my); }
        return sxx == 0 || syy == 0 ? null : Math.Round(sxy / Math.Sqrt(sxx * syy), 2);
    }

    // ---------- Prometheus ----------
    private async Task<List<(Dictionary<string, string> Labels, List<double?[]> Pts)>> RangeAsync(
        string q, long from, long to, long step, long t0)
    {
        var url = $"{_prom}/api/v1/query_range?query={Uri.EscapeDataString(q)}&start={from}&end={to}&step={step}";
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
        var result = new List<(Dictionary<string, string>, List<double?[]>)>();
        foreach (var s in doc.RootElement.GetProperty("data").GetProperty("result").EnumerateArray())
        {
            var labels = s.GetProperty("metric").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
            var pts = s.GetProperty("values").EnumerateArray().Select(v => new double?[]
            {
                v[0].GetDouble() - t0,
                double.TryParse(v[1].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null,
            }).ToList();
            result.Add((labels, pts));
        }
        return result;
    }

    private static double? Max(List<double?[]> pts) => pts.Select(p => p[1]).Where(v => v.HasValue).DefaultIfEmpty(null).Max();
    private static double? First(List<double?[]> pts) => pts.Select(p => p[1]).FirstOrDefault(v => v.HasValue);
    private static double? Last(List<double?[]> pts) => pts.Select(p => p[1]).LastOrDefault(v => v.HasValue);

    private async Task CollectPrometheusAsync(Evidence ev, RunRecord r, long from, long to, long step, IReadOnlyList<ReplicaView> replicas)
    {
        var t0 = r.StartedAtUnix;
        async Task<List<(Dictionary<string, string> Labels, List<double?[]> Pts)>> Q(string q) => await RangeAsync(q, from, to, step, t0);
        static string Short(string pod) => pod.Split('-')[^1];

        var app = "namespace=\"demo\",container=\"app\"";
        var cpu = await Q($"sum by (pod) (rate(container_cpu_usage_seconds_total{{{app}}}[30s]))");
        var mem = await Q($"max by (pod) (container_memory_working_set_bytes{{{app}}})");
        var memLimit = await Q($"max(kube_pod_container_resource_limits{{{app},resource=\"memory\"}})");
        var cpuLimit = await Q($"max(kube_pod_container_resource_limits{{{app},resource=\"cpu\"}})");
        var heap = await Q("max by (instance) (dotnet_gc_last_collection_heap_size_bytes{job=\"transactions-api\"})");
        var restarts = await Q($"sum by (pod) (kube_pod_container_status_restarts_total{{{app}}})");

        // GC heap hard limit and cardinality limit come from the replicas themselves (/admin/stats).
        var gcLimit = replicas.Select(v => (double?)v.Stats?.GcHeapLimitBytes).Where(v => v > 0).DefaultIfEmpty(null).Max();
        var cardLimit = replicas.Select(v => v.Stats?.CardinalityLimit).FirstOrDefault(v => v > 0);
        var memLim = Last(memLimit.FirstOrDefault().Pts ?? []);
        var cpuLim = Last(cpuLimit.FirstOrDefault().Pts ?? []);
        gcLimit ??= memLim * 0.75;

        // Only pods that existed during the run itself (the 30s lead-in can catch pods from a previous rollout).
        // Scraped cAdvisor series go stale when a pod is deleted; remote-written .NET series do not (Prometheus
        // keeps returning their last value for 5m), so the pod set comes from cAdvisor.
        static bool During(List<double?[]> pts) => pts.Any(p => p[0] >= 0 && p[1].HasValue);
        cpu = cpu.Where(s => During(s.Pts)).ToList();
        mem = mem.Where(s => During(s.Pts)).ToList();
        restarts = restarts.Where(s => During(s.Pts)).ToList();
        var podSet = cpu.Select(s => s.Labels.GetValueOrDefault("pod", "")).Where(p => p.Length > 0).ToHashSet();
        heap = heap.Where(s => During(s.Pts) && (podSet.Count == 0 || podSet.Contains(s.Labels.GetValueOrDefault("instance", "")))).ToList();
        var pods = podSet.Union(heap.Select(s => s.Labels.GetValueOrDefault("instance", "")))
            .Where(p => p.Length > 0).Distinct().Order().ToList();
        foreach (var pod in pods)
        {
            var h = heap.FirstOrDefault(s => s.Labels.GetValueOrDefault("instance") == pod).Pts;
            ev.Pods.Add(new PodFacts
            {
                Pod = pod,
                HeapStartBytes = h is null ? null : First(h),
                HeapPeakBytes = h is null ? null : Max(h),
                GcLimitBytes = gcLimit,
                CpuPeakCores = Max(cpu.FirstOrDefault(s => s.Labels.GetValueOrDefault("pod") == pod).Pts ?? []),
                CpuLimitCores = cpuLim,
                MemPeakBytes = Max(mem.FirstOrDefault(s => s.Labels.GetValueOrDefault("pod") == pod).Pts ?? []),
                MemLimitBytes = memLim,
                CardinalityLimit = cardLimit,
            });
        }

        ev.Charts.Add(new ChartSpec
        {
            Title = "App replicas: CPU used (cAdvisor)", Unit = "cores",
            Note = "Pinned at the limit while serving nothing = GC thrash or a spin, not request work.",
            Series = cpu.Select(s => new ChartSeries(Short(s.Labels.GetValueOrDefault("pod", "?")), s.Pts)).ToList(),
            Refs = cpuLim is { } c ? [new RefLine(c, "CPU limit")] : [],
        });
        ev.Charts.Add(new ChartSpec
        {
            Title = "App replicas: .NET GC heap vs GC heap limit", Unit = "bytes",
            Note = "The GC throws OutOfMemoryException when the heap reaches its hard limit (default 75% of the container memory limit), even if the container is far from being OOMKilled.",
            Series = heap.Select(s => new ChartSeries(Short(s.Labels.GetValueOrDefault("instance", "?")), s.Pts)).ToList(),
            Refs = gcLimit is { } g ? [new RefLine(g, "GC heap limit")] : [],
        });
        ev.Charts.Add(new ChartSpec
        {
            Title = "App replicas: container memory (working set)", Unit = "bytes",
            Series = mem.Select(s => new ChartSeries(Short(s.Labels.GetValueOrDefault("pod", "?")), s.Pts)).ToList(),
            Refs = memLim is { } m ? [new RefLine(m, "memory limit")] : [],
        });
        ev.Charts.Add(new ChartSpec
        {
            Title = "App replicas: restarts", Unit = "short",
            Series = restarts.Select(s => new ChartSeries(Short(s.Labels.GetValueOrDefault("pod", "?")), s.Pts)).ToList(),
        });

        var promSel = "namespace=\"monitoring\",container=\"prometheus\"";
        var alloySel = "namespace=\"alloy\",container=\"alloy\"";
        var promMem = await Q($"max(container_memory_working_set_bytes{{{promSel}}})");
        var promLim = await Q($"max(kube_pod_container_resource_limits{{{promSel},resource=\"memory\"}})");
        var promRestarts = await Q($"max(kube_pod_container_status_restarts_total{{{promSel}}})");
        var alloyMem = await Q($"max(container_memory_working_set_bytes{{{alloySel}}})");
        var alloyLim = await Q($"max(kube_pod_container_resource_limits{{{alloySel},resource=\"memory\"}})");
        var alloyRestarts = await Q($"max(kube_pod_container_status_restarts_total{{{alloySel}}})");
        var pm = promMem.FirstOrDefault().Pts ?? [];
        var am = alloyMem.FirstOrDefault().Pts ?? [];
        var pl = Last(promLim.FirstOrDefault().Pts ?? []);
        var al = Last(alloyLim.FirstOrDefault().Pts ?? []);
        var refs = new List<RefLine>();
        if (pl is { } p1) refs.Add(new RefLine(p1, "Prometheus limit"));
        if (al is { } a1 && a1 != pl) refs.Add(new RefLine(a1, "Alloy limit"));
        ev.Charts.Add(new ChartSpec
        {
            Title = "Prometheus & Alloy memory", Unit = "bytes",
            Series = [new ChartSeries("Prometheus", pm), new ChartSeries("Alloy", am)], Refs = refs,
        });

        var head = await Q("max(prometheus_tsdb_head_series)");
        var tx = await Q("count(http_transactions_total)");
        var rt = await Q("count(http_transactions_total{response_time!=\"\"}) or vector(0)");
        ev.Charts.Add(new ChartSpec
        {
            Title = "Series in Prometheus", Unit = "short",
            Series =
            [
                new ChartSeries("head series (all)", head.FirstOrDefault().Pts ?? []),
                new ChartSeries("http_transactions_total", tx.FirstOrDefault().Pts ?? []),
                new ChartSeries("…with response_time label", rt.FirstOrDefault().Pts ?? []),
            ],
        });

        var accepted = await Q("sum(rate(otelcol_receiver_accepted_metric_points_total[1m]))");
        var refused = await Q("sum(rate(otelcol_receiver_refused_metric_points_total[1m])) or vector(0)");
        var rwFailed = await Q("sum(rate(prometheus_remote_storage_samples_failed_total[1m])) or vector(0)");
        ev.Charts.Add(new ChartSpec
        {
            Title = "Alloy: OTLP in / remote_write failures (per second)", Unit = "short",
            Series =
            [
                new ChartSeries("OTLP points accepted", accepted.FirstOrDefault().Pts ?? []),
                new ChartSeries("OTLP points refused", refused.FirstOrDefault().Pts ?? []),
                new ChartSeries("remote_write samples failed", rwFailed.FirstOrDefault().Pts ?? []),
            ],
        });

        ev.Facts["promMemPeak"] = Max(pm);
        ev.Facts["promMemLimit"] = pl;
        ev.Facts["promRestartsDelta"] = Delta(promRestarts.FirstOrDefault().Pts);
        ev.Facts["alloyMemPeak"] = Max(am);
        ev.Facts["alloyMemLimit"] = al;
        ev.Facts["alloyRestartsDelta"] = Delta(alloyRestarts.FirstOrDefault().Pts);
        ev.Facts["headSeriesPeak"] = Max(head.FirstOrDefault().Pts ?? []);
        ev.Facts["txSeriesPeak"] = Max(tx.FirstOrDefault().Pts ?? []);
        ev.Facts["rtSeriesPeak"] = Max(rt.FirstOrDefault().Pts ?? []);
        ev.Facts["alloyRefusedPeak"] = Max(refused.FirstOrDefault().Pts ?? []);
        ev.Facts["rwFailedPeak"] = Max(rwFailed.FirstOrDefault().Pts ?? []);
        ev.Facts["appRestartsDelta"] = restarts.Sum(s => Delta(s.Pts) ?? 0);
    }

    private static double? Delta(List<double?[]>? pts)
    {
        if (pts is null) return null;
        var first = First(pts);
        var max = Max(pts);
        return first is null || max is null ? null : Math.Max(0, max.Value - first.Value);
    }

    // ---------- Kubernetes ----------
    private static readonly Regex ExceptionLine = new(
        @"(Exception|Fatal error|Internal CLR error|PAL_SEH|OOMKilled|out of memory|panic:|SIGSEGV|level=error)", RegexOptions.IgnoreCase);

    private async Task CollectKubeAsync(Evidence ev, RunRecord r)
    {
        var from = DateTimeOffset.FromUnixTimeSeconds(r.StartedAtUnix - 30);
        var to = DateTimeOffset.UtcNow;
        foreach (var ns in Namespaces)
        {
            var pods = await kube.GetJsonAsync($"/api/v1/namespaces/{ns}/pods");
            foreach (var pod in pods.GetProperty("items").EnumerateArray())
            {
                var podName = pod.GetProperty("metadata").GetProperty("name").GetString()!;
                if (!pod.GetProperty("status").TryGetProperty("containerStatuses", out var statuses)) continue;
                foreach (var cs in statuses.EnumerateArray())
                {
                    var container = cs.GetProperty("name").GetString()!;
                    if (!cs.TryGetProperty("lastState", out var last) || !last.TryGetProperty("terminated", out var term)) continue;
                    var finished = term.TryGetProperty("finishedAt", out var f) ? f.GetString() : null;
                    if (finished is null || !DateTimeOffset.TryParse(finished, CultureInfo.InvariantCulture, out var fin) || fin < from) continue;
                    ev.Terminations.Add(new Termination(ns, podName, container,
                        term.TryGetProperty("reason", out var rs) ? rs.GetString() : null,
                        term.TryGetProperty("exitCode", out var ec) ? ec.GetInt32() : null,
                        finished, cs.GetProperty("restartCount").GetInt32()));

                    var logs = await kube.GetTextAsync(
                        $"/api/v1/namespaces/{ns}/pods/{podName}/log?container={container}&previous=true&limitBytes=400000");
                    if (logs is not null) ev.Logs.Add(Excerpt(podName, container, logs));
                }
            }

            var events = await kube.GetJsonAsync($"/api/v1/namespaces/{ns}/events");
            foreach (var e in events.GetProperty("items").EnumerateArray())
            {
                var type = e.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                var reason = e.TryGetProperty("reason", out var re) ? re.GetString() ?? "" : "";
                if (type != "Warning" && reason is not ("Killing" or "OOMKilling" or "BackOff" or "Evicted" or "Preempting")) continue;
                var ts = new[] { "lastTimestamp", "eventTime", "firstTimestamp" }
                    .Select(k => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
                    .FirstOrDefault(v => v is not null) ?? e.GetProperty("metadata").GetProperty("creationTimestamp").GetString();
                if (ts is null || !DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, out var when) || when < from || when > to) continue;
                ev.Events.Add(new EventRow(when.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture), ns, type, reason,
                    e.GetProperty("involvedObject").GetProperty("name").GetString() ?? "",
                    e.TryGetProperty("count", out var cnt) && cnt.ValueKind == JsonValueKind.Number ? cnt.GetInt32() : 1,
                    e.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : ""));
            }
        }
        ev.Events.Sort((a, b) => string.CompareOrdinal(a.Time, b.Time));
    }

    private static LogExcerpt Excerpt(string pod, string container, string logs)
    {
        var lines = logs.Split('\n').Select(l => l.TrimEnd()).ToList();
        var top = lines.Where(l => ExceptionLine.IsMatch(l))
            .Select(l => l.Trim()).GroupBy(l => l).OrderByDescending(g => g.Count())
            .Take(8).Select(g => g.Count() > 1 ? $"{g.Count()}× {g.Key}" : g.Key).ToList();
        // The last fatal error's stack is usually the most telling (what the process died in).
        var start = lines.FindLastIndex(l => l.Contains("Fatal error", StringComparison.OrdinalIgnoreCase));
        if (start < 0) start = lines.FindIndex(l => l.Contains("Exception", StringComparison.Ordinal));
        var trace = start < 0 ? [] : lines.Skip(start).Take(16).ToList();
        return new LogExcerpt(pod, container, top, trace);
    }
}

static class Analyzer
{
    private static string Mib(double? b) => b is null ? "?" : $"{b.Value / 1048576:N0} MiB";
    private static string Pct(double? a, double? b) => a is null || b is null || b == 0 ? "?" : $"{100 * a.Value / b.Value:N0}%";

    public static (string Headline, List<Finding> Findings) Analyze(RunRecord r, RunSummary? control = null)
    {
        var f = new List<Finding>();
        var ev = r.Evidence;
        var pts = r.Points;
        long ok = pts.Sum(p => p.Ok), tmo = pts.Sum(p => p.Timeouts), http = pts.Sum(p => p.HttpErrors),
             conn = pts.Sum(p => p.ConnErrors), drop = pts.Sum(p => p.Dropped);
        var failed = tmo + http + conn + drop;
        var ms = r.Milestones;

        // 1. What the client saw.
        if (failed == 0)
            f.Add(new("ok", "No client-visible failures", $"{ok:N0} requests succeeded; worst 1-second p95 {(pts.Count == 0 ? 0 : pts.Max(p => p.P95)):N0} ms."));
        else
        {
            var firstTmo = pts.FirstOrDefault(p => p.Timeouts > 0);
            var firstConn = pts.FirstOrDefault(p => p.ConnErrors > 0);
            var seq = new List<string>();
            if (firstTmo is not null) seq.Add($"timeouts from t={firstTmo.T}s ({firstTmo.Target} req/s)");
            if (firstConn is not null) seq.Add($"connection errors from t={firstConn.T}s ({firstConn.Target} req/s)");
            var interp = firstTmo is not null && firstConn is not null && firstTmo.T < firstConn.T
                ? " Requests hung first, then connections failed: the pods stopped responding, were removed from the Service (readiness) and/or restarted (liveness)."
                : "";
            f.Add(new(ms.GetValueOrDefault("errors50") is not null ? "critical" : "warn",
                ms.GetValueOrDefault("errors50") is { } e50 ? $"Service collapsed at {e50.Rate} req/s (≥50% failed at t={e50.T}s)" : $"{failed:N0} client-visible failures",
                $"{ok:N0} ok · {tmo:N0} timeouts · {http:N0} HTTP 5xx · {conn:N0} connection errors · {drop:N0} dropped. Sequence: {string.Join(", then ", seq)}.{interp}"));
        }
        if (drop > 0)
            f.Add(new("warn", "Driver hit its in-flight cap", $"{drop:N0} requests were never sent because {r.Config.MaxInFlight:N0} were already in flight. Counted as failures."));

        // 2. App pod terminations.
        var appTerms = ev.Terminations.Where(t => t.Namespace == "demo" && t.Container == "app").ToList();
        var liveness = ev.Events.Count(e => e.Namespace == "demo" && e.Message.Contains("Liveness probe failed", StringComparison.Ordinal));
        if (appTerms.Count > 0)
        {
            var byReason = string.Join(", ", appTerms.GroupBy(t => t.Reason ?? "unknown").Select(g => $"{g.Count()}× {g.Key}"));
            var oomKilled = appTerms.Any(t => t.Reason == "OOMKilled");
            f.Add(new("critical",
                oomKilled ? "App pods OOMKilled by the kernel (container memory limit)"
                    : liveness > 0 ? "App pods killed by the liveness probe after becoming unresponsive" : "App pods terminated",
                $"{appTerms.Count} of the api containers terminated during the run ({byReason})." +
                (liveness > 0 ? $" {liveness} liveness-probe failure event(s): /healthz did not answer within the probe timeout." : "")));
        }

        // 3. .NET out-of-memory inside the process (GC heap hard limit).
        var dotnetOom = ev.Logs.Where(l => l.TopLines.Concat(l.FirstTrace).Any(x => x.Contains("OutOfMemoryException", StringComparison.Ordinal))).ToList();
        var nearLimitAtStart = ev.Pods.Where(p => p.HeapStartBytes / p.GcLimitBytes >= 0.85).ToList();
        if (dotnetOom.Count > 0)
        {
            var pod = ev.Pods.OrderByDescending(p => p.HeapPeakBytes ?? 0).FirstOrDefault();
            f.Add(new("critical", ".NET OutOfMemoryException: managed heap hit the GC heap hard limit",
                $"{dotnetOom.Count} crashed container log(s) show System.OutOfMemoryException in ordinary allocations, then a fatal CLR error. " +
                (pod is null ? "" :
                $"GC heap peaked at {Mib(pod.HeapPeakBytes)} of a {Mib(pod.GcLimitBytes)} GC limit, while the container working set peaked at only {Mib(pod.MemPeakBytes)} of {Mib(pod.MemLimitBytes)} — " +
                "so Kubernetes never saw an OOM; the .NET runtime refused the allocations itself.")));
        }
        if (nearLimitAtStart.Count > 0)
        {
            var p = nearLimitAtStart[0];
            f.Add(new(dotnetOom.Count > 0 ? "critical" : "warn",
                $"GC heap was already {Pct(p.HeapStartBytes, p.GcLimitBytes)} of its limit before the test",
                $"{nearLimitAtStart.Count} replica(s) started the run with {Mib(p.HeapStartBytes)} of {Mib(p.GcLimitBytes)} heap in use. " +
                $"The OpenTelemetry .NET SDK pre-allocates metric-point storage per metric stream sized by the cardinality limit " +
                $"(OTEL_CARDINALITY_LIMIT={p.CardinalityLimit?.ToString("N0", CultureInfo.InvariantCulture) ?? "?"}, applied to every instrument), " +
                "so a high limit reserves most of the heap up front and leaves almost no headroom for new series. " +
                "Lower the limit (and/or apply it only to the metrics that need it), raise the memory limit, or set DOTNET_GCHeapHardLimitPercent."));
        }

        // 4. CPU pinned.
        var pinned = ev.Pods.Where(p => p.CpuPeakCores >= 0.95 * p.CpuLimitCores).ToList();
        if (pinned.Count > 0)
            f.Add(new("warn", $"CPU pinned at the limit on {pinned.Count} replica(s)",
                $"Peak {pinned.Max(p => p.CpuPeakCores):N2} of {pinned[0].CpuLimitCores:N2} cores. " +
                (dotnetOom.Count > 0 ? "Alongside the OutOfMemoryExceptions this is the GC running continuously trying to free space (GC thrash), not request work."
                    : "Requests queue behind throttled CPU, raising latency until timeouts.")));

        var fx = ev.Facts;

        // 5. Verdict: did metrics back pressure cause resource contention with the live app?
        {
            var timeoutMs = fx.GetValueOrDefault("exportTimeoutMs") ?? 8000;
            var exportMax = fx.GetValueOrDefault("exportMaxMs");
            var retries = (fx.GetValueOrDefault("exportRetries") ?? 0) + (fx.GetValueOrDefault("exportFailures") ?? 0);
            var throttlePeak = fx.GetValueOrDefault("throttlePeakPct");
            var corr = fx.GetValueOrDefault("exportLatencyCorrelation");
            var early = fx.GetValueOrDefault("exportEarlyMs");
            var late = fx.GetValueOrDefault("exportLateMs");
            var onsetT = fx.GetValueOrDefault("exportPressureOnsetT");
            var cpuLimit = fx.GetValueOrDefault("cpuLimitTotal");
            var projected = fx.GetValueOrDefault("projectedCpuAtDegradation");
            var capacity = fx.GetValueOrDefault("capacityEstimateRps");
            var degrMs = ms.GetValueOrDefault("degraded") ?? ms.GetValueOrDefault("firstTimeout");
            var degraded = degrMs is not null;
            var isSoak = fx.GetValueOrDefault("isSoak") == 1;
            static string N(double? v, string fmt = "N0") => v?.ToString(fmt, CultureInfo.InvariantCulture) ?? "?";

            var pressure = exportMax >= 0.5 * timeoutMs || retries > 0;
            var contention = throttlePeak >= 25 || pinned.Count > 0;
            var linked = corr >= 0.3;
            var masked = nearLimitAtStart.Count > 0 || appTerms.Any(t => t.Reason == "OOMKilled") && !pressure;

            // Load saturation: would request work alone have reached the CPU limit where it degraded?
            var loadSaturated = degrMs is not null && projected is not null && cpuLimit is not null && projected >= 0.9 * cpuLimit;
            if (fx.GetValueOrDefault("cpuPerRequestMs") is { } cprMs)
                f.Add(new(loadSaturated ? "critical" : "info",
                    loadSaturated ? $"Load saturation: request CPU alone reaches the limit at ≈{N(capacity)} req/s" : $"Request-only capacity ≈ {N(capacity)} req/s",
                    $"Requests cost ≈{cprMs:N2} ms of CPU each (measured early in the run, at low cardinality); {N(cpuLimit, "N1")} cores in total across replicas ⇒ ≈{N(capacity)} req/s before any metrics overhead. " +
                    (degrMs is null ? "The run did not degrade."
                        : $"At the degradation point ({degrMs.Rate:N0} req/s) requests alone would use ≈{N(projected, "N2")} cores " +
                          (loadSaturated ? "— at or above the limit, so the degradation is explained by load, not by metrics."
                                         : "— below the limit, so something else (e.g. export cost) used the remaining CPU."))));

            // Ordering: export pressure must start before (or with) the degradation to be a cause.
            var precedes = onsetT is not null && (degrMs is null || onsetT <= degrMs.T + 10);

            var evidence =
                $"Export duration peaked at {N(exportMax)} ms (timeout {timeoutMs:N0} ms; first quarter avg {N(early)} ms → last quarter {N(late)} ms), " +
                $"{retries:N0} export failures/retries, payload up to {fx.GetValueOrDefault("exportPointsPeak") ?? 0:N0} points. " +
                $"Export pressure began at {(onsetT is null ? "—" : $"t={onsetT:N0}s")}; degradation at {(degrMs is null ? "—" : $"t={degrMs.T}s ({degrMs.Rate:N0} req/s)")}. " +
                $"Pod CPU throttling peaked at {N(throttlePeak)}%. Export-cost ↔ p95 correlation r = {N(corr, "N2")}. Agent: {r.AgentProfile}.";

            if (isSoak)
            {
                // At constant load the only thing that changes over time is cardinality / export cost.
                var cpuE = fx.GetValueOrDefault("soakCpuEarly"); var cpuL = fx.GetValueOrDefault("soakCpuLate");
                var p95E = fx.GetValueOrDefault("soakP95Early"); var p95L = fx.GetValueOrDefault("soakP95Late");
                var thrE = fx.GetValueOrDefault("soakThrottleEarly"); var thrL = fx.GetValueOrDefault("soakThrottleLate");
                var expE = fx.GetValueOrDefault("soakExportEarly"); var expL = fx.GetValueOrDefault("soakExportLate");
                var failLate = fx.GetValueOrDefault("soakFailuresLate") ?? 0;
                var latencyRose = p95L >= Math.Max(2 * (p95E ?? 0), (p95E ?? 0) + 100) || failLate > 0;
                var cpuRose = cpuL >= 1.15 * cpuE || thrL >= 25 && thrL >= 2 * (thrE ?? 0) + 5;
                var exportRose = expL >= Math.Max(10 * (expE ?? 0), 500) || retries > 0;
                var earlySaturated = cpuE is not null && cpuLimit is not null && cpuE >= 0.85 * cpuLimit;
                var soakEvidence = $"Constant {r.Config.StartRate:N0} req/s. Early → late: CPU {N(cpuE, "N2")} → {N(cpuL, "N2")} cores, " +
                    $"throttling (worst replica) {N(thrE)}% → {N(thrL)}%, mean across replicas {N(fx.GetValueOrDefault("soakThrottleMeanEarly"))}% → {N(fx.GetValueOrDefault("soakThrottleMeanLate"))}%, " +
                    $"export {N(expE)} → {N(expL)} ms, p95 mean {N(p95E)} → {N(p95L)} ms (median {N(fx.GetValueOrDefault("soakP95MedianEarly"))} → {N(fx.GetValueOrDefault("soakP95MedianLate"))} ms), {failLate:N0} failures in the second half. ";
                f.Add(new("info", "Soak: early vs late at constant load", soakEvidence));

                // Control at the same rate, duration and agent profile with bounded labels.
                var controlClean = control is not null && control.Degraded is null && control.FirstTimeout is null && control.Errors5 is null;
                var controlText = control is null
                    ? (r.FleetPods > 0
                        ? "No matching control run found (same rate, duration, agent and labels, without the fleet): run one to rule out the environment. "
                        : "No matching control run found (same rate, duration and agent, bounded labels): run one to rule out the environment. ")
                    : controlClean ? $"Control \"{control.Name}\" at the same rate/duration/agent had no degradation and no errors. "
                    : $"Control \"{control.Name}\" also degraded, so the environment/load explains it, not the labels. ";
                if (control is not null)
                    f.Add(new(controlClean ? "info" : "warn", controlClean ? "Control run was clean" : "Control run also degraded", controlText));

                // Transient stall: at constant load any degradation is suspect; the early/late comparison misses a
                // stall that recovers (e.g. when the agent crashes and exports start failing fast).
                var badSecs = r.Points.Where(p => p.P95 >= r.Config.DegradedP95Ms || p.Timeouts + p.HttpErrors + p.ConnErrors + p.Dropped > 0).ToList();
                if (degraded && badSecs.Count > 0 && pressure && contention && precedes && !loadSaturated && !masked && (control is null || controlClean))
                {
                    var fails = badSecs.Sum(p => p.Timeouts + p.HttpErrors + p.ConnErrors + p.Dropped);
                    var gcAt = fx.GetValueOrDefault("gcPauseAtDegradationPct");
                    var unreach = fx.GetValueOrDefault("unreachableAtDegradation");
                    f.Add(new("critical", "Metrics back pressure: CONFIRMED — transient outage at constant load",
                        $"At a constant {r.Config.StartRate:N0} req/s the app degraded from t={degrMs!.T}s: {badSecs.Count} bad seconds between t={badSecs.First().T}s and t={badSecs.Last().T}s, " +
                        $"worst p95 {badSecs.Max(p => p.P95):N0} ms, {fails:N0} failed requests" +
                        (unreach is > 0 ? $", {unreach:N0} replica(s) unresponsive" : "") + ". " +
                        $"Export pressure began first (t={onsetT:N0}s); request CPU alone would be ≈{N(projected, "N2")} of {N(cpuLimit, "N1")} cores (not load saturation); " +
                        $"pod throttling peaked at {N(throttlePeak)}%" + (gcAt is not null ? $"; GC paused up to {gcAt:N0}% of wall time around the stall" : "") + ". " +
                        controlText + evidence));
                }
                else if (latencyRose && exportRose && cpuRose && !earlySaturated && !masked && (control is null || controlClean))
                    f.Add(new("critical", "Metrics back pressure: CONFIRMED — at constant load, growing export cost took CPU from request handling",
                        soakEvidence + evidence));
                else if (exportRose && (cpuRose || latencyRose))
                    f.Add(new("warn", "Metrics back pressure: PARTIAL",
                        soakEvidence + (!latencyRose ? "Latency did not rise enough to call it degradation. " : "") + (!cpuRose ? "CPU did not grow. " : "")
                        + (earlySaturated ? "CPU was already near the limit early on (load, not metrics). " : "") + evidence + " Next: a higher level (more load or longer) or the constrained agent."));
                else
                    f.Add(new("info", "Metrics back pressure: NOT CONFIRMED",
                        soakEvidence + (!exportRose ? "Export cost did not grow enough to matter. " : "Export cost grew but did not affect the app. ") + evidence));
            }
            else if (degraded && pressure && contention && precedes && !loadSaturated && !masked)
                f.Add(new("critical", "Metrics back pressure: CONFIRMED — export cost competed with request handling",
                    evidence + (linked ? " Latency rose with export cost." : " (Latency/export correlation is weak; confirm with a soak test.)")));
            else if (pressure && (contention || degraded))
                f.Add(new("warn", "Metrics back pressure: PARTIAL",
                    evidence
                    + (loadSaturated ? " The degradation is explained by request load reaching the CPU limit." : "")
                    + (!precedes && degrMs is not null ? " Export pressure started only after the app had already degraded: exports were a casualty, not the cause." : "")
                    + (masked ? " A different failure (GC heap pre-allocation or a container OOMKill) also occurred." : "")
                    + (!degraded ? " The app did not degrade." : "") + (!contention ? " No significant CPU contention in the pods." : "")
                    + " A ramp couples load and cardinality; use a soak (Hypothesis tests panel) to separate them."));
            else
                f.Add(new("info", "Metrics back pressure: NOT CONFIRMED",
                    evidence + (!pressure ? " Exports stayed well within their timeout with no retries, so the agent was not pushing back."
                        : " The export path was under pressure but the app showed no contention.")
                             + (masked ? " The failure is explained by something else (see findings above)." : "")));
        }

        // 6. Metrics pipeline.
        // Alloy restarts: the Prometheus-based delta undercounts while Alloy is down (its own metrics stop),
        // so also take the container restartCount from the Kubernetes termination records.
        var alloyKubeRestarts = ev.Terminations.Where(t => t.Namespace == "alloy").Select(t => (double)t.RestartCount).DefaultIfEmpty(0).Max();
        fx["alloyRestarts"] = Math.Max(fx.GetValueOrDefault("alloyRestartsDelta") ?? 0, alloyKubeRestarts);
        if (ev.ExportFailureReasons.Count > 0)
            f.Add(new("info", "Why the app's exports failed",
                string.Join(", ", ev.ExportFailureReasons.OrderByDescending(x => x.Value).Select(x => $"{x.Key}: {x.Value:N0}")) +
                ". Counted per HTTP attempt (status code = the request reached the agent; timeout/connection = it did not complete), " +
                "plus exports that failed before any HTTP request. Summed over the real replicas since they started."));
        if (ev.PrometheusUp)
        {
            var promPct = fx.GetValueOrDefault("promMemPeak") / fx.GetValueOrDefault("promMemLimit");
            if (fx.GetValueOrDefault("promRestartsDelta") > 0)
                f.Add(new("critical", "Prometheus restarted during the run", $"Memory peaked at {Mib(fx.GetValueOrDefault("promMemPeak"))} of {Mib(fx.GetValueOrDefault("promMemLimit"))}. Likely OOMKilled; check for a WAL-replay crash loop."));
            else if (promPct >= 0.85)
                f.Add(new("warn", "Prometheus close to its memory limit", $"Peak {Mib(fx.GetValueOrDefault("promMemPeak"))} of {Mib(fx.GetValueOrDefault("promMemLimit"))}."));
            if (fx.GetValueOrDefault("alloyRestarts") > 0)
                f.Add(new("critical", "Alloy restarted during the run", $"{fx.GetValueOrDefault("alloyRestarts"):N0} restart(s) (Kubernetes restartCount). Memory peaked at {Mib(fx.GetValueOrDefault("alloyMemPeak"))} of {Mib(fx.GetValueOrDefault("alloyMemLimit"))}."));
            if (fx.GetValueOrDefault("alloyRefusedPeak") > 0 || fx.GetValueOrDefault("rwFailedPeak") > 0)
                f.Add(new("warn", "Metrics were refused or failed to write", $"Alloy refused up to {fx.GetValueOrDefault("alloyRefusedPeak"):N0} points/s; remote_write failed up to {fx.GetValueOrDefault("rwFailedPeak"):N0} samples/s."));

            var pipelineFine = !(fx.GetValueOrDefault("promRestartsDelta") > 0) && !(fx.GetValueOrDefault("alloyRestarts") > 0) && !(promPct >= 0.85);
            if (pipelineFine && appTerms.Count > 0)
                f.Add(new("info", "The metrics pipeline was not the bottleneck",
                    $"Prometheus peaked at {Mib(fx.GetValueOrDefault("promMemPeak"))} ({Pct(fx.GetValueOrDefault("promMemPeak"), fx.GetValueOrDefault("promMemLimit"))} of limit), " +
                    $"Alloy at {Mib(fx.GetValueOrDefault("alloyMemPeak"))} ({Pct(fx.GetValueOrDefault("alloyMemPeak"), fx.GetValueOrDefault("alloyMemLimit"))}). The app failed before the pipeline was stressed."));
            f.Add(new("info", "Cardinality reached",
                $"Prometheus head series peaked at {fx.GetValueOrDefault("headSeriesPeak"):N0}. " +
                (fx.GetValueOrDefault("txSeriesPeak") is { } txs
                    ? $"http_transactions_total: {txs:N0} series in Prometheus, {fx.GetValueOrDefault("rtSeriesPeak") ?? 0:N0} with a response_time label. "
                    : "http_transactions_total was not stored in Prometheus (Alloy's PROTECT_PROM drop rule removes high-cardinality series). ") +
                $"The app exported up to {fx.GetValueOrDefault("exportPointsPeak") ?? 0:N0} points per replica per export."));
        }
        else
            f.Add(new("warn", "Prometheus unavailable when the report was generated", ev.PrometheusError ?? "unknown error"));
        if (ev.KubeError is not null) f.Add(new("info", "Kubernetes evidence unavailable", ev.KubeError));

        // 7. Simulated fleet sharing the agent.
        if (r.FleetPods > 0 && r.FleetEnd is { } fe)
        {
            var fs = r.FleetStart;
            var exports = fe.Exports - (fs?.Exports ?? 0);
            var failures = fe.ExportFailures - (fs?.ExportFailures ?? 0);
            var retries = fe.HttpRetries - (fs?.HttpRetries ?? 0);
            var failPct = exports > 0 ? 100.0 * failures / exports : 0;
            f.Add(new(failPct >= 5 ? "warn" : "info", $"Fleet: {r.FleetPods:N0} simulated pods shared the agent",
                $"Besides the {r.Evidence.Pods.Count} real replicas, {r.FleetPods:N0} simulated pods each pushed their own OTLP export " +
                $"(response_time: {fe.ResponseTime}, {fe.ExtraSeries:N0} extra series, up to {fe.PointsPerPod:N0} points per pod per export" +
                (fe.ChurnSeconds > 0 ? $", ~20% of pods replaced every {fe.ChurnSeconds}s ({fe.Churned:N0} so far)" : "") + "). " +
                $"During the run: {exports:N0} fleet exports, {failures:N0} failed ({failPct:N1}%), {retries:N0} HTTP retries. " +
                (fe.AppendedLabels > 0 ? $"Each fleet series carried {fe.AppendedLabels} appended identity labels. " : "") +
                (fe.FailureReasons is { Count: > 0 } fr
                    ? "Fleet failure reasons (since fleet start): " + string.Join(", ", fr.OrderByDescending(x => x.Value).Select(x => $"{x.Key}: {x.Value:N0}")) + ". "
                    : "") +
                "Fleet series are dropped before Prometheus by the PROTECT_PROM rule; Alloy still receives and converts all of them."));
        }

        var order = new[] { "critical", "warn", "info", "ok" };
        f = f.OrderBy(x => Array.IndexOf(order, x.Severity)).ToList();
        var headline = f.FirstOrDefault(x => x.Title.StartsWith("Metrics back pressure: CONFIRMED", StringComparison.Ordinal))?.Title
                       ?? f.FirstOrDefault(x => x.Title.StartsWith("Load saturation", StringComparison.Ordinal))?.Title
                       ?? f.FirstOrDefault(x => x.Severity == "critical" && x.Title.Contains(".NET", StringComparison.Ordinal))?.Title
                       ?? f.FirstOrDefault(x => x.Severity == "critical")?.Title
                       ?? f.FirstOrDefault(x => x.Severity == "warn")?.Title
                       ?? "Healthy: no failures";
        return (headline, f);
    }
}

static class ReportRenderer
{
    public static string Render(RunRecord r)
    {
        var data = JsonSerializer.Serialize(r, Json.Options).Replace("</", "<\\/", StringComparison.Ordinal);
        var title = System.Net.WebUtility.HtmlEncode(r.Name);
        return Template.Replace("__TITLE__", title, StringComparison.Ordinal).Replace("__DATA__", data, StringComparison.Ordinal);
    }

    private const string Template = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>__TITLE__ · Load Test Report</title>
<style>
  :root { --bg:#f6f7f9; --panel:#fff; --ink:#1b1f24; --muted:#5d6673; --line:#e3e6ea; --grid:#eef0f3;
    --ok:#1f9d55; --warn:#c77700; --bad:#d23c3c; --info:#2f6fde;
    --c1:#2f6fde; --c2:#1f9d55; --c3:#d23c3c; --c4:#c77700; --c5:#8a4fd8; --c6:#0e9aa7; --c7:#6b7280;
    --mono: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
  @media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) { --bg:#0f1216; --panel:#171b21; --ink:#e6e9ee; --muted:#9aa4b2; --line:#262c35; --grid:#1f242c;
    --ok:#3ccf7a; --warn:#f0a640; --bad:#ff6b6b; --info:#6b9cff;
    --c1:#6b9cff; --c2:#3ccf7a; --c3:#ff6b6b; --c4:#f0a640; --c5:#b48cff; --c6:#3fd0db; --c7:#9aa4b2; } }
  :root[data-theme="dark"] { --bg:#0f1216; --panel:#171b21; --ink:#e6e9ee; --muted:#9aa4b2; --line:#262c35; --grid:#1f242c;
    --ok:#3ccf7a; --warn:#f0a640; --bad:#ff6b6b; --info:#6b9cff;
    --c1:#6b9cff; --c2:#3ccf7a; --c3:#ff6b6b; --c4:#f0a640; --c5:#b48cff; --c6:#3fd0db; --c7:#9aa4b2; }
  * { box-sizing: border-box; }
  body { margin:0; background:var(--bg); color:var(--ink); font:14px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif; }
  .wrap { max-width:1200px; margin:0 auto; padding:24px 16px 64px; }
  h1 { font-size:24px; margin:0 0 4px; letter-spacing:-0.01em; }
  h2 { font-size:13px; text-transform:uppercase; letter-spacing:.06em; color:var(--muted); margin:28px 0 10px; font-weight:600; }
  .sub { color:var(--muted); }
  .panel { background:var(--panel); border:1px solid var(--line); border-radius:10px; padding:14px 16px; min-width:0; }
  .headline { margin-top:16px; border-left:4px solid var(--bad); }
  .headline.ok { border-left-color:var(--ok); } .headline.warn { border-left-color:var(--warn); }
  .headline .k { font-size:12px; color:var(--muted); text-transform:uppercase; letter-spacing:.06em; }
  .headline .v { font-size:19px; font-weight:650; margin-top:2px; }
  .grid { display:grid; gap:12px; }
  .kpis { grid-template-columns:repeat(6,minmax(0,1fr)); margin-top:12px; }
  @media (max-width:900px) { .kpis { grid-template-columns:repeat(3,minmax(0,1fr)); } }
  @media (max-width:520px) { .kpis { grid-template-columns:repeat(2,minmax(0,1fr)); } }
  .kpi .l { color:var(--muted); font-size:12px; } .kpi .v { font-size:20px; font-weight:650; font-variant-numeric:tabular-nums; }
  .findings { display:grid; gap:8px; }
  .finding { display:grid; grid-template-columns:auto 1fr; gap:4px 12px; align-items:start; }
  .sev { font-size:11px; font-weight:700; text-transform:uppercase; padding:2px 8px; border-radius:999px; letter-spacing:.04em; white-space:nowrap; }
  .sev.critical { background:color-mix(in srgb,var(--bad) 18%,transparent); color:var(--bad); }
  .sev.warn { background:color-mix(in srgb,var(--warn) 20%,transparent); color:var(--warn); }
  .sev.info { background:color-mix(in srgb,var(--info) 16%,transparent); color:var(--info); }
  .sev.ok { background:color-mix(in srgb,var(--ok) 18%,transparent); color:var(--ok); }
  .finding .t { font-weight:600; } .finding .d { grid-column:2; color:var(--muted); font-size:13px; }
  .charts { grid-template-columns:repeat(2,minmax(0,1fr)); }
  @media (max-width:900px) { .charts { grid-template-columns:1fr; } }
  .chart h3 { font-size:13px; margin:0 0 4px; font-weight:600; }
  .chart .note { color:var(--muted); font-size:12px; margin:0 0 4px; }
  .legend { display:flex; flex-wrap:wrap; gap:2px 14px; font-size:12px; color:var(--muted); margin-bottom:4px; }
  .legend i { display:inline-block; width:10px; height:3px; border-radius:2px; vertical-align:middle; margin-right:5px; }
  svg { width:100%; height:200px; display:block; }
  svg text { fill:var(--muted); font-size:10px; font-family:var(--mono); }
  .scroll { overflow-x:auto; }
  table { width:100%; border-collapse:collapse; font-size:12.5px; font-variant-numeric:tabular-nums; }
  th, td { text-align:right; padding:5px 8px; border-bottom:1px solid var(--line); white-space:nowrap; vertical-align:top; }
  th { color:var(--muted); font-weight:600; font-size:11.5px; }
  th:first-child, td:first-child, td.l, th.l { text-align:left; }
  td.wrap { white-space:normal; text-align:left; }
  pre { font-family:var(--mono); font-size:11.5px; background:var(--bg); border:1px solid var(--line); border-radius:8px; padding:10px; overflow-x:auto; margin:6px 0 12px; white-space:pre; }
  .kv { display:grid; grid-template-columns:auto 1fr; gap:3px 16px; font-size:13px; }
  .kv div:nth-child(odd) { color:var(--muted); }
  .muted { color:var(--muted); }
  footer { margin-top:32px; color:var(--muted); font-size:12px; }
  @media print { body { background:#fff; } .panel { break-inside:avoid; } }
</style>
</head>
<body>
<div class="wrap" id="app"></div>
<script>
const R = __DATA__;
const css = (v) => getComputedStyle(document.documentElement).getPropertyValue(v).trim();
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;' }[c]));
const n = (v, d = 0) => v == null || isNaN(v) ? '–' : Number(v).toLocaleString(undefined, { maximumFractionDigits: d, minimumFractionDigits: d });
const B = (b) => b == null ? '–' : b >= 1073741824 ? (b / 1073741824).toFixed(2) + ' GiB' : (b / 1048576).toFixed(0) + ' MiB';
const MS = (ms) => ms == null ? '–' : ms >= 1000 ? (ms / 1000).toFixed(2) + ' s' : Math.round(ms) + ' ms';
const T = (s) => { s = Math.max(0, Math.round(s)); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`; };
const SI = (v) => v == null ? '–' : Math.abs(v) >= 1e6 ? (v / 1e6).toFixed(2) + 'M' : Math.abs(v) >= 1e3 ? (v / 1e3).toFixed(1) + 'k' : (Math.round(v * 100) / 100);
const fmtFor = (u) => u === 'bytes' ? B : u === 'ms' ? MS : u === 'cores' ? (v) => n(v, 2) : u === 'pct' ? (v) => n(v, 0) + '%' : SI;
const colors = ['--c1', '--c2', '--c4', '--c5', '--c6', '--c3', '--c7'];
const when = (u) => new Date(u * 1000).toISOString().replace('T', ' ').replace('.000Z', ' UTC');

function niceMax(v) { if (!(v > 0)) return 1; const p = Math.pow(10, Math.floor(Math.log10(v))), x = v / p; return (x <= 1 ? 1 : x <= 2 ? 2 : x <= 2.5 ? 2.5 : x <= 5 ? 5 : 10) * p; }
function chartHtml(spec, idx) {
  return `<div class="panel chart"><h3>${esc(spec.title)}</h3>${spec.note ? `<p class="note">${esc(spec.note)}</p>` : ''}
    <div class="legend" id="lg${idx}"></div><svg id="ch${idx}"></svg></div>`;
}
function drawChart(idx, spec) {
  const svg = document.getElementById('ch' + idx), lg = document.getElementById('lg' + idx);
  const W = svg.clientWidth || 560, H = 200, L = 56, Rr = 10, Tt = 8, Bb = 20, yf = fmtFor(spec.unit);
  const series = spec.series.filter(s => s.pts.length);
  const ys = series.flatMap(s => s.pts.map(p => p[1])).filter(v => v != null);
  const xs = series.flatMap(s => s.pts.map(p => p[0]));
  const xMin = Math.min(0, ...xs), xMax = Math.max(60, ...xs);
  const yMax = niceMax(Math.max(0, ...ys, ...(spec.refs || []).map(r => r.y)) * 1.05);
  const X = (x) => L + (x - xMin) / (xMax - xMin) * (W - L - Rr), Y = (y) => Tt + (1 - Math.min(y, yMax) / yMax) * (H - Tt - Bb);
  let g = '';
  for (let i = 0; i <= 4; i++) { const y = yMax * i / 4; g += `<line x1="${L}" x2="${W - Rr}" y1="${Y(y)}" y2="${Y(y)}" stroke="${css('--grid')}"/><text x="${L - 6}" y="${Y(y) + 3}" text-anchor="end">${esc(yf(y))}</text>`; }
  for (let i = 0; i <= 5; i++) { const x = xMin + (xMax - xMin) * i / 5; g += `<text x="${X(x)}" y="${H - 5}" text-anchor="middle">${x < 0 ? '-' + T(-x) : T(x)}</text>`; }
  if (xMin < 0) g += `<line x1="${X(0)}" x2="${X(0)}" y1="${Tt}" y2="${H - Bb}" stroke="${css('--c7')}" stroke-dasharray="2 3"/>`;
  for (const r of (spec.refs || [])) g += `<line x1="${L}" x2="${W - Rr}" y1="${Y(r.y)}" y2="${Y(r.y)}" stroke="${css('--c3')}" stroke-dasharray="5 4"/><text x="${W - Rr - 2}" y="${Y(r.y) - 4}" text-anchor="end">${esc(r.label)}</text>`;
  series.forEach((s, i) => {
    let d = '', pen = false;
    for (const [x, y] of s.pts) { if (y == null) { pen = false; continue; } d += (pen ? 'L' : 'M') + X(x).toFixed(1) + ' ' + Y(y).toFixed(1); pen = true; }
    g += `<path d="${d}" fill="none" stroke="${css(s.color || colors[i % colors.length])}" stroke-width="1.8" stroke-linejoin="round" ${s.dash ? `stroke-dasharray="${s.dash}"` : ''}/>`;
  });
  svg.setAttribute('viewBox', `0 0 ${W} ${H}`); svg.innerHTML = g || `<text x="${W / 2}" y="${H / 2}" text-anchor="middle">no data</text>`;
  lg.innerHTML = series.map((s, i) => {
    const vals = s.pts.map(p => p[1]).filter(v => v != null);
    return `<span><i style="background:${css(s.color || colors[i % colors.length])}"></i>${esc(s.name)} <b>${vals.length ? esc(yf(Math.max(...vals))) : '–'}</b> peak</span>`;
  }).join('');
}

const pts = R.points, ms = R.milestones || {};
const sum = (f) => pts.reduce((a, p) => a + f(p), 0);
const ok = sum(p => p.ok), tmo = sum(p => p.timeouts), http = sum(p => p.httpErrors), conn = sum(p => p.connErrors), drop = sum(p => p.dropped);
const failed = tmo + http + conn + drop;
const sevOf = R.findings.some(f => f.severity === 'critical') ? 'bad' : R.findings.some(f => f.severity === 'warn') ? 'warn' : 'ok';
const P = (f) => pts.map(p => [p.t, f(p)]);
const clientCharts = [
  { title: 'Throughput (client)', unit: 'short', series: [
    { name: 'target req/s', pts: P(p => p.target), color: '--c7', dash: '5 4' },
    { name: 'succeeded/s', pts: P(p => p.ok), color: '--c2' },
    { name: 'failed/s', pts: P(p => p.timeouts + p.httpErrors + p.connErrors + p.dropped), color: '--c3' } ] },
  { title: 'Latency (client)', unit: 'ms', series: [
    { name: 'p50', pts: P(p => p.p50), color: '--c1' }, { name: 'p95', pts: P(p => p.p95), color: '--c4' }, { name: 'p99', pts: P(p => p.p99), color: '--c3' } ],
    refs: [{ y: R.config.timeoutMs, label: 'timeout' }, { y: R.config.degradedP95Ms, label: 'degraded' }] },
  { title: 'Failures by type (client, per second)', unit: 'short', series: [
    { name: 'timeouts', pts: P(p => p.timeouts), color: '--c4' }, { name: 'HTTP 5xx', pts: P(p => p.httpErrors), color: '--c3' },
    { name: 'connection', pts: P(p => p.connErrors), color: '--c5' }, { name: 'dropped', pts: P(p => p.dropped), color: '--c7' } ] },
  { title: 'In flight (client)', unit: 'short', series: [{ name: 'in-flight requests', pts: P(p => p.inFlight), color: '--c1' }] },
];
const rs = R.replicaSamples || [], podsRs = [...new Set(rs.map(s => s.pod))].sort();
const replicaCharts = rs.length ? [
  { title: 'Replicas: CPU throttled % (polled directly from pods)', unit: 'pct', note: 'Blank stretches = the pod did not answer the driver.',
    series: podsRs.map(p => ({ name: p.split('-').pop(), pts: rs.filter(s => s.pod === p).map(s => [s.ts - R.startedAtUnix, s.reachable ? s.throttledPct : null]) })) },
] : [];
const allCharts = [...clientCharts, ...(R.evidence.charts || []), ...replicaCharts];

const mrow = (label, m) => `<tr><td>${label}</td><td>${m ? n(m.rate) + ' req/s' : '–'}</td><td>${m ? T(m.t) : '–'}</td><td class="wrap">${m ? esc(m.what) : ''}</td></tr>`;
const c = R.config;
let html = `
  <h1>${esc(R.name)}</h1>
  <div class="sub">${esc(when(R.startedAtUnix))} · ${T(R.points.length ? R.points[R.points.length - 1].t : 0)} · labels: <b>${esc(R.mode)}</b> · agent: <b>${esc(R.agentProfile)}</b>${R.fleetPods ? ` · fleet: <b>${n(R.fleetPods)} simulated pods</b>` : ''}${R.dryRun ? ' · dry run' : ''} · run id <span style="font-family:var(--mono)">${esc(R.id)}</span></div>
  ${R.notes ? `<p>${esc(R.notes)}</p>` : ''}
  <div class="panel headline ${sevOf}"><div class="k">Most likely cause</div><div class="v">${esc(R.headline)}</div>
    <div class="muted" style="margin-top:4px">Stop reason: ${esc(R.stopReason || '–')}</div></div>
  <div class="grid kpis">
    <div class="panel kpi"><div class="l">Max target rate</div><div class="v">${n(Math.max(0, ...pts.map(p => p.target)))}</div></div>
    <div class="panel kpi"><div class="l">Peak succeeded/s</div><div class="v">${n(Math.max(0, ...pts.map(p => p.ok)))}</div></div>
    <div class="panel kpi"><div class="l">Succeeded</div><div class="v">${n(ok)}</div></div>
    <div class="panel kpi"><div class="l">Failed</div><div class="v" style="color:${failed ? css('--bad') : 'inherit'}">${n(failed)}</div></div>
    <div class="panel kpi"><div class="l">Error rate</div><div class="v">${ok + failed ? n(100 * failed / (ok + failed), 1) : '0'}%</div></div>
    <div class="panel kpi"><div class="l">Worst p95</div><div class="v">${MS(Math.max(0, ...pts.map(p => p.p95)))}</div></div>
  </div>

  <h2>Findings</h2>
  <div class="panel findings">${R.findings.map(f => `<div class="finding"><span class="sev ${f.severity}">${esc(f.severity)}</span><span class="t">${esc(f.title)}</span><span class="d">${esc(f.detail)}</span></div>`).join('')}</div>

  <h2>Degradation milestones</h2>
  <div class="panel scroll"><table><tr><th class="l">Milestone</th><th>At rate</th><th>At time</th><th class="l">Detail</th></tr>
    ${mrow('Degraded (p95 ≥ ' + n(c.degradedP95Ms) + ' ms)', ms.degraded)}${mrow('First timeout', ms.firstTimeout)}${mrow('Errors ≥ 5%', ms.errors5)}${mrow('Errors ≥ 50% (collapse)', ms.errors50)}</table></div>
  ${R.checks && R.checks.length ? `<h2>Dry-run checks</h2><div class="panel findings">${R.checks.map(k => `<div class="finding"><span class="sev ${k.ok ? 'ok' : 'critical'}">${k.ok ? 'pass' : 'fail'}</span><span class="t">${esc(k.name)}</span><span class="d">${esc(k.detail)}</span></div>`).join('')}</div>` : ''}

  <h2>Timeline</h2>
  ${R.evidence.prometheusUp ? '' : `<p class="muted">Prometheus charts unavailable: ${esc(R.evidence.prometheusError || '')}</p>`}
  <div class="grid charts">${allCharts.map(chartHtml).join('')}</div>

  <h2>Replicas</h2>
  <div class="panel scroll"><table><tr><th class="l">Pod</th><th>GC heap at start</th><th>GC heap peak</th><th>GC heap limit</th><th>Container mem peak</th><th>Mem limit</th><th>CPU peak</th><th>CPU limit</th><th>OTel cardinality limit</th></tr>
    ${(R.evidence.pods || []).map(p => `<tr><td style="font-family:var(--mono)">${esc(p.pod)}</td><td>${B(p.heapStartBytes)}</td><td>${B(p.heapPeakBytes)}</td><td>${B(p.gcLimitBytes)}</td><td>${B(p.memPeakBytes)}</td><td>${B(p.memLimitBytes)}</td><td>${n(p.cpuPeakCores, 2)}</td><td>${n(p.cpuLimitCores, 2)}</td><td>${n(p.cardinalityLimit)}</td></tr>`).join('') || '<tr><td colspan="9" class="l muted">No data</td></tr>'}</table></div>

  <h2>Ramp steps</h2>
  <div class="panel scroll"><table><tr><th>Rate</th><th>From</th><th>To</th><th>Succeeded</th><th>Failed</th><th>Error %</th><th>Worst p95</th></tr>
    ${(R.steps || []).map(s => `<tr><td>${n(s.rate)}</td><td>${T(s.startT)}</td><td>${T(s.endT)}</td><td>${n(s.ok)}</td><td>${n(s.failed)}</td><td>${n(s.errorPct, 2)}</td><td>${MS(s.p95)}</td></tr>`).join('') || '<tr><td colspan="7" class="muted">Stopped before the first step completed</td></tr>'}</table></div>

  <h2>Container terminations</h2>
  <div class="panel scroll"><table><tr><th class="l">Namespace</th><th class="l">Pod</th><th class="l">Container</th><th class="l">Reason</th><th>Exit code</th><th class="l">Finished</th><th>Restarts</th></tr>
    ${(R.evidence.terminations || []).map(t => `<tr><td class="l">${esc(t.namespace)}</td><td class="l" style="font-family:var(--mono)">${esc(t.pod)}</td><td class="l">${esc(t.container)}</td><td class="l"><b>${esc(t.reason)}</b></td><td>${n(t.exitCode)}</td><td class="l">${esc(t.finishedAt)}</td><td>${n(t.restartCount)}</td></tr>`).join('') || '<tr><td colspan="7" class="l muted">None during the run</td></tr>'}</table>
    ${R.evidence.kubeError ? `<p class="muted">${esc(R.evidence.kubeError)}</p>` : ''}</div>

  <h2>Kubernetes warning events</h2>
  <div class="panel scroll"><table><tr><th class="l">Time (UTC)</th><th class="l">Namespace</th><th class="l">Reason</th><th class="l">Object</th><th>Count</th><th class="l">Message</th></tr>
    ${(R.evidence.events || []).map(e => `<tr><td class="l">${esc(e.time)}</td><td class="l">${esc(e.namespace)}</td><td class="l">${esc(e.reason)}</td><td class="l" style="font-family:var(--mono)">${esc(e.object)}</td><td>${n(e.count)}</td><td class="wrap">${esc(e.message)}</td></tr>`).join('') || '<tr><td colspan="6" class="l muted">None during the run</td></tr>'}</table></div>

  <h2>Crashed container logs (excerpts)</h2>
  ${(R.evidence.logs || []).map(l => `<div class="panel" style="margin-bottom:10px"><b style="font-family:var(--mono)">${esc(l.pod)}</b> <span class="muted">/ ${esc(l.container)} (previous container)</span>
    ${l.topLines.length ? `<div class="muted" style="margin-top:6px">Most frequent error lines</div><pre>${esc(l.topLines.join('\n'))}</pre>` : ''}
    ${l.firstTrace.length ? `<div class="muted">Fatal stack</div><pre>${esc(l.firstTrace.join('\n'))}</pre>` : ''}</div>`).join('') || '<div class="panel muted">No crashed containers during the run</div>'}

  <h2>Configuration</h2>
  <div class="panel kv">
    <div>Start rate / step / step duration</div><div>${n(c.startRate)} req/s · +${n(c.stepRate)} req/s · ${n(c.stepSeconds)} s</div>
    <div>Max rate</div><div>${n(c.maxRate)} req/s</div>
    <div>Request timeout</div><div>${n(c.timeoutMs)} ms</div>
    <div>Degraded threshold (p95)</div><div>${n(c.degradedP95Ms)} ms</div>
    <div>Auto-stop at errors</div><div>${n(c.stopErrorPct)}%</div>
    <div>Driver in-flight cap</div><div>${n(c.maxInFlight)}</div>
    <div>Labels on http_transactions_total</div><div>${esc(R.mode)}</div>
    <div>Agent profile</div><div>${esc(R.agentProfile)}</div>
    <div>Simulated fleet</div><div>${R.fleetPods ? n(R.fleetPods) + ' pods sharing the agent' : 'none'}</div>
    <div>OTel cardinality limit</div><div>${(R.evidence.pods || [])[0] ? n(R.evidence.pods[0].cardinalityLimit) : '–'}</div>
  </div>
  <footer>Generated by transactions-driver at ${esc(when(R.endedAtUnix))}. Self-contained file: the raw run data is embedded in this page.</footer>`;
document.getElementById('app').innerHTML = html;
const render = () => allCharts.forEach((s, i) => drawChart(i, s));
render(); window.addEventListener('resize', render);
</script>
</body>
</html>
""";
}
