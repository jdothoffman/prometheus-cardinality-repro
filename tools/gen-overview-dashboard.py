#!/usr/bin/env python3
"""Generates monitoring/dashboards/overview.json: "Repro Overview", the one dashboard that shows everything
this repo does while it runs (load, app cost, cardinality, OTLP export, Alloy, Prometheus, traces, fleet,
nodes), with links to the focused dashboards.

    python3 tools/gen-overview-dashboard.py && kubectl --context k3d-cardinality apply -k monitoring/dashboards

Queries reuse the ones proven in cardinality.json / deep-dive.json, so the dashboards agree.
"""
import json
import pathlib

OUT = pathlib.Path(__file__).resolve().parent.parent / "monitoring/dashboards/overview.json"
PROM = {"type": "prometheus", "uid": "prometheus"}
TEMPO = {"type": "tempo", "uid": "tempo"}
APP = 'namespace="demo", container="app"'
ALLOY = 'namespace="alloy", container="alloy"'
PROMC = 'namespace="monitoring", container="prometheus"'
TEMPOC = 'namespace="tracing", container="tempo"'
FLEET = 'namespace="demo", container="fleet"'
NS = 'namespace=~"demo|alloy|monitoring|tracing"'
MI = 1024 * 1024

panels = []
y = 0


def add(p):
    p["id"] = len(panels) + 1
    panels.append(p)
    return p


def row(title):
    global y
    add({"type": "row", "title": title, "collapsed": False, "gridPos": {"x": 0, "y": y, "w": 24, "h": 1}, "panels": []})
    y += 1


def t(expr, legend="", ref=None, exemplar=False, **kw):
    d = {"datasource": PROM, "expr": expr, "legendFormat": legend, "range": True, "exemplar": exemplar}
    if ref:
        d["refId"] = ref
    d.update(kw)
    return d


def refs(targets):
    for i, x in enumerate(targets):
        x.setdefault("refId", chr(ord("A") + i))
    return targets


def ts(title, targets, x, w, h=8, unit="short", desc="", overrides=None, stack=False, fill=8, min0=True, thresholds=None):
    custom = {"lineWidth": 2, "fillOpacity": fill, "spanNulls": False, "showPoints": "never"}
    if stack:
        custom["stacking"] = {"mode": "normal", "group": "A"}
    if thresholds:
        custom["thresholdsStyle"] = {"mode": "dashed"}
    defaults = {"unit": unit, "custom": custom}
    if min0:
        defaults["min"] = 0
    if thresholds:
        defaults["thresholds"] = {"mode": "absolute", "steps": thresholds}
    return add({"type": "timeseries", "title": title, "description": desc, "datasource": PROM,
                "gridPos": {"x": x, "y": y, "w": w, "h": h},
                "fieldConfig": {"defaults": defaults, "overrides": overrides or []},
                "options": {"legend": {"displayMode": "list", "placement": "bottom"}, "tooltip": {"mode": "multi", "sort": "desc"}},
                "targets": refs(targets)})


def limit_line(name):
    """Override: draw a series named `name` as a dashed red line (memory/CPU limits)."""
    return {"matcher": {"id": "byName", "options": name},
            "properties": [{"id": "color", "value": {"mode": "fixed", "fixedColor": "red"}},
                           {"id": "custom.lineStyle", "value": {"fill": "dash", "dash": [10, 10]}},
                           {"id": "custom.fillOpacity", "value": 0}]}


def color(name, c):
    return {"matcher": {"id": "byName", "options": name}, "properties": [{"id": "color", "value": {"mode": "fixed", "fixedColor": c}}]}


def stat(title, expr, x, w=3, h=4, unit="short", desc="", mappings=None, steps=None, decimals=None, graph="none", link=None):
    defaults = {"unit": unit, "color": {"mode": "thresholds"},
                "thresholds": {"mode": "absolute", "steps": steps or [{"color": "blue", "value": None}]}}
    if mappings:
        defaults["mappings"] = mappings
    if decimals is not None:
        defaults["decimals"] = decimals
    if link:
        defaults["links"] = [link]
    return add({"type": "stat", "title": title, "description": desc, "datasource": PROM,
                "gridPos": {"x": x, "y": y, "w": w, "h": h},
                "fieldConfig": {"defaults": defaults, "overrides": []},
                "options": {"reduceOptions": {"calcs": ["lastNotNull"], "fields": "", "values": False},
                            "colorMode": "background", "graphMode": graph, "textMode": "value", "justifyMode": "center"},
                "targets": refs([t(expr, instant=False)])})


