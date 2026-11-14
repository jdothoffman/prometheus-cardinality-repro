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
make ui          # Grafana http://localhost:3000 (admin/admin) -> Dashboards > Cardinality Repro / Cardinality Deep Dive
make load        # k6, RATE=300 req/s for DURATION=30m (make load RATE=600 to go faster)
```

## Load-test console (Start button)

`make ui`, then open **http://localhost:8080**. The console is the same .NET image running with `APP_ROLE=driver`, as its own one-replica Deployment on the control-plane node. That way, generating load never takes CPU from the replicas under test.

1. **Dry run (safe).** Click this first. It runs for about 60s at 20→100 req/s with labels locked to bounded, and stops itself at 5% errors. Then it checks every hop and shows a checklist:
   - driver → API requests succeed, with healthy latency
   - all replicas are reachable, with bounded labels
   - Prometheus is up, with memory headroom
   - Alloy is receiving OTLP, with no remote_write failures
   - `http_transactions_total` is fresh in Prometheus
   - the Grafana dashboard exists

   Nothing is pushed toward a crash.
2. **Baseline run:** set labels to **Bounded** and click **Start test**. The rate steps up (default: 50 req/s, +50 every 30s, up to 2000) until errors reach the auto-stop threshold or the max rate is reached. This finds the system's real capacity.
3. **Bug run:** switch labels to **+ response_time label** and click **Start test** again with the same ramp. Compare the two runs in **Run history**: the rate at which p95 crossed the degradation threshold, at which the first 3s timeout happened, and at which errors reached 5% and 50%.

How it works:
- **Load model:** open-model arrival rate. Requests keep coming at the target rate however slow the system gets, so in-flight requests pile up, latency climbs to the timeout, and then errors take over.
- **Timeout:** 3s per request on the client side, configurable. Errors are counted as timeouts, HTTP 5xx, connection errors (refused or reset, e.g. a pod that was OOMKilled), or "dropped", when the driver hit its in-flight cap.
- **Business outcomes aren't errors.** Completed, pending, declined and failed are all HTTP 200, so every error you see comes from overload.
- **What the console shows:** throughput, client-side p50/p95/p99, failures by type, and per-replica memory and CPU throttling. Replica figures are read directly from each pod's cgroup through a headless Service, so they keep working when Prometheus is down. It also shows Prometheus and Alloy memory, plus node memory.
- **Labels stay applied across restarts.** The label toggle reaches every replica without a restart, and is re-applied to replicas that come back after an OOMKill.

### Test names and reports

Give each run a **name**, and optionally **notes**, in the console before you click Start or Dry run. When a run ends, the driver waits about 25s for the metrics pipeline to catch up. Then it collects evidence while it's still fresh, because Kubernetes events expire after about an hour and a crashed container's logs are lost on its next restart. The evidence is saved as a **self-contained HTML report**:

- **Most likely cause** and a list of **findings**, from rule-based analysis. It covers:
  - the client failure sequence
  - pod terminations (OOMKilled, or killed by the liveness probe)
  - .NET `OutOfMemoryException` hitting the GC heap hard limit
  - GC heap headroom before the test
  - CPU pinned at the limit
  - Prometheus and Alloy health
  - the cardinality reached
- Degradation milestones, ramp steps, configuration, and dry-run checks
- Charts: client throughput, latency, failures and in-flight requests, plus from Prometheus:
  - CPU per pod
  - **.NET GC heap against the GC heap limit**
  - container memory and restarts
  - Prometheus and Alloy memory
  - series counts
  - Alloy OTLP in and remote_write failures
- Container terminations, Kubernetes warning events, and **exception lines and the fatal stack from crashed containers' logs**

Open or download a report from **Run history** in the console, or run `make reports` to copy all of them to `./reports/` (gitignored). Runs and reports are stored on a persistent volume, so they survive driver restarts. The driver gets read-only access to pods, pod logs and events through its own ServiceAccount.

### Where the starvation comes from

| Resource | Constraint |
|---|---|
| App pod CPU | Limit of 1 core per replica, so the pod is throttled and requests queue (see the "Throttled" column) |
| Node CPU | App-pool nodes capped at 3 real CPUs (`make node-cpus`, applied by `make up`) |
| App pod memory | 768Mi limit. The OTel SDK holds every series in memory (cumulative), so this ends in an OOMKill and connection errors. |
| Prometheus | `make profile-oom` (default): capped at 1Gi, so it OOM-loops and the app is unaffected. `make profile-starvation`: no memory limit and scheduled on the app pool, so it becomes a noisy neighbour that takes node memory and CPU from the app. |

Run the bug test under both Prometheus profiles. `profile-oom` shows the pipeline failing while the app keeps serving. `profile-starvation` shows the original incident: the monitoring stack starving the production app.

## Reproducing the incident with make targets

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
app/                         .NET 10 image: Api.cs (transactions-api), Driver.cs + wwwroot/ (load-test console), Report.cs (run reports)
agent/config.alloy           Alloy pipeline, unguarded (production-like)
agent/config-guarded.alloy   Alloy pipeline with memory_limiter + relabel drop
agent/values.yaml            Alloy chart values: agent node pool, OTLP ports, 1Gi limit
cluster/k3d-config.yaml      k3d cluster (app pool); `make up` adds the agent pool node
monitoring/values.yaml       kube-prometheus-stack: 1Gi Prometheus limit, remote-write receiver on
monitoring/values-starvation.yaml  overlay: Prometheus unlimited, on the app pool (noisy neighbour)
monitoring/dashboards/       Grafana dashboards: "Cardinality Repro" (overview), "Cardinality Deep Dive" (5 sections: flow, cardinality, Prometheus, app, Alloy)
k8s/app.yaml                 transactions-api (4 replicas, app pool), headless Service, transactions-driver
k8s/load-k6.yaml             k6 script + Job (remote-writes its latency to Prometheus)
```

## Cleanup

```sh
make down
```

## License

[MIT](LICENSE)
