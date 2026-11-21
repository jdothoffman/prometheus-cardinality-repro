# Prometheus high-cardinality repro: .NET OTel → Grafana Alloy → Prometheus

A local reproduction of a production incident. A .NET service records each HTTP transaction with the labels `consume_type`, `state`, `product` and `pod` across about 200 pods. It also records **the response time as a float label**. The OTel SDK holds every series in memory and pushes them on a schedule to a Grafana Alloy agent on its own node pool, which forwards them to Prometheus. Series counts explode, and Prometheus and/or Alloy OOM.

```
 k6 (300 req/s) ──HTTP──▶ transactions-api ×4        ──OTLP/HTTP every 10s──▶  Alloy           ──remote_write──▶ Prometheus ──▶ Grafana
                          (app pool, 2 nodes)            (background thread)       (agent pool,                    (1Gi limit)
                          OTel SDK aggregates in memory                             tainted node)
                          50 simulated pods each = 200 pod names                    batch + WAL
 k6 ──remote_write (its own latency, bypasses Alloy)──────────────────────────────────────────────────▶ Prometheus
```

> **Status (2026-11-15):** results are in [docs/experiment-log.md](docs/experiment-log.md), with pick-up notes in [docs/next-steps.md](docs/next-steps.md) and the fix plan in [docs/proving-the-fix.md](docs/proving-the-fix.md). The production service turned out to **already bucket response time**. So the float label here is a reproduction device, not the production cause, and the suspects are now the `pod` and appended labels and the SDK cardinality limit. See the [300-pod fleet](#pod-label-test-a-300-pod-simulated-fleet) below. An [independent review](docs/review-2026-11-15.md) corrected several claims: in this repo, the app's cost comes from the SDK's own state, not from agent back pressure. A 40-minute L5 pair (three bounded runs to compare against now) found **no stall**, put the labels' steady-state CPU effect **within noise**, and explained the export failures: the OTel SDK **drops any export over 64 MiB before sending**, which silently takes the pod's own metrics out of Prometheus. A sweep of the SDK cardinality limit by itself (experiment log, section 4c) showed that **a high limit on every instrument takes the app down with bounded labels**. The SDK reserves about 4 KiB of heap per unit of limit; 200k failed through liveness-probe restarts with no `OOMKilled`, and 100k passed. Next: production's limit settings, read against that sweep.

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

Open or download a report from **Run history** in the console, or run `make reports` to copy all of them to `./reports/` (gitignored). Runs and reports are stored on a persistent volume, so they survive driver restarts. `python3 tools/report-stats.py reports/<run>.html` prints the statistics the experiment log quotes from a report's embedded data (windowed mean throttling, median p95, stalls, export failure reasons; `--minutes` adds the per-minute export timeline). The driver gets read-only access to pods, pod logs and events through its own ServiceAccount.

### Where the starvation comes from

| Resource | Constraint |
|---|---|
| App pod CPU | Limit of 1 core per replica, so the pod is throttled and requests queue (see the "Throttled" column) |
| Node CPU | App-pool nodes capped at 3 real CPUs (`make node-cpus`, applied by `make up`) |
| App pod memory | 768Mi limit. The OTel SDK holds every series in memory (cumulative), so this ends in an OOMKill and connection errors. |
| Prometheus | `make profile-oom` (default): capped at 1Gi, so it OOM-loops and the app is unaffected. `make profile-starvation`: no memory limit and scheduled on the app pool, so it becomes a noisy neighbour that takes node memory and CPU from the app. |

Run the bug test under both Prometheus profiles. `profile-oom` shows the pipeline failing while the app keeps serving. `profile-starvation` shows the original incident: the monitoring stack starving the production app.

## Experiment: does metrics back pressure cause contention with the live app?

**Hypothesis:** high-cardinality metrics, pushed to an agent that can't keep up, create back pressure. That cost lands on the app pod's CPU and heap and competes with request handling.

**What's measured, from inside the app:** every OTLP export is wrapped. The measurement covers serialize + send + retries, the points per export, and the bytes, status code and retries for each HTTP attempt. The console reads this directly every 2s, the report charts it, and Grafana shows it under Deep Dive → "6 · Export path".

**Agent profiles.** Add `PROTECT_PROM=1` to either one so Alloy drops high-cardinality series (`response_time` or GUID labels) just before remote_write. The app → Alloy back pressure is unchanged, but Prometheus doesn't crash-loop, so Grafana keeps working during the run.

| Command | Alloy | Effect |
|---|---|---|
| `make agent-normal PROTECT_PROM=1` | 1Gi, no CPU limit, no memory_limiter | Usually keeps up (it can still OOM on very large payloads) |
| `make agent-constrained PROTECT_PROM=1` | 250m CPU / 384Mi + memory_limiter (70%) | Slows down and refuses data with HTTP 503, then OOMs. Intended to make the app retry (`OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY=in_memory`). In the L4/L5 runs it was crash-looping or idle when the app stalled, so treat it as "an undersized agent" (see the review). |

Each report records **why exports failed**: `HTTP <code>`, `timeout / cancelled`, `connection: …`, or "not sent". It shows throttling both as the most-throttled pod and as the mean across pods, and p95 both as a mean and as a median.

### Hypothesis tests panel (one click per run)

The console's **Hypothesis tests** panel runs **constant-rate soaks** at a percentage of measured capacity. Load stays fixed, so anything that gets worse over time comes from growing cardinality and export cost, not from more traffic. A ramp mixes the two together.

Each button first does **Reset everything**:
1. restarts the app replicas, which clears SDK series
2. restarts Alloy, which clears its WAL and series cache
3. wipes Prometheus

Then it waits for all three to be ready, applies the labels, waits one export cycle, and starts the run with an automatic name and notes.

| Level | Load | Duration | Hypothesis labels | Agent |
|---|---|---|---|---|
| L1 | 60% of capacity | 10 min | all 5 incl. `response_time` (6 dp) | normal |
| L2 | 60% | 10 min | + 3 unique GUID labels | normal |
| L3 | 70% | 15 min | + 5 unique GUID labels | normal |
| L4 | 70% | 15 min | + 5 unique GUID labels | **constrained** |
| L5 | 80% | 20 min | + 5 unique GUID labels | **constrained** |

- Run **Run control (bounded)** and **Run hypothesis** at the same level, then compare them in Run history.
- The panel shows the agent each level needs, and the command to switch to it.
- **Capacity** defaults to 2,350 req/s, the knee measured with `CPU_WORK_ITERATIONS=1000` (≈1.7–2.4 ms CPU per request, 4 × 1-core pods). Each report's "Request-only capacity" finding re-measures it. Update the field if you change the CPU work, which is set in `k8s/app.yaml`, not with `kubectl set env`, because `make app` re-applies the manifest.
- **If a level passes, move to the next.** The app staying healthy, or a PARTIAL / NOT CONFIRMED verdict, both count as passing.

**Extra GUID labels** (console label panel, 0–5): `request_id`, `trace_id`, `session_id`, `correlation_id`, `tenant_id`.
- **Values:** unique per request, or a pool of 10 / 100 / 1,000 / 10,000 random GUIDs. Changing the setting regenerates them.
- **Why they add pressure:** each one adds about 40 bytes to *every* series (3 labels roughly doubled the bytes per point), so they make each export bigger and costlier to serialize, independently of series count.

**Verdict.** Each report includes a **Metrics back pressure** finding.
- **Soaks** compare the first and last quarter at constant load: CPU, throttling, export cost, p95 and failures. **CONFIRMED** needs all three of these to rise, with CPU not already near the limit early on:
  - latency (p95 doubles, or failures appear)
  - export cost
  - CPU or throttling
- **Ramps** need all of the following for **CONFIRMED**:
  - degradation
  - export pressure (at least 50% of the 8s timeout, or retries)
  - CPU contention
  - export pressure that **started before** the degradation
  - request CPU alone, projected from the low-load part of the run, **below the limit** at the degradation point
- Otherwise the report says **Load saturation** (requests alone hit the CPU limit) or **PARTIAL**, with the reason.

**Cardinality limit scope:** `OTEL_CARDINALITY_LIMIT_SCOPE=transactions` (the default) applies the 200k limit only to `http_transactions_total`. With `all`, the SDK pre-allocates storage for every instrument: about 560 MiB, or 97% of the GC heap limit. The app then dies of `OutOfMemoryException` before any back pressure can build. That's a separate finding.

## Pod-label test: a 300-pod simulated fleet

Production pods each stamp only their **own** pod name, so no single pod sends much. This test checks whether the *aggregate* overwhelms the agent and affects the live app.

```sh
make fleet-node      # once: tainted node pool=fleet (1.75 GB, 2 CPUs) for the simulated pods
make prod-pods       # real replicas: own pod name only (VIRTUAL_PODS=1); make lab-pods restores 50
make fleet-on        # 2 x 150 = 300 simulated pods (APP_ROLE=fleet, app/Fleet.cs); make fleet-off stops them
make appended-labels N=4   # optional: 0-8 identity labels (k8s node, pod uid, container id, ...) on every series
```

- **Per simulated pod:** its own OTel MeterProvider, resource and OTLP export every 10s, at its own phase.
- **Labels:** production-style (own `pod`, `response_time` as 5 bands), plus 100 extra series for the pod's other metrics. Knobs are in `k8s/fleet.yaml`: `FLEET_PODS`, `FLEET_RATE_PER_POD`, `FLEET_RESPONSE_TIME`, `FLEET_EXTRA_SERIES`, `FLEET_CHURN_SECONDS` and `FLEET_APPENDED_LABELS`.
- **Prometheus:** the `PROTECT_PROM` configs drop fleet series before Prometheus. Alloy still receives and converts them.
- **Control matching:** a fleet run's report compares against the same labels without the fleet.

## Finding the way back: step labels down

Once the failure reproduces, use the label controls in the console to reduce cardinality one step at a time. Re-run the same ramp after each step and compare in Run history:

1. `response_time` precision 6 dp → 3 dp (≈1ms buckets) → 2 dp (≈10ms) → 1 dp (≈100ms) → **band** (5 fixed values) → off. The right fix is the `http_transaction_duration_seconds` histogram, which is already recorded. Its buckets are shaped around this app's latencies (10–100 ms in 10–15 ms steps).
2. Then `pod` (about 200 values): drop it, or rely on the resource's `instance`.
3. Then `product`, `state`, `consume_type`, if needed.

Click **Apply labels**, then **Reset everything** (or **Restart replicas**) before each run. With cumulative temporality, removing a label doesn't free the series the SDK already holds; only a restart does.

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
app/                         .NET 10 image: Api.cs (transactions-api), Driver.cs + wwwroot/ (load-test console), Report.cs (run reports), Fleet.cs (simulated pods)
agent/config.alloy           Alloy pipeline, unguarded (production-like)
agent/config-guarded.alloy   Alloy pipeline with memory_limiter + relabel drop
agent/config-constrained.alloy + values-constrained.yaml   "constrained" agent profile (back pressure)
agent/config*-protect.alloy  same pipelines + drop high-cardinality series before Prometheus (PROTECT_PROM=1)
agent/values.yaml            Alloy chart values: agent node pool, OTLP ports, 1Gi limit
cluster/k3d-config.yaml      k3d cluster (app pool); `make up` adds the agent pool node
monitoring/values.yaml       kube-prometheus-stack: 1Gi Prometheus limit, remote-write receiver on
monitoring/values-starvation.yaml  overlay: Prometheus unlimited, on the app pool (noisy neighbour)
monitoring/dashboards/       Grafana dashboards: "Cardinality Repro" (overview), "Cardinality Deep Dive" (5 sections: flow, cardinality, Prometheus, app, Alloy)
k8s/app.yaml                 transactions-api (4 replicas, app pool), headless Service, transactions-driver
k8s/fleet.yaml               transactions-fleet: simulated pods on the fleet node (make fleet-on)
k8s/load-k6.yaml             k6 script + Job (remote-writes its latency to Prometheus)
docs/                        experiment-log.md (results), next-steps.md (pick-up notes), proving-the-fix.md, review-2026-11-15.md
```

## Cleanup

```sh
make down
```

## License

[MIT](LICENSE)