def gauge(title, expr, x, w=3, h=4, desc=""):
    return add({"type": "gauge", "title": title, "description": desc, "datasource": PROM,
                "gridPos": {"x": x, "y": y, "w": w, "h": h},
                "fieldConfig": {"defaults": {"unit": "percentunit", "min": 0, "max": 1, "color": {"mode": "thresholds"},
                                             "thresholds": {"mode": "absolute", "steps": [
                                                 {"color": "green", "value": None}, {"color": "orange", "value": 0.7},
                                                 {"color": "red", "value": 0.9}]}}, "overrides": []},
                "options": {"reduceOptions": {"calcs": ["lastNotNull"], "fields": "", "values": False},
                            "showThresholdLabels": False, "showThresholdMarkers": True},
                "targets": refs([t(expr)])})


def vmap(d):
    return [{"type": "value", "options": {k: {"text": v[0], "color": v[1], "index": i} for i, (k, v) in enumerate(d.items())}}]


GREEN = [{"color": "green", "value": None}]


def red_above(v, warn=None):
    s = [{"color": "green", "value": None}]
    if warn is not None:
        s.append({"color": "orange", "value": warn})
    s.append({"color": "red", "value": v})
    return s


# ---------------------------------------------------------------- At a glance
row("At a glance: is it running, how is it configured, is anything breaking?")
add({"type": "text", "title": "Pipeline and where to look", "gridPos": {"x": 0, "y": y, "w": 6, "h": 12},
     "options": {"mode": "markdown", "content": "\n".join([
         "```",
         "k6 / console ─▶ transactions-api ×4",
         "                │ OTLP every 10s",
         "                ▼   (metrics + spans)",
         "            Alloy (agent pool)",
         "        remote_write │      │ OTLP",
         "                ▼    │      ▼",
         "        Prometheus ◀─┘    Tempo",
         "                 ╲      ╱",
         "                  Grafana",
         "```",
         "**Start a run:** the [load-test console](http://localhost:8080) (needs `make ui`) or `make load`.",
         "",
         "**Drill in:** [Cardinality Repro](/d/cardinality-repro) · [Deep Dive](/d/cardinality-deep-dive) · "
         "[Trace Correlation](/d/trace-correlation) · [Traces Drilldown](/a/grafana-exploretraces-app/explore) · "
         "[Explore](/explore)",
         "",
         "Red markers on graphs = container restarts; orange = `response_time` label switched.",
     ])}})
# Tiles: three rows of six, right of the text panel.
X0, W = 6, 3
stat("Offered load (k6)", 'sum(rate(k6_http_reqs_total[1m]))', X0, unit="reqps", graph="area",
     desc="Requests/s k6 sent (make load). The console's own load isn't in Prometheus: see Transactions / s.")
stat("Transactions / s recorded", 'sum(rate(http_transactions_total{job="transactions-api"}[1m]))', X0 + W, unit="reqps", graph="area",
     desc="What the app recorded, as it arrives through Alloy (about 20 s behind).")
stat("Server p95", 'histogram_quantile(0.95, sum by (le) (rate(http_transaction_duration_seconds_bucket{job="transactions-api"}[1m])))',
     X0 + 2 * W, unit="s", graph="area", steps=red_above(0.25, 0.1), desc="From the bounded duration histogram.")
stat("Client failures (k6)", 'max(k6_http_req_failed_rate) or vector(0)', X0 + 3 * W, unit="percentunit", decimals=2,
     steps=red_above(0.01, 0.001), desc="Share of k6 requests that failed or timed out.")
stat("App replicas available", 'sum(kube_deployment_status_replicas_available{namespace="demo", deployment="transactions-api"})',
     X0 + 4 * W, steps=[{"color": "red", "value": None}, {"color": "orange", "value": 1}, {"color": "green", "value": 4}],
     desc="Of 4. Below 4 means a replica is restarting or failing its readiness probe.")
