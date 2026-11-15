# Proving the fix: response-time metrics that scale

**Status:** a candidate fix and the plan to test it. Run this *after* the hypothesis is shown reliably (see [next-steps.md](next-steps.md), decision 3). The results so far are in [experiment-log.md](experiment-log.md).

## The problem in one line

`response_time` as a float label makes nearly every request a new time series. At 6 decimal places it behaves like a request ID. Pipeline cost then grows with **request volume** instead of staying fixed.

## How precision multiplies series

Each extra decimal place gives about 10× more distinct values. Every distinct value is a new series for every combination of the other labels. The bounded labels in this repo (4 consume types × 4 states × 10 products × 50 pods per replica) give about 8,000 series per replica before `response_time` is added.

| Precision | Example | Distinct values, normal (~5–65 ms) | Distinct values, degraded (up to the 3 s timeout) | Series per replica |
|---|---|---|---|---|
| 6 dp (current) | `0.023817` | ~60,000, nearly every request unique | ~3,000,000 | grows until the SDK cap (200k) |
| 3 dp | `0.024` | ~60 | ~3,000 | up to ~480k, over the cap |
| 2 dp | `0.02` | ~7 | ~300 | up to ~56k |
| 1 dp | `0.0` | 1–2 | ~30 | ~8–16k |
| off | (none) | 1 | 1 | ~8k |

**Two things make it worse:**

- **Cardinality rises during an incident.** When latency spreads into the tail, new values appear. Series counts jump exactly when the system is already stressed.
- **Series are never forgotten.** With cumulative temporality, every series the SDK has seen stays in memory and is re-sent every 10s until the pod restarts.

Rounding reduces the damage. It doesn't remove it, because a rounded number still multiplies every pod, product and state.

## The fix: track response time without overwhelming the agent

**Principle:** a metric's cost depends on the number of distinct label combinations, never on the number of requests. A histogram of a billion requests costs the same as one of ten.

Use three layers, one per question:

| Question | Tool | Cost grows with |
|---|---|---|
| p95/p99 latency by product, now and over time | **Metrics:** a histogram with bounded labels | label combinations (fixed) |
| Why was *this* request slow? | **Traces**, with tail sampling that keeps slow and error requests | sampled requests |
| Exact latency of transaction X (audit, support) | **Logs** or a structured event store | requests, outside the metrics pipeline |

### 1. A histogram, never a label

Each request adds 1 to a bucket counter, so the series count is fixed. Percentiles are computed at query time:

```promql
histogram_quantile(0.95,
  sum by (le, product) (rate(http_transaction_duration_seconds_bucket[5m])))
```

In .NET, choose the buckets and an allow-list of labels in one View. The SDK is where aggregation happens, so labels dropped here are merged correctly:

```csharp
.AddView("http.transaction.duration", new ExplicitBucketHistogramConfiguration
{
    Boundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5],
    TagKeys    = ["consume_type", "state", "product"],   // allow-list: anything else is dropped
})
```

- ASP.NET Core instrumentation already emits `http.server.request.duration` as a histogram.
- This repo already records `http.transaction.duration` alongside the bad label.
- For higher resolution at the same cost, use exponential histograms (`Base2ExponentialBucketHistogramConfiguration`). That is one series per label combination, stored by Prometheus as native histograms. Native histograms have to be enabled on Prometheus and on remote write.
- If someone needs latency *as a label*, use a band label with about 5 values instead of a number. This repo has one: `response_time` precision **band** gives `0-50ms`, `50-250ms`, `250ms-1s`, `1-3s` and `3s+` (run F5).

### 2. A cardinality budget per metric

- **Label values must come from a small, known set.** Product, state, region and route template are fine. IDs, raw numbers, full URLs and timestamps are never fine.
- **Do the math before adding a label.** The production shape:
  - 4 × 4 × 10 = 160 combinations
  - × about 12 series per histogram (10 buckets + sum + count)
  - ≈ 2,000 series per pod
  - × 150 pods ≈ 290k series
  
  That's manageable. Exponential histograms bring it to about 24k. A 6-dp `response_time` label is unbounded.
- **Per-pod latency is rarely needed.** Keep pod identity on a few health metrics, and aggregate it away on latency histograms, either in the SDK or with Prometheus recording rules.

### 3. Traces and exemplars for individual requests

- Traces hold the exact duration of each request. Tail sampling in Alloy or the OTel Collector keeps all slow and error traces plus a small share of normal ones.
- Exemplars link a histogram bucket to a sample trace ID. In Grafana you can click a p99 spike and open a real slow request.

### 4. Guardrails at every hop

These turn one bad label into a dashboard problem instead of an outage.

- **SDK:**
  - Keep `CardinalityLimit` low. The default of 2,000 is a protection: overflow goes to `otel_metric_overflow` instead of exploding. Production has almost certainly raised it; check.
  - Use the allow-list View from section 1.
- **Agent:**
  - Use a `memory_limiter`.
  - Drop known-bad series before remote write, as [config-protect.alloy](../agent/config-protect.alloy) does. This is a safety net only: the agent can drop whole series, but only the SDK can merge them correctly once a label is removed.
- **Prometheus:**
  - Set per-target `sample_limit` and `label_limit`.
  - Alert on `prometheus_tsdb_head_series` growth and on `otel_metric_overflow` appearing.
- **Process:** review new metric labels like database indexes. Whoever adds a label owns its cardinality, including labels the observability team appends.

## How to prove it in this repo

