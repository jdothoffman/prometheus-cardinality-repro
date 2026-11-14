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
}

sealed class RunRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Notes { get; set; }
    public string Mode { get; set; } = "";
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

sealed record RunSummary(string Id, string Name, string StartedAt, string Mode, bool DryRun, long DurationSeconds,
    int MaxTargetRate, int PeakOkPerSec, Milestone? Degraded, Milestone? FirstTimeout, Milestone? Errors5,
    Milestone? Errors50, string? StopReason, string Headline, int ChecksPassed, int ChecksTotal);

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

    public string? ReportPath(string id)
    {
        if (!Regex.IsMatch(id, "^[A-Za-z0-9._-]+$")) return null;
        var p = Path.Combine(_reports, id + ".html");
        return File.Exists(p) ? p : null;
    }

    private static RunSummary Summarize(RunRecord r) => new(
        r.Id, r.Name, DateTimeOffset.FromUnixTimeSeconds(r.StartedAtUnix).ToString("u", CultureInfo.InvariantCulture),
        r.Mode, r.DryRun, r.DurationSeconds,
        r.Points.Count == 0 ? 0 : r.Points.Max(p => p.Target),
        r.Points.Count == 0 ? 0 : r.Points.Max(p => p.Ok),
        r.Milestones.GetValueOrDefault("degraded"), r.Milestones.GetValueOrDefault("firstTimeout"),
        r.Milestones.GetValueOrDefault("errors5"), r.Milestones.GetValueOrDefault("errors50"),
        r.StopReason, r.Headline, r.Checks.Count(c => c.Ok), r.Checks.Count);

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

    public static (string Headline, List<Finding> Findings) Analyze(RunRecord r)
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

        // 5. Metrics pipeline.
        var fx = ev.Facts;
        if (ev.PrometheusUp)
        {
            var promPct = fx.GetValueOrDefault("promMemPeak") / fx.GetValueOrDefault("promMemLimit");
            if (fx.GetValueOrDefault("promRestartsDelta") > 0)
                f.Add(new("critical", "Prometheus restarted during the run", $"Memory peaked at {Mib(fx.GetValueOrDefault("promMemPeak"))} of {Mib(fx.GetValueOrDefault("promMemLimit"))}. Likely OOMKilled; check for a WAL-replay crash loop."));
            else if (promPct >= 0.85)
                f.Add(new("warn", "Prometheus close to its memory limit", $"Peak {Mib(fx.GetValueOrDefault("promMemPeak"))} of {Mib(fx.GetValueOrDefault("promMemLimit"))}."));
            if (fx.GetValueOrDefault("alloyRestartsDelta") > 0)
                f.Add(new("critical", "Alloy restarted during the run", $"Memory peaked at {Mib(fx.GetValueOrDefault("alloyMemPeak"))} of {Mib(fx.GetValueOrDefault("alloyMemLimit"))}."));
            if (fx.GetValueOrDefault("alloyRefusedPeak") > 0 || fx.GetValueOrDefault("rwFailedPeak") > 0)
                f.Add(new("warn", "Metrics were refused or failed to write", $"Alloy refused up to {fx.GetValueOrDefault("alloyRefusedPeak"):N0} points/s; remote_write failed up to {fx.GetValueOrDefault("rwFailedPeak"):N0} samples/s."));

            var pipelineFine = !(fx.GetValueOrDefault("promRestartsDelta") > 0) && !(fx.GetValueOrDefault("alloyRestartsDelta") > 0) && !(promPct >= 0.85);
            if (pipelineFine && appTerms.Count > 0)
                f.Add(new("info", "The metrics pipeline was not the bottleneck",
                    $"Prometheus peaked at {Mib(fx.GetValueOrDefault("promMemPeak"))} ({Pct(fx.GetValueOrDefault("promMemPeak"), fx.GetValueOrDefault("promMemLimit"))} of limit), " +
                    $"Alloy at {Mib(fx.GetValueOrDefault("alloyMemPeak"))} ({Pct(fx.GetValueOrDefault("alloyMemPeak"), fx.GetValueOrDefault("alloyMemLimit"))}). The app failed before the pipeline was stressed."));
            f.Add(new("info", "Cardinality reached",
                $"Prometheus head series peaked at {fx.GetValueOrDefault("headSeriesPeak"):N0}; http_transactions_total at {fx.GetValueOrDefault("txSeriesPeak"):N0} series, " +
                $"{fx.GetValueOrDefault("rtSeriesPeak") ?? 0:N0} of them carrying a response_time label."));
        }
        else
            f.Add(new("warn", "Prometheus unavailable when the report was generated", ev.PrometheusError ?? "unknown error"));
        if (ev.KubeError is not null) f.Add(new("info", "Kubernetes evidence unavailable", ev.KubeError));

        var order = new[] { "critical", "warn", "info", "ok" };
        f = f.OrderBy(x => Array.IndexOf(order, x.Severity)).ToList();
        var headline = f.FirstOrDefault(x => x.Severity == "critical" && x.Title.Contains(".NET", StringComparison.Ordinal))?.Title
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
  <div class="sub">${esc(when(R.startedAtUnix))} · ${T(R.points.length ? R.points[R.points.length - 1].t : 0)} · labels: <b>${esc(R.mode)}</b>${R.dryRun ? ' · dry run' : ''} · run id <span style="font-family:var(--mono)">${esc(R.id)}</span></div>
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
    <div>Labels</div><div>${esc(R.mode)}</div>
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
