# Next steps: picking up the experiment

**Updated:** 2026-11-15, after the L5 runs, fix runs F1 and F5, [the independent review](review-2026-11-15.md), and the start of the 300-pod fleet test. Results and evidence are in [experiment-log.md](experiment-log.md). The fix plan is in [proving-the-fix.md](proving-the-fix.md).

## Where we are

- **Production pivot.** Production **already buckets response time**, so the float `response_time` label used in L1–L5 isn't the production cause. The suspects now:
  1. **A raised `CardinalityLimit` across many instruments** (run A). The SDK reserves most of the GC heap at startup. The result is `OutOfMemoryException` and slow or failed requests, with no `OOMKilled`. It needs no bad label.
  2. **Large per-pod SDK state from the pod label plus appended labels**: peer-pod or IP labels, high-resolution histograms, many instruments multiplied by extra labels.
  3. **The aggregate of all pods overwhelming the agent.** Likely to cost lost metrics rather than slower requests (see below).
- **What the repro has shown (after the review's corrections):**
  - **Pipeline damage from cardinality: every time.** 200k series per pod, 35–67 MB exports, Alloy OOMKilled 4–6× per run.
  - **Cost to the app:** clear in **heap** (about 480 MiB against 40 MiB), small in **CPU** (+1–4 points of mean throttling across pods, about 1 ms of median p95), and **intermittent stalls**: L4 1 of 2, L5 2 of 3, both L5 stalls starting at t=973 for unknown reasons.
  - **Mechanism:** the app's cost comes from the **SDK's own state**, not agent back pressure. The agent was crash-looping or idle during every stall.
  - **Fixes:** F1 (SDK allow-list View) removes everything. F5 (latency bands) is safe.
- **300-pod fleet test (running):** 300 simulated pods with production-style labels (own pod name, bucketed response time) share the agent with the 4 real replicas.
  - **Before any load:** the constrained agent refused about 29k points/s, about 55% of fleet exports failed, and **the real replicas' metrics stopped reaching Prometheus**.
  - **Prediction, written down before the run:** the app is unaffected.

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
| P300 control / fleet | 80% | running | (pair) |

Mean throttling across pods, t=420–960: control 9.8%, hypothesis runs 10.9 / 11.2 / 14.1%, F1 10.7%, F5 8.6%. Median late p95: 62.6 ms against 63.6–63.7 ms. There is only one control, so a few points is within noise.

## State things were left in

| Thing | State |
|---|---|
| k3d cluster `cardinality` | Running, with a 5th node: `k3d-fleetpool-0` (tainted `pool=fleet`, 1.75 GB, 2 CPUs) for the simulated fleet |
| Agent profile | **constrained + protect** (Alloy 250m CPU / 384 MiB, memory_limiter). Drops `response_time`/GUID series **and the simulated fleet's series** before Prometheus |
| Real replicas | 4 replicas, `CPU_WORK_ITERATIONS=1000` (≈ 2,350 req/s capacity), **`VIRTUAL_PODS=1`** (`make prod-pods`; `make lab-pods` restores 50 for the L1–L5 presets) |
| Fleet | `make fleet-on` / `make fleet-off` (2 × 150 = 300 pods). Started by the P300 pair; turn it off when done |
| Histogram buckets | Reshaped around real latencies (`10, 25, 40, 50, 60, 70, 80, 100, 250 ms, 1, 3 s`) from the fleet build on |
| Node CPUs | App nodes at **4 CPUs** (`APP_NODE_CPUS=4 make node-cpus`), not the Makefile default of 3 |
| Run history | On the driver's PVC. HTML copies are in `./reports/` (`make reports`, gitignored) |
| Git | PR #9 (fleet simulator, review additions, these docs): open, awaiting approval |
| Not yet deployed | Export-failure-reason recording, appended-labels knob, robust report statistics, churn fix. They're in PR #9; deploy with `make app` after the P300 pair finishes |

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

### 2. Which experiment next?

**Recommended order:**

1. **Finish the P300 pair** and judge it against the criteria written down before the run (experiment log, section 4b).
2. **Re-test run A with production's cardinality-limit settings.** This is the strongest remaining production candidate, and it needs no bad label.
3. **Deploy PR #9's recording**, so the next runs say *why* exports fail.
4. **Size the fleet and agent from production figures** (series per pod, Alloy limits), and add the appended labels production uses (`make appended-labels N=…`).
5. **One 40-minute L5 hypothesis plus a second L5 control,** to see whether the t=973 event recurs on a period, and to measure noise.
6. **L5 with the normal agent,** to separate agent profile from load level.

### 3. What counts as an effect?

**Bar, set before the runs:**

- **Stalls:** an outage or stall in at least 2 of 3 runs, with every matching control clean.
- **Steady-state effects:** judge these on the **mean across pods** and the **median p95**, against at least two controls. The report's worst-pod throttling and mean p95 exaggerate single-pod and short events.

### 4. Housekeeping

- Review and approve PR #9.
- Delete merged remote branches (`feature/stall-verdict-gc`).
- When the experiment is over, use `make down` to delete the cluster. `k3d cluster stop cardinality` only stops it.