stat("Restarts (last 1h)", f'sum(increase(kube_pod_container_status_restarts_total{{{NS}}}[1h])) or vector(0)', X0 + 5 * W,
     decimals=0, steps=red_above(1), desc="App, Alloy, Prometheus, Grafana and Tempo containers. Details in the Cluster section.")
y += 4
stat("http_transactions_total series", 'count(http_transactions_total)', X0, steps=red_above(100000, 40000),
     desc="Bounded is about 32k with 200 pod names, under 1k with VIRTUAL_PODS=1.")
stat("Prometheus head series", 'max(prometheus_tsdb_head_series)', X0 + W, steps=red_above(500000, 250000), graph="area")
gauge("Prometheus mem / limit", f'max(container_memory_working_set_bytes{{{PROMC}}}) / max(kube_pod_container_resource_limits{{{PROMC}, resource="memory"}})', X0 + 2 * W)
gauge("Alloy mem / limit", f'max(container_memory_working_set_bytes{{{ALLOY}}}) / max(kube_pod_container_resource_limits{{{ALLOY}, resource="memory"}})', X0 + 3 * W)
stat("Alloy refused points / s", 'sum(rate(otelcol_receiver_refused_metric_points_total[1m])) or vector(0)', X0 + 4 * W,
     steps=red_above(1), desc="memory_limiter pushing back: those exports fail and the app retries them.")
stat("OTLP export failures / s", 'sum(rate(otlp_export_failures_total{job="transactions-api"}[1m])) or vector(0)', X0 + 5 * W,
     decimals=2, steps=red_above(0.05),
     desc="Exports the app's SDK didn't deliver. Over 64 MiB they're dropped before sending, and the pod goes dark in Prometheus.")
y += 4
stat("response_time label", 'max(transactions_cardinality_unbounded)', X0,
     mappings=vmap({"0": ("off", "green"), "1": ("ON (bug)", "red")}), desc="CARDINALITY_MODE / console label switch.")
stat("Pod names per replica", 'max(transactions_virtual_pods)', X0 + W, steps=GREEN,
     desc="VIRTUAL_PODS: 1 = production-like (make prod-pods), 50 = the L1–L5 setting (make lab-pods).")
stat("SDK cardinality limit", 'max(transactions_config_cardinality_limit)', X0 + 2 * W, steps=GREEN,
     desc="OTEL_CARDINALITY_LIMIT. The SDK default is 2000; scope 'all' pre-allocates about 4 KiB heap per unit (run-A sweep).")
stat("Tag allow-list (F1)", 'max(transactions_config_tag_allowlist)', X0 + 3 * W,
     mappings=vmap({"0": ("off", "text"), "1": ("on", "green")}), desc="make fix-app / unfix-app.")
stat("Tracing", 'max(transactions_config_trace_sample_ratio)', X0 + 4 * W, unit="percentunit",
     mappings=vmap({"0": ("off", "text")}), steps=GREEN,
     desc="Configured trace sampling, live (console or make traces-on RATIO=…). Hard cap 75%. Exemplars ride on the metrics while it's on.",
     link={"title": "Trace Correlation", "url": "/d/trace-correlation"})
stat("Agent profile", f'max(kube_pod_container_resource_limits{{{ALLOY}, resource="memory"}})', X0 + 5 * W, unit="bytes",
     mappings=vmap({str(384 * MI): ("constrained", "orange"), str(1024 * MI): ("normal", "green")}), steps=GREEN,
     desc="From Alloy's memory limit: 384 MiB = make agent-constrained, 1 GiB = make agent-normal.")
y += 4