**Goal:** show that the fix brings a run that is known to be bad back to control-level behavior. Do this at the **same level that reproduced the hypothesis**, with the same rate, duration and agent profile.

### Fix runs

Each run is the hypothesis preset at that level, plus a fix knob. All knobs already exist.

| Run | What it proves | How |
|---|---|---|
| **F0. Label removed** | The app is healthy without `response_time` and the GUIDs. This is the control run, which already exists for every level. | Run control |
| **F1. Allow-list View** (the real fix) | Even if code *still adds* the bad labels, an SDK View that keeps only bounded tags brings everything back to normal. This is the guard against labels appended later. | `make fix-app`, then Run hypothesis. Undo with `make unfix-app`. |
| **F2. SDK default cap** (safety net) | With the cap at 2,000, the bad labels overflow into `otel_metric_overflow` instead of reaching the pipeline. Data is lost, but the app is protected. | `make cap-app`, then Run hypothesis. Undo with `make uncap-app`. |
| **F3. Precision step-down** (partial fix) | Where rounding stops hurting: 6 → 3 → 2 → 1 dp → off, then GUIDs 5 → 0. | Hypothesis preset, with `response_time` precision and GUID count set in the console |
| **F5. Latency band label** (the label-shaped fix) | Teams that want latency *as a label* can have one safely: `response_time` with 5 fixed values (`0-50ms`, `50-250ms`, `250ms-1s`, `1-3s`, `3s+`) instead of a float. Series are at most 5× control, however latency spreads. | Hypothesis preset with `response_time` precision set to **band (5 fixed values)** in the console (precision `-1` via the API) |
| **F4. Agent-side drop only** (shows its limits) | `PROTECT_PROM=1` protects Prometheus. It does **not** protect the app or Alloy, because the app still builds and sends the full payload. This is the agent profile already used in L4 and L5. | `make agent-constrained PROTECT_PROM=1` |

**Caveats:**

- `make fix-app` and `make cap-app` set env vars with `kubectl set env`. A later `make app` re-applies the manifest and **removes** them. Re-run the knob after any `make app`.
- The console's **Reset everything** step does a rollout restart, which keeps those env vars.

### What counts as fixed

Compare against the hypothesis run at the same level and against its control. The fix passes when all of these hold:

| Signal | Hypothesis (L5, 3 runs) | Fixed means | **F1 result** |
|---|---|---|---|
| Export payload per replica | 66–67 MB, 200k points | at control level, ≈1.2 MB / 8k points | ✅ 1.2 MB / 8.2k |
| Export failures / retries | 452–455 / 45–68 | 0 / 0 | ✅ 0 / 0 |
| Alloy | refusing 10.6–11.2k points/s, 0–5 restarts | no restarts, 0 refused | ✅ healthy |
| App .NET heap (peak) | 477–482 MiB of 576 MiB | at control level, ≈40 MiB | ✅ 41 MiB |
| Pod throttling, late quarter | 42–51% | within a few points of control (33%) | ✅ 31% |
| p95, late quarter | 64–87 ms | within a few ms of control (63 ms) | ✅ 63 ms |
| Stalls / client errors | 1 stall (p95 1,340 ms), 0 errors | none | ✅ none |

**F5 passed** (run `20261115-134526`):

- **What the app added:** `response_time` as 5 fixed bands, with no GUID labels.
- **Export:** 15.8k points / 2.8 MB, about 2× control, because only the `0-50ms` and `50-250ms` bands occur at normal latency. There were 0 export failures, and Alloy was healthy with brief refusals of up to 882 points/s.
- **App:** heap 49 MiB, throttling 30 → 21%, p95 63 ms, 0 client failures.
- **Caveat:** `PROTECT_PROM` drops any series with a `response_time` label, so the Prometheus cost of the band series wasn't measured here. By the arithmetic it's about 2× the bounded series, and at most 5× under heavy degradation.

**F1 passed on every signal** (run `20261115-132242`, 2026-11-15). The code still added `response_time`(6dp) and 5 unique GUID labels; the SDK View (`OTEL_ALLOWED_TAGS=consume_type,state,product,pod`) dropped them before export.

### Histogram accuracy: buckets matter

In F1, the server-side histogram's p95 read **89 ms**, while the true client-side p95 was **63 ms** (p50: 34 ms against 37 ms, which is fine). The current boundaries are `5, 10, 25, 50, 100, 250 ms…`. 22% of requests sit in the single 50–100 ms bucket, and interpolation inside it overestimates p95 and p99.

- **Lesson:** a histogram keeps the latency signal at fixed cost, but **place the boundaries where your percentiles and SLOs are**. For example: `25, 50, 60, 75, 100, 150, 250, 500 ms, 1, 2.5, 5 s`.
- **Alternative:** use exponential histograms, which adapt their resolution automatically.
- **Stability:** the histogram was rock-steady (88.3–88.8 ms all run), so even coarse buckets work for trends and alerting. They're just imprecise in absolute terms.
- **Suggested follow-up:** re-run F1 with SLO-shaped buckets and check that the p95 lands within a few ms of the driver's.

For F3, record each step's numbers. The first precision at which every signal is at control level is the "safe" setting, and the answer to "which labels can we keep?"

### Production rollout check

Once the fix is chosen, verify it in production with the same signals:

- the OTLP export size and failures per pod
- agent memory, restarts and refused points
- `prometheus_tsdb_head_series`
- app p95 and CPU throttling

Compare each against a pre-fix baseline window at similar traffic.
