# Next steps: picking up the experiment

**Updated:** 2026-11-15 (evening), after the L5 runs, fix runs F1 and F5, [the independent review](review-2026-11-15.md), the 300-pod fleet test and the 40-minute L5 pair. Results and evidence are in [experiment-log.md](experiment-log.md). The fix plan is in [proving-the-fix.md](proving-the-fix.md).

## Where we are

- **Production pivot.** Production **already buckets response time**, so the float `response_time` label used in L1–L5 isn't the production cause. The suspects now:
  1. **A raised `CardinalityLimit` across many instruments** (run A). The SDK reserves most of the GC heap at startup. The result is `OutOfMemoryException` and slow or failed requests, with no `OOMKilled`. It needs no bad label.
  2. **Large per-pod SDK state from the pod label plus appended labels**: peer-pod or IP labels, high-resolution histograms, many instruments multiplied by extra labels.
  3. **The aggregate of all pods overwhelming the agent.** Likely to cost lost metrics rather than slower requests (see below).
- **What the repro has shown (after the review's corrections):**
  - **Pipeline damage from cardinality: every time.** 200k series per pod, 35–67 MB exports, Alloy OOMKilled 4–6× per run.
  - **Cost to the app:** clear in **heap** (about 480 MiB against 40 MiB); **CPU within noise** (+1–4 points of mean throttling, inside the 3–7 point spread of three bounded runs); **median p95 at most 1 ms higher**; and **intermittent stalls**: L4 1 of 2, L5 2 of 4, both L5 stalls starting at t=973 for unknown reasons, none in the 40-minute run.
  - **Mechanism:** the app's cost comes from the **SDK's own state**, not agent back pressure. The agent was crash-looping or idle during every stall. From minute 6 of every high-cardinality run the SDK **dropped each export before sending** (payload over the OTLP exporter's 64 MiB `MaxRequestSizeBytes`), which also took the pod's own runtime metrics out of Prometheus.
  - **Fixes:** F1 (SDK allow-list View) removes everything. F5 (latency bands) is safe.
- **300-pod fleet test: done. Agent overload alone does not slow the app.** 300 simulated pods (own pod name, bucketed response time) shared the constrained agent with the 4 real replicas.
  - **Agent:** refused up to 97k points/s, OOMKilled 6×. 98% of fleet exports failed. The real replicas' metrics were dark for about 18 of 20 minutes.
  - **App:** 0 failures, heap 35 MiB, throttling and p95 at control level, even though its own exports failed and retried the whole run (1,204 retries at the 8 s timeout).
  - **Conclusion:** the cost of cardinality to the app is the size of the SDK's own state. Agent overload costs visibility, not latency. Retries multiplied the agent's inbound load about 5×.
  - The prediction written down before the run held.
- **40-minute L5 pair: done. No stall, and the export failures are explained.** A 40-minute control and a 40-minute hypothesis run (200k series, 480 MiB heap per pod), scored against criteria written before the runs:
  - **t=973 did not recur.** Neither run had a second above 100 ms after warm-up. Stalls stand at 2 of 4 hypothesis runs (peaks 1,340 and 497 ms); no bounded run went above 236 ms. Trigger still unknown, not periodic.
  - **Noise floor:** three bounded runs spread 3 points of mean throttling (t=420–960) and 7 points (t=1030–1200). The hypothesis runs sit inside it. Median p95: at most 1 ms apart.
  - **Why exports fail:** 844 of 992 failed attempts were "not sent". The OTel .NET OTLP exporter drops any export over **64 MiB** (`OtlpExporterOptions.MaxRequestSizeBytes`, exporter 1.18+) before any HTTP request. Each replica crossed it in minute 6 and then dropped every export, runtime metrics included: **dark in Prometheus for 34 minutes** while Alloy sat idle at 85 MiB.

## Scorecard

| Run | Load | Result | Control |
|---|---|---|---|
| L4 | 70% | 10 s outage, 2,205 timeouts (Alloy crash-looping at the time) | clean |
| L4 repeat | 70% | no degradation | clean (some noise) |
| L5 | 80% | stall from t=973 (worst p95 1,340 ms, 0 errors) | clean |
| L5 repeat 1 | 80% | stall from t=973 (worst p95 497 ms) | (same control) |
| L5 repeat 2 | 80% | no stall | (same control) |
| **L5 F1 fix** | 80% | same bad labels, dropped by the SDK View: **everything at control level** | (same control) |
| **L5 F5 band** | 80% | `response_time` as 5 bands: **healthy** | (same control) |
| P300 control (no fleet) | 80% | clean (worst p95 236 ms, 0 errors) | — |
| **P300 fleet** (300 pods) | 80% | **no app effect** (0 errors, heap 35 MiB, p95 at control); agent refused up to 97k pts/s, OOMKilled 6×; 98% of fleet exports lost | clean |
| L5 40-min control | 80% | clean (worst p95 100 ms, 0 errors) | — |
| **L5 40-min hypothesis** | 80% | **no stall in 40 min** (worst p95 77 ms, 0 errors); heap 480 MiB; every export dropped by the SDK from minute 6 (payload > 64 MiB); replicas dark in Prometheus for 34 min; Alloy OOMKilled 5× then idle | clean |

Mean throttling across pods, t=420–960: bounded runs 9.8 / 9.3 / 12.6% (L5 control, 40-min control, P300 control), hypothesis runs 10.9 / 11.1 / 14.1 / 10.8%, F1 10.7%, F5 8.6%. Median p95, t=1030–1200: bounded 62.8 / 62.6 / 63.0 ms, hypothesis 63.3 / 63.6 / 63.4 / 62.9 ms. With three bounded runs the noise floor is about 3 points of throttling and 0.5 ms of p95, so the hypothesis runs' CPU difference is within noise and the p95 difference is at most about 1 ms.

## State things were left in

| Thing | State |
|---|---|
| k3d cluster `cardinality` | Running, with a 5th node: `k3d-fleetpool-0` (tainted `pool=fleet`, 1.75 GB, 2 CPUs) for the simulated fleet |
| Agent profile | **constrained + protect** (Alloy 250m CPU / 384 MiB, memory_limiter). Drops `response_time`/GUID series **and the simulated fleet's series** before Prometheus |
| Real replicas | 4 replicas, `CPU_WORK_ITERATIONS=1000` (≈ 2,350 req/s capacity), **`VIRTUAL_PODS=50`** again: `make app` re-applies the manifest, which resets `make prod-pods`. Run `make prod-pods` before the next fleet test |
| Fleet | `make fleet-on` / `make fleet-off` (2 × 150 = 300 pods). **Off** after the P300 pair |
| Histogram buckets | Reshaped around real latencies (`10, 25, 40, 50, 60, 70, 80, 100, 250 ms, 1, 3 s`) from the fleet build on |
| Node CPUs | App nodes at **4 CPUs** (`APP_NODE_CPUS=4 make node-cpus`), not the Makefile default of 3 |
| Run history | On the driver's PVC. HTML copies are in `./reports/` (`make reports`, gitignored) |
| Git | PR #9 merged. PR #10 (this branch): the P300 results and the 40-minute pair |
| Deployed build | `main` after PR #9: export-failure-reason recording, appended-labels knob, robust report statistics, churn fix (deployed 2026-11-15 15:16 UTC and verified: `/admin/stats` now has `exportFailureReasons` and `appendedLabels`). The first `make app` attempt failed silently in `k3d image import`; running it again worked |
| Alloy | `restartCount` 5 since the 40-min hypothesis run (OOMKilled during its first 4 minutes, idle since). "Reset everything" restarts it before the next run |
| Real replicas' SDK state | Still holding the 40-min hypothesis run's 200k series and 480 MiB heap each, and dropping every export (payload > 64 MiB). "Reset everything" (part of every prepared run) restarts them |

## Resume

```sh
cd ~/repos/grafanPromPoc
k3d cluster start cardinality                     # if it was stopped
kubectl --context k3d-cardinality get pods -A     # wait until everything is Running
make ui                                           # console :8080, Grafana :3000, Prometheus :9090
```

If the fleet node's CPU cap was lost: `docker update --cpus 2 k3d-fleetpool-0`. If the app nodes' caps were lost: `APP_NODE_CPUS=4 make node-cpus`.

## Decisions needed

### 1. Get the production facts (blocks the sizing of every next run)

Ask the observability team, or check the code and config:

- [ ] **`CardinalityLimit` settings:** `SetCardinalityLimit`, `SetMaxMetricPointsPerMetricStream`, or Views with `CardinalityLimit`. For which instruments, and what value?
- [ ] **Series per pod:** e.g. `count({instance="<one pod>"})` across all metrics.
- [ ] **How response time is bucketed:** a histogram (how many buckets?), a band label, or rounding to N decimals?
- [ ] **Which labels are appended, and where:** SDK resource attributes, an Alloy processor such as `k8sattributes`, or relabel rules. Are resource attributes promoted to metric labels?
- [ ] **Alloy:** replicas, CPU and memory limits, and `memory_limiter` settings. Are there OOMKills or refused points (`otelcol_receiver_refused_metric_points_total`)?
- [ ] **OTel retry:** is `OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY` set? What's the export interval?
- [ ] **Heap against the GC limit** on app pods (75% of the memory limit), and restarts with exit `Error` (not `OOMKilled`).
- [ ] **OTLP exporter package version and request limit:** `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.18 and later drop any export over 64 MiB (`OtlpExporterOptions.MaxRequestSizeBytes`) before sending. Which version is deployed, and is the limit set?
- [ ] **Pods whose series went stale while the pod kept running** during the incident. That is the signature of the drop (experiment log, insight 15): the pod's own heap and GC metrics vanish with the rest.

### 2. Which experiment next?

**Recommended order:**

1. ~~Finish the P300 pair~~ **Done** (experiment log, section 4b): no app effect.
2. **Run-A sweep: the cardinality limit by itself.** The strongest remaining production candidate, it needs no bad label, and this version needs no production facts (about 1 hour of cluster time). Run A showed that a limit of 200k on every instrument reserves about 560 MiB at startup. The sweep maps the whole curve so production's value can be read off it once known.
   - **Settings:** bounded labels, constrained + protect agent, 4 replicas, `VIRTUAL_PODS=50`. `OTEL_CARDINALITY_LIMIT_SCOPE=all` with `OTEL_CARDINALITY_LIMIT` at **2,000 (the SDK default), 10,000, 50,000, 100,000 and 200,000**: `kubectl --context k3d-cardinality -n demo set env deploy/transactions-api OTEL_CARDINALITY_LIMIT=<n> OTEL_CARDINALITY_LIMIT_SCOPE=all`, then a prepared run (which restarts the pods). Restore with `OTEL_CARDINALITY_LIMIT=200000 OTEL_CARDINALITY_LIMIT_SCOPE=transactions` afterwards.
   - **Measure:** the idle heap after startup (direct polls, `/admin/stats`) against the 576 MiB GC limit; then a 10-minute soak at 1,900 req/s per value, with the usual report. The controls are the existing bounded runs (scope `transactions`, idle heap 26 MiB).
   - **Criteria, to be written into the log before the first run:** a value *fails* if its soak has client errors, a stall (5 s or more over 100 ms) or pod restarts with exit `Error`; otherwise it *costs* the idle heap it reserves. Expected: heap grows linearly with the limit (run A: 560 MiB ÷ 200k ≈ 2.9 KB per unit of limit across the ~30 instruments), and the app fails somewhere between 100k and 200k.
   - **Output:** a table *limit → idle heap, soak result*. Once production's value is known, it is read off the table, and the exact re-test with production's settings follows.
3. ~~Deploy PR #9's recording~~ **Done.** The next runs say *why* exports fail.
4. **Size the fleet and agent from production figures** (series per pod, Alloy limits), and add the appended labels production uses (`make appended-labels N=…`).
5. ~~One 40-minute L5 hypothesis plus a second L5 control~~ **Done:** no stall, noise floor measured, export failures explained (experiment log, section 4, "L5 × 40 min").
6. **L5 with the normal agent,** to separate agent profile from load level. Lower priority: the stall question is parked until a stall recurs, and when one does, the report needs node-level CPU and pressure metrics, which it doesn't record yet.

### 3. What counts as an effect?

**Bar, set before the runs:**

- **Stalls:** an outage or stall in at least 2 of 3 runs, with every matching control clean.
- **Measured noise floor (2026-11-15):** three bounded runs at 1,900 req/s spread 3 points of mean throttling at t=420–960 and 7 points at t=1030–1200; their median p95 spans 0.5 ms. Anything smaller is noise.
- **Steady-state effects:** judge these on the **mean across pods** and the **median p95**, against at least two controls. The report's worst-pod throttling and mean p95 exaggerate single-pod and short events.

### 4. Housekeeping

- Merge PR #10.
- Delete the merged branches, local and remote: `feature/fleet-sim`, `feature/latency-band`, `feature/stall-verdict-gc`, `features/docsupdate`.
- Report tooling: take "App heap (peak)" from the direct polls, not Prometheus (it showed 56 MiB against 480 MiB once the pod's exports were being dropped). Add node-level CPU and pressure metrics to the report, for the next stall.
- When the experiment is over, use `make down` to delete the cluster. `k3d cluster stop cardinality` only stops it.