# ---------------------------------------------------------------- 1. Load and latency
row("1 · Load and latency: is the app degrading?")
ts("Throughput", [
    t('sum(rate(k6_http_reqs_total[1m]))', "k6 offered req/s"),
    t('sum(rate(http_transactions_total{job="transactions-api"}[1m]))', "transactions/s recorded"),
    t('sum by (state) (rate(http_transactions_total{job="transactions-api"}[1m]))', "{{state}}"),
], 0, 8, unit="reqps")
ts("Response time (diamonds = sampled requests: hover → View trace)", [
    t('histogram_quantile(0.50, sum by (le) (rate(http_transaction_duration_seconds_bucket{job="transactions-api"}[1m])))', "server p50"),
    t('histogram_quantile(0.95, sum by (le) (rate(http_transaction_duration_seconds_bucket{job="transactions-api"}[1m])))', "server p95", exemplar=True),
    t('histogram_quantile(0.99, sum by (le) (rate(http_transaction_duration_seconds_bucket{job="transactions-api"}[1m])))', "server p99"),
    t('max(k6_http_req_duration_p95)', "k6 client p95"),
    t('max(k6_http_req_duration_p99)', "k6 client p99"),
], 8, 8, unit="s", fill=0, desc="Server side: http_transaction_duration_seconds (via Alloy). Client side: k6 (make load only).")
ts("Failures", [
    t('max(k6_http_req_failed_rate)', "k6 failed share"),
    t('(sum(rate(http_server_request_duration_seconds_count{job="transactions-api", http_response_status_code!~"2.."}[1m])) or vector(0)) / sum(rate(http_server_request_duration_seconds_count{job="transactions-api"}[1m]))', "server non-2xx share"),
    t('(sum(rate(http_transactions_total{job="transactions-api", state="cancelled"}[1m])) or vector(0)) / sum(rate(http_transactions_total{job="transactions-api"}[1m]))', "cancelled by client (timeout)"),
], 16, 8, unit="percentunit", desc="Business outcomes (declined/failed) are HTTP 200; these are overload failures only.")
y += 8

# ---------------------------------------------------------------- 2. App replicas
row("2 · App replicas: what the SDK's state costs the pod")
ts("CPU per pod vs limit", [
    t(f'sum by (pod) (rate(container_cpu_usage_seconds_total{{{APP}}}[1m]))', "{{pod}}"),
    t(f'max(kube_pod_container_resource_limits{{{APP}, resource="cpu"}})', "limit"),
], 0, 6, unit="cores", fill=0, overrides=[limit_line("limit")])
ts("CPU throttling per pod", [
    t(f'sum by (pod) (rate(container_cpu_cfs_throttled_periods_total{{{APP}}}[1m])) / sum by (pod) (rate(container_cpu_cfs_periods_total{{{APP}}}[1m]))', "{{pod}}"),
], 6, 6, unit="percentunit", fill=0, desc="Share of CFS periods throttled. Bounded runs: about 9–13% mean at 80% load (noise floor about 3 points).")
ts("Memory per pod vs limit", [
    t(f'sum by (pod) (container_memory_working_set_bytes{{{APP}}})', "{{pod}}"),
    t(f'max(kube_pod_container_resource_limits{{{APP}, resource="memory"}})', "limit"),
], 12, 6, unit="bytes", fill=0, overrides=[limit_line("limit")])
ts(".NET GC heap per pod", [
    t('sum by (instance) (dotnet_gc_last_collection_heap_size_bytes{job="transactions-api"})', "{{instance}}"),
], 18, 6, unit="bytes", fill=0,
   desc="Heap after the last GC (all generations). High-cardinality SDK state shows up here: about 40 MiB bounded, 480 MiB at 200k series.")
y += 8
ts("GC pause time per pod", [
    t('sum by (instance) (rate(dotnet_gc_pause_time_seconds_total{job="transactions-api"}[1m]))', "{{instance}}"),
], 0, 6, unit="percentunit", fill=0, desc="Seconds paused per second.")
ts("Thread pool queue / active requests", [
    t('sum by (instance) (dotnet_thread_pool_queue_length{job="transactions-api"})', "queue {{instance}}"),
    t('sum by (instance) (http_server_active_requests{job="transactions-api"})', "active {{instance}}"),
], 6, 6, fill=0)
ts("App restarts", [
    t(f'sum by (pod) (kube_pod_container_status_restarts_total{{{APP}}})', "{{pod}}"),
], 12, 6, fill=0, desc="Liveness kills show here without OOMKilled (the run-A 200k failure mode).")
ts("Allocation rate per pod", [
    t('sum by (instance) (rate(dotnet_gc_heap_allocated_bytes_total{job="transactions-api"}[1m]))', "{{instance}}"),
], 18, 6, unit="Bps", fill=0)
y += 8

