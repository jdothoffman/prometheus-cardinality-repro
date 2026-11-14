# Prometheus high-cardinality repro: .NET OTel → Grafana Alloy → Prometheus

A local reproduction of a production incident. A .NET service records each HTTP transaction with the labels `consume_type`, `state`, `product` and `pod` across about 200 pods. It also records **the response time as a float label**. The OTel SDK holds every series in memory and pushes them on a schedule to a Grafana Alloy agent on its own node pool, which forwards them to Prometheus. Series counts explode, and Prometheus and/or Alloy OOM.

```
 k6 (300 req/s) ──HTTP──▶ transactions-api ×4        ──OTLP/HTTP every 10s──▶  Alloy           ──remote_write──▶ Prometheus ──▶ Grafana
                          (app pool, 2 nodes)            (background thread)       (agent pool,                    (1Gi limit)
                          OTel SDK aggregates in memory                             tainted node)
                          50 simulated pods each = 200 pod names                    batch + WAL
 k6 ──remote_write (its own latency, bypasses Alloy)──────────────────────────────────────────────────▶ Prometheus
```

All components are free and open source: k3d/k3s, kube-prometheus-stack 93.3.0, Grafana Alloy v1.21.3 (chart 1.15.0), OpenTelemetry .NET 1.21, k6 1.8.3.

## Cluster and node pools

| Node | Pool | Runs |
|---|---|---|
| `k3d-cardinality-server-0` | (control plane) | k3s, and some monitoring pods |
| `k3d-cardinality-agent-0/1` | `pool=app` | transactions-api, k6, Prometheus/Grafana |
| `k3d-agentpool-0` | `pool=agent`, taint `pool=agent:NoSchedule` | **Grafana Alloy only** |

The node memory caps add up to about 7.25GB, inside Docker Desktop's 8GB VM. If you can, raise Docker Desktop to 12GB or more (Settings → Resources).

## The metric

`http_transactions_total{consume_type, state, product, pod, [response_time]}`

| Label | Values | Note |
|---|---|---|
| `consume_type` | 4 | api, web, mobile, batch |
| `state` | 4 | completed, pending, declined, failed |
| `product` | 10 | checking, savings, … |
| `pod` | 200 | 4 real replicas × `VIRTUAL_PODS=50`, named like real ReplicaSet pods |
| `response_time` | unbounded | e.g. `"0.023417"`, six decimals, so nearly unique per request. **The bug.** |

The baseline is 4 × 4 × 10 × 200 = **32,000 series**: already large, but stable. With `response_time` on, every request adds a series: about 300 new series per second at the default load, growing without limit.

`http_transaction_duration_seconds` is the correct way to record response time: a histogram whose labels don't include `pod` or `response_time`. It's there for comparison.

**Non-blocking:** the request handler only calls `Counter.Add` / `Histogram.Record`, which aggregate in memory. A `PeriodicExportingMetricReader` on a background thread pushes OTLP every 10s (`OTEL_METRIC_EXPORT_INTERVAL`). When Alloy is slow or down, exports fail in the background and requests are unaffected. Run `make agent-down` and watch the "App HTTP latency" panel to see this.

## Quick start

```sh
make prereqs     # brew install k3d helm
make all         # cluster + node pools, monitoring, Alloy, app
make ui          # Grafana http://localhost:3000 (admin/admin) -> Dashboards > Cardinality Repro
make load        # k6, RATE=300 req/s for DURATION=30m (make load RATE=600 to go faster)
```

## Reproducing the incident

| Step | Command | What you should see |
|---|---|---|
| 1. Baseline | `make load` | `http_transactions_total` climbs to about 32k series and flattens. There are 200 distinct `pod` values. Head series reach roughly 130k. |
| 2. Explode | `make explode` | `response_time` becomes a label. Series grow by about RATE per second. Every 10s each replica re-sends *all* its series (cumulative temporality), so the OTLP payloads, Alloy's memory and its WAL active series all grow. Prometheus head series climb toward its 1Gi limit. |
| 3. OOM | `make watch`, `make status` | Prometheus is OOMKilled. WAL replay on restart OOMs again, giving CrashLoopBackOff. Depending on the rate, Alloy can OOM first. That failure stays contained on its own node pool. |
| 4. Non-blocking | look at "App HTTP latency" | k6 p95 stays flat throughout. `make agent-down` shows the app keeps serving with no agent at all. |
| 5. "Fix" the app | `make calm` | Prometheus may *still* loop, because its WAL already holds the series. |
| 6. Recover | `make reset-prometheus` | Deleting the pod wipes the emptyDir, so the TSDB starts fresh. |

Extra knob: `make churn` replaces about 20% of the pod names every 60s, simulating rollouts. Each rollout brings a new set of `pod` values, so series churn even without `response_time`.

## Where cardinality costs memory

1. **The app** (OTel SDK): cumulative state for every series, capped per metric by `OTEL_CARDINALITY_LIMIT`. The SDK default is 2000; this repro raises it to 200,000 to match production.
2. **The OTLP payload**: re-sends all series every interval. Alloy's receiver limits are raised to 64MiB in `agent/config.alloy`. At the defaults (gRPC 4MiB, HTTP 20MiB), exports start failing first.
3. **Alloy**: the series cache in `otelcol.exporter.prometheus` (GC every 5m) and the remote_write WAL.
4. **Prometheus**: the head block. Head series are the main driver of its memory.

## Mitigations to compare

Run each while exploded (`make explode`, then `make reset-prometheus` if Prometheus is looping).

| Command | Layer | Effect |
|---|---|---|
| `make fix-app` | App, the real fix | An OTel View with a `TagKeys` allow-list (`consume_type,state,product,pod`) drops `response_time` inside the SDK. |
| `make cap-app` | App, safety net | Restores the SDK cap of 2000 series per metric. The excess goes into `otel_metric_overflow="true"`, so it is bounded but lossy. |
| `make guard-agent` | Agent | Alloy gets a `memory_limiter`, which refuses data instead of OOMing; the app's exporter just logs failures. A `prometheus.relabel` rule drops any series that carries `response_time`. Revert with `make unguard-agent`. |

Undo with `make unfix-app` / `make uncap-app` / `make calm` / `make unchurn` / `make agent-up`.

## Files

```
app/                         .NET 10 transactions-api (Program.cs, Dockerfile)
agent/config.alloy           Alloy pipeline, unguarded (production-like)
agent/config-guarded.alloy   Alloy pipeline with memory_limiter + relabel drop
agent/values.yaml            Alloy chart values: agent node pool, OTLP ports, 1Gi limit
cluster/k3d-config.yaml      k3d cluster (app pool); `make up` adds the agent pool node
monitoring/values.yaml       kube-prometheus-stack: 1Gi Prometheus limit, remote-write receiver on
monitoring/dashboards/       "Cardinality Repro" Grafana dashboard
k8s/app.yaml                 transactions-api Deployment (4 replicas, app pool) + Service
k8s/load-k6.yaml             k6 script + Job (remote-writes its latency to Prometheus)
```

## Cleanup

```sh
make down
```