# ---------------------------------------------------------------- 3. Cardinality
row("3 · Cardinality: the core of the test")
CW = 4
stat("http_transactions_total series", 'count(http_transactions_total)', 0, w=CW, steps=red_above(100000, 40000), graph="area")
stat("Distinct pod values", 'count(count by (pod) (http_transactions_total))', CW, w=CW, steps=GREEN, graph="area")
stat("Distinct response_time values", 'count(count by (response_time) (http_transactions_total{response_time!=""})) or vector(0)', 2 * CW, w=CW,
     steps=red_above(100), graph="area")
stat("New series / s (Prometheus)", 'sum(rate(prometheus_tsdb_head_series_created_total[1m]))', 3 * CW, w=CW, steps=red_above(500, 50),
     graph="area", decimals=1)
stat("Appended labels", 'max(transactions_config_appended_labels)', 4 * CW, w=CW, steps=GREEN, desc="APPENDED_LABELS (make appended-labels N=…)")
stat("Duration histogram series", 'count(http_transaction_duration_seconds_bucket)', 5 * CW, w=CW, steps=GREEN,
     desc="Bounded by design: the right home for response time. Exemplars don't change this count.")
y += 4
ts("Series over time", [
    t('max(prometheus_tsdb_head_series)', "Prometheus head series"),
    t('count(http_transactions_total)', "http_transactions_total series"),
    t('max(prometheus_remote_write_wal_storage_active_series)', "Alloy WAL active series"),
    t('count(count by (response_time) (http_transactions_total{response_time!=""}))', "distinct response_time values"),
], 0, 9, fill=0)
add({"type": "bargauge", "title": "Distinct values per label (http_transactions_total)", "datasource": PROM,
     "gridPos": {"x": 9, "y": y, "w": 7, "h": 8},
     "fieldConfig": {"defaults": {"unit": "short", "min": 0, "color": {"mode": "continuous-GrYlRd"}}, "overrides": []},
     "options": {"orientation": "horizontal", "displayMode": "gradient", "showUnfilled": True,
                 "reduceOptions": {"calcs": ["lastNotNull"], "fields": "", "values": False}},
     "targets": refs([t(f'count(count by ({l}) (http_transactions_total{{{l}!=""}})) or vector(0)', l, instant=True, range=False)
                      for l in ("consume_type", "state", "product", "pod", "response_time", "request_id")])})
add({"type": "table", "title": "Top 10 metrics by series (transactions-api)", "datasource": PROM,
     "gridPos": {"x": 16, "y": y, "w": 8, "h": 8},
     "fieldConfig": {"defaults": {"unit": "short"}, "overrides": []},
     "options": {"showHeader": True, "sortBy": [{"displayName": "Value", "desc": True}], "cellHeight": "sm"},
     "transformations": [{"id": "organize", "options": {"excludeByName": {"Time": True}, "renameByName": {"__name__": "metric", "Value": "series"}}}],
     "targets": refs([t('topk(10, count by (__name__) ({job="transactions-api"}))', instant=True, format="table", range=False)])})
y += 8

# ---------------------------------------------------------------- 4. OTLP export
row("4 · OTLP export: app → Alloy (measured inside the app)")
ts("Export duration per replica", [t('max by (instance) (otlp_export_last_duration_seconds{job="transactions-api"})', "{{instance}}")],
   0, 6, unit="s", fill=0, thresholds=[{"color": "transparent", "value": None}, {"color": "red", "value": 8}],
   desc="Serialize + send + retries. Dashed line: the 8 s export timeout.")
ts("Points per export", [t('max by (instance) (otlp_export_last_points{job="transactions-api"})', "{{instance}}")], 6, 6, fill=0)
ts("Bytes per export", [t('max by (instance) (otlp_export_last_bytes{job="transactions-api"})', "{{instance}}")], 12, 6, unit="bytes",
   fill=0, thresholds=[{"color": "transparent", "value": None}, {"color": "red", "value": 64 * MI}],
   desc="Dashed line: 64 MiB, where OTel .NET drops the export before sending (MaxRequestSizeBytes).")
ts("Export failures + HTTP retries / s", [
    t('sum by (instance) (rate(otlp_export_failures_total{job="transactions-api"}[1m]))', "failures {{instance}}"),
    t('sum by (instance) (rate(otlp_export_http_retries_total{job="transactions-api"}[1m]))', "retries {{instance}}"),
], 18, 6, fill=0)
y += 8

# ---------------------------------------------------------------- 5. Alloy
row("5 · Alloy agent (own node pool)")
ts("OTLP metric points received / s", [
    t('sum(rate(otelcol_receiver_accepted_metric_points_total[1m]))', "accepted"),
    t('sum(rate(otelcol_receiver_refused_metric_points_total[1m]))', "refused (memory_limiter)"),
    t('sum(rate(otelcol_receiver_failed_metric_points_total[1m]))', "failed"),
], 0, 6, fill=0, overrides=[color("refused (memory_limiter)", "red"), color("failed", "orange")],
   desc="Includes the simulated fleet when it's on.")
ts("Memory vs limit", [
    t(f'sum(container_memory_working_set_bytes{{{ALLOY}}})', "working set"),
    t(f'max(kube_pod_container_resource_limits{{{ALLOY}, resource="memory"}})', "limit"),
], 6, 6, unit="bytes", overrides=[limit_line("limit")])
ts("CPU vs limit", [
    t(f'sum(rate(container_cpu_usage_seconds_total{{{ALLOY}}}[1m]))', "CPU"),
    t(f'max(kube_pod_container_resource_limits{{{ALLOY}, resource="cpu"}})', "limit"),
], 12, 6, unit="cores", overrides=[limit_line("limit")], desc="No limit line = no CPU limit (normal profile).")
ts("remote_write → Prometheus", [
    t('sum(rate(prometheus_remote_storage_samples_total[1m]))', "samples sent/s"),
    t('sum(rate(prometheus_remote_storage_samples_failed_total[1m]))', "failed/s"),
    t('sum(rate(prometheus_remote_storage_samples_retried_total[1m]))', "retried/s"),
    t('sum(prometheus_remote_storage_samples_pending)', "pending"),
    t('sum(rate(prometheus_remote_storage_exemplars_total[1m]))', "exemplars sent/s"),
], 18, 6, fill=0, overrides=[color("failed/s", "red")])
y += 8

# ---------------------------------------------------------------- 6. Prometheus
row("6 · Prometheus (1 GiB limit: the OOM trigger)")
ts("Head series", [t('max(prometheus_tsdb_head_series)', "head series")], 0, 6)
ts("Memory vs limit", [
    t(f'sum(container_memory_working_set_bytes{{{PROMC}}})', "working set"),
    t(f'max(kube_pod_container_resource_limits{{{PROMC}, resource="memory"}})', "limit"),
], 6, 6, unit="bytes", overrides=[limit_line("limit")])
ts("Series created / removed per s", [
    t('sum(rate(prometheus_tsdb_head_series_created_total[1m]))', "created/s"),
    t('sum(rate(prometheus_tsdb_head_series_removed_total[1m]))', "removed/s (head GC)"),
], 12, 6, fill=0)
ts("Restarts and samples ingested / s", [
    t(f'max(kube_pod_container_status_restarts_total{{{PROMC}}})', "restarts"),
    t('sum(rate(prometheus_tsdb_head_samples_appended_total[1m]))', "samples/s"),
], 18, 6, fill=0, overrides=[{"matcher": {"id": "byName", "options": "samples/s"},
                                "properties": [{"id": "custom.axisPlacement", "value": "right"}]}])
y += 8

# ---------------------------------------------------------------- 7. Traces
row("7 · Traces and exemplars (make tracing + make traces-on)")
ts("Spans / s through the pipeline", [
    t('sum(rate(otelcol_receiver_accepted_spans_total[1m]))', "Alloy accepted"),
    t('sum(rate(otelcol_receiver_refused_spans_total[1m]))', "Alloy refused"),
    t('sum(rate(otelcol_exporter_sent_spans_total[1m]))', "Alloy → Tempo"),
    t('sum(rate(tempo_distributor_spans_received_total[1m]))', "Tempo received"),
], 0, 8, fill=0, overrides=[color("Alloy refused", "red")], desc="About 3 spans per sampled transaction.")
ts("Exemplars (trace ids on samples)", [
    t('sum(rate(prometheus_tsdb_exemplar_exemplars_appended_total[1m]))', "appended/s"),
    t('max(prometheus_tsdb_exemplar_exemplars_in_storage)', "in storage"),
    t('max(prometheus_tsdb_exemplar_max_exemplars)', "ring buffer size"),
], 8, 8, fill=0, overrides=[limit_line("ring buffer size"),
                            {"matcher": {"id": "byName", "options": "appended/s"}, "properties": [{"id": "custom.axisPlacement", "value": "right"}]}],
   desc="One fixed ring buffer for all series: exemplars never add series.")
ts("Tempo memory and CPU", [
    t(f'sum(container_memory_working_set_bytes{{{TEMPOC}}})', "memory"),
    t(f'max(kube_pod_container_resource_limits{{{TEMPOC}, resource="memory"}})', "limit"),
    t(f'sum(rate(container_cpu_usage_seconds_total{{{TEMPOC}}}[1m]))', "CPU cores"),
], 16, 8, unit="bytes", overrides=[limit_line("limit"),
                                   {"matcher": {"id": "byName", "options": "CPU cores"},
                                    "properties": [{"id": "unit", "value": "cores"}, {"id": "custom.axisPlacement", "value": "right"}]}])
y += 8
ts("Trace sampling: configured vs observed (cap 75%)", [
    t('max(transactions_config_trace_sample_ratio)', "configured"),
    # 2m window, and only above 20 tx/s: at load edges rate() extrapolation of two counters (one of which only
    # exists after the first traced request) gives meaningless ratios, even above 1.
    t('(sum(rate(transactions_traced_total{job="transactions-api"}[2m])) / sum(rate(http_transactions_total{job="transactions-api"}[2m])))'
      ' and on () (sum(rate(http_transactions_total{job="transactions-api"}[2m])) > 20)', "observed (traced / all)"),
    t('max(transactions_config_trace_sample_ratio_max)', "cap"),
], 0, 10, unit="percentunit", fill=0, overrides=[limit_line("cap"), color("configured", "blue"), color("observed (traced / all)", "green")],
   desc="Observed = transactions whose trace was sampled ÷ all transactions, counted in the app (2-min window, shown above 20 tx/s). The cap bounds the probability, so a short window can read a little above the ratio: at 300 tx/s and 75% it stayed within 74–77%.")
add({"type": "table", "title": "Slowest recent sampled transactions (click a Trace ID; more in Trace Correlation)", "datasource": TEMPO,
     "gridPos": {"x": 10, "y": y, "w": 14, "h": 8},
     "fieldConfig": {"defaults": {}, "overrides": []},
     "options": {"showHeader": True, "cellHeight": "sm", "sortBy": [{"displayName": "Duration", "desc": True}]},
     "targets": [{"refId": "A", "datasource": TEMPO, "queryType": "traceql", "limit": 20, "tableType": "traces",
                  "query": '{resource.service.name="transactions-api" && kind=server && duration >= 50ms}'
                           ' | select(span.transaction.state, span.transaction.product, span.transaction.consume_type, resource.service.instance.id)'}]})
y += 8

# ---------------------------------------------------------------- 8. Fleet
row("8 · Simulated fleet (make fleet-on: 2 × 150 pods on the fleet node)")
stat("Fleet replicas", 'sum(kube_deployment_status_replicas_available{namespace="demo", deployment="transactions-fleet"}) or vector(0)', 0, w=4, h=8,
     steps=GREEN, desc="Each replica simulates 150 pods, each with its own OTLP export to Alloy.")
ts("Fleet CPU and memory", [
    t(f'sum(rate(container_cpu_usage_seconds_total{{{FLEET}}}[1m]))', "CPU cores"),
    t(f'sum(container_memory_working_set_bytes{{{FLEET}}})', "memory"),
], 4, 10, unit="cores", overrides=[{"matcher": {"id": "byName", "options": "memory"},
                                    "properties": [{"id": "unit", "value": "bytes"}, {"id": "custom.axisPlacement", "value": "right"}]}])
ts("Alloy inbound load vs the app's own", [
    t('sum(rate(otelcol_receiver_accepted_metric_points_total[1m])) + sum(rate(otelcol_receiver_refused_metric_points_total[1m]))', "points/s offered to Alloy (all senders)"),
    t('sum(otlp_export_last_points{job="transactions-api"}) / 10', "real replicas' points/s (points per export / 10 s)"),
], 14, 10, fill=0, desc="The difference is the fleet. The protect profiles drop the fleet's series before Prometheus, so they appear only here.")
y += 8

# ---------------------------------------------------------------- 9. Cluster
row("9 · Cluster: nodes, restarts, terminations")
ts("Node memory used / capacity", [
    t('sum by (node) (container_memory_working_set_bytes{container!="", container!="POD"}) / on (node) max by (node) (kube_node_status_capacity{resource="memory"})', "{{node}}"),
], 0, 12, unit="percentunit", fill=0, desc="Docker Desktop VM: 8 GB for all nodes together.")
ts("Node CPU used (cores)", [
    t('sum by (node) (rate(container_cpu_usage_seconds_total{container!="", container!="POD"}[1m]))', "{{node}}"),
], 12, 12, unit="cores", fill=0, desc="App nodes are capped at 4 CPUs (make node-cpus), the agent and fleet nodes at 2.")
y += 8
add({"type": "table", "title": "Restarts and last termination reason", "datasource": PROM,
     "gridPos": {"x": 0, "y": y, "w": 24, "h": 7},
     "fieldConfig": {"defaults": {"unit": "short"}, "overrides": []},
     "options": {"showHeader": True, "cellHeight": "sm", "sortBy": [{"displayName": "restarts", "desc": True}]},
     "transformations": [
         {"id": "merge", "options": {}},
         {"id": "organize", "options": {"excludeByName": {"Time": True},
                                        "renameByName": {"Value #A": "restarts", "Value #B": "last terminated (1 = this reason)"}}}],
     "targets": [
         t(f'sum by (namespace, pod, container) (kube_pod_container_status_restarts_total{{{NS}}}) > 0', ref="A", instant=True, format="table", range=False),
         t(f'max by (namespace, pod, container, reason) (kube_pod_container_status_last_terminated_reason{{{NS}}}) == 1', ref="B", instant=True, format="table", range=False),
     ]})
y += 7

dashboard = {
    "uid": "repro-overview",
    "title": "Repro Overview",
    "description": "Everything this repo does while it runs, in one place. Generated by tools/gen-overview-dashboard.py.",
    "tags": ["cardinality", "repro", "overview"],
    "timezone": "browser", "refresh": "10s", "schemaVersion": 39, "editable": True,
    "time": {"from": "now-30m", "to": "now"},
    "graphTooltip": 1,  # shared crosshair across panels
    "links": [
        {"title": "Cardinality Repro", "type": "link", "url": "/d/cardinality-repro", "icon": "dashboard", "keepTime": True},
        {"title": "Deep Dive", "type": "link", "url": "/d/cardinality-deep-dive", "icon": "dashboard", "keepTime": True},
        {"title": "Trace Correlation", "type": "link", "url": "/d/trace-correlation", "icon": "dashboard", "keepTime": True},
        {"title": "Traces Drilldown", "type": "link", "url": "/a/grafana-exploretraces-app/explore", "icon": "external link", "keepTime": True},
        {"title": "Load-test console (make ui)", "type": "link", "url": "http://localhost:8080", "icon": "external link", "targetBlank": True},
        {"title": "Prometheus (make ui)", "type": "link", "url": "http://localhost:9090", "icon": "external link", "targetBlank": True},
    ],
    "annotations": {"list": [
        {"builtIn": 1, "datasource": {"type": "grafana", "uid": "-- Grafana --"}, "enable": True, "hide": True,
         "iconColor": "rgba(0, 211, 255, 1)", "name": "Annotations & Alerts", "type": "dashboard"},
        {"datasource": PROM, "enable": True, "iconColor": "red", "name": "Container restarts",
         "expr": f'sum by (namespace, pod, container) (changes(kube_pod_container_status_restarts_total{{{NS}}}[1m])) > 0',
         "step": "60s", "titleFormat": "restart", "textFormat": "{{namespace}}/{{pod}} ({{container}})", "tagKeys": "namespace"},
        {"datasource": PROM, "enable": True, "iconColor": "orange", "name": "response_time label switched",
         "expr": 'max(changes(transactions_cardinality_unbounded[1m])) > 0', "step": "60s",
         "titleFormat": "label set changed", "textFormat": "response_time label switched on/off"},
    ]},
    "templating": {"list": []},
    "panels": panels,
}
OUT.write_text(json.dumps(dashboard, indent=2, ensure_ascii=False) + "\n")
print(f"{OUT.relative_to(OUT.parent.parent.parent)}: {len(panels)} panels")
