# Experiment log: metrics pressure vs the live app

**Date:** 2026-11-14 · **Environment:** local k3d cluster (this repo) · **Status (revised 2026-11-15 after [an independent review](review-2026-11-15.md)):** L1–L5 complete, L4 ×2, L5 ×3, fix runs F1 and F5 done, and a 300-pod fleet test done. **Production already buckets response time**, so the float `response_time` label is not the production cause. The suspect is now the pod and appended labels (section 4b). **In this repo the app-side cost comes from each pod's own SDK state (about 200k series), not from agent back pressure. It is small in steady state, with latency stalls in 2 of 5 constrained-agent runs. F1 removes it.**

## TL;DR

- **300-pod fleet test (section 4b): agent overload alone does not slow the app.** With 300 simulated pods sharing the constrained agent, Alloy refused up to 97k points/s and OOMKilled 6×, 98% of the fleet's exports failed, and the real replicas' metrics went dark for about 18 of 20 minutes. The real replicas' own exports failed and retried for the whole run (1,204 retries, 8 s timeouts). The app: 0 failures, heap 35 MiB, throttling and p95 at control level. **The cost of cardinality to the app is the size of the SDK's own state, not the back-pressure path.** Agent overload costs visibility, not latency.
- **Production pivot (2026-11-15).** Production already buckets response time. The float `response_time` label studied in L1–L5 is therefore *not* the production cause, although it remains a clear way to reproduce high-cardinality damage. The remaining suspects:
  - the `pod` label and the labels the observability team appends
  - a raised `CardinalityLimit` across many instruments (run A)
  - the aggregate of many pods overwhelming the agent
  
  The 300-pod fleet test is in section 4b.
- **Cardinality → damage to the metrics pipeline: reproduced at every level (L1–L5).**
  - With `response_time` as a float label, every replica reaches the OTel SDK cap (200k series) and builds **35–67 MB exports every 10s**.
  - Exports time out or fail.
  - **Alloy was OOMKilled 4–6 times per run**, on both the normal (1 GiB) and constrained profiles.
- **Cost to the app: real, but small in steady state.**
  - **Heap:** 477–488 MiB (83–85% of the GC limit), against about 40 MiB in controls.
  - **Throttling (mean across pods):** about 10% in the control, against 11–14% in the hypothesis runs before t=960.
  - **Per-pod CPU:** within 1–2% of control.
  - **Median p95:** about 1 ms higher.
  - The earlier "+9 to +18 points of throttling" and "+24 ms p95" were the worst pod and the stall averaged in (review 3.1–3.2).
- **Latency stalls: intermittent.**
  - **L4 first run:** a 10 s outage with 2,205 timeouts.
  - **L5:** 2 of 3 runs stalled, and both stalls **began at the same second (t=973)**, with a trigger not yet identified (review 3.3).
  - **No stalls in any control, in F1 or in F5.**
- **Mechanism, corrected.** The agent was **not** pushing back when the app stalled.
  - **L5 runs:** Alloy accepted and refused nothing after about t=105–385. Its memory was flat at about 50 MiB, and the app's exports failed in about 35 ms.
  - **L4 outage:** Alloy was crash-looping.
  - **What every app-side effect shares:** each pod holding about 200k series and a 460 MiB heap, and collecting them every 10 s. That is the cost of cardinality inside the SDK (review 3.4).
- **Fix F1 proven (SDK allow-list View, `make fix-app`).** The app *still adds* `response_time` and 5 GUIDs, but the View drops them before export. Every signal returned to control level:
  - 8.2k points / 1.2 MB per export, 0 failures
  - Alloy healthy
  - heap 41 MiB
  - median p95 63 ms
- **F5 (latency as 5 bands) is safe:** about 2× control points, 0 export failures, no app impact.
- **Run A remains a strong production candidate.** The OTel cardinality limit applied to every instrument made the SDK reserve **about 560 MiB, 97% of the GC heap, at startup**. The result was `OutOfMemoryException`, a total outage, and **no `OOMKilled` in Kubernetes**. It needs no bad label at all.
- **Histogram accuracy:** the old buckets put p95 at 89 ms against a true 63 ms. The buckets are now shaped around real latencies (section 4b).
- **Next:** see [next-steps.md](next-steps.md):
  1. get the production facts (cardinality limit, series per pod, appended labels, Alloy limits)
  2. re-test run A with those settings
  3. a longer L5 with a second control
  4. size the fleet and agent from production figures, then repeat P300

## How we recreated it, and how it shows impact on the running app

In this repo, `transactions-api` plays the part of the production app. It serves live HTTP transactions with a 3s client timeout, and it ships its metrics the way production does: OTel SDK → Alloy agent on its own node pool → Prometheus.

### The causal chain we're testing

```
bad labels in app code ──► more series in the app's OTel SDK ──► bigger export every 10s (built in the app pod)
        │                                                              │
        │  (observed: heap 460+ MiB,                 agent can't keep up ◄┘  (observed: OOMKilled, crash-looping,
        │   collect 200k series / 10s)               (refuses / slows / OOMs)  or receiving nothing)
        ▼                                                     ┊
app pod spends CPU + heap on metrics ◄┄┄ retries, payloads held in memory   (tested, NOT observed at the
        │                                                                      time of any stall; review 3.4)
        ▼
less CPU left for requests ──► throttling ──► higher latency ──► stalls / timeouts   (small; stalls intermittent)
```

The solid path is what the data supports. **The dotted path, agent push-back driving the app's cost, was the original hypothesis, but the agent was crashed or idle whenever the app stalled.**

### Why the runs isolate the metrics as the cause

Each level is a **control / hypothesis pair** run back to back on the same cluster:

- **Identical in both runs:**
  - code and image, replicas and CPU/memory limits
  - nodes, agent profile and Prometheus
  - request rate (constant, not a ramp) and duration
  - a full reset first (app, Alloy and Prometheus restarted, with fresh series)
- **The only difference:** the labels on one metric.
  - Control: `consume_type, state, product, pod`.
  - Hypothesis: those, plus `response_time` at 6 dp and 5 unique GUID labels.

So any difference in the app's CPU, heap, throttling or latency comes from the labels.

Each step of the chain is measured **inside the app pods**, not inferred from dashboards:

| Link in the chain | Measured by | L5 control → hypothesis |
|---|---|---|
| Series per export | wrapped OTLP exporter (points per export) | 8.2k → **200k** points |
| Export payload | the exporter's HTTP handler (bytes sent) | 1.2 MB → **67 MB** every 10s |
| Agent state | exporter failures and retries; Alloy's accepted/refused points; Kubernetes restarts | 0 → **452 failures / 45 retries**. Alloy refused up to 11k points/s early, was OOMKilled 5×, then from t≈125 **accepted and refused nothing** (exports failing in about 35 ms) |
| App memory | .NET GC heap, polled from each pod | 41 → **482 MiB** (84% of the GC limit) |
| App CPU contention | the pod's cgroup `cpu.stat`: share of time throttled at its 1-core limit, **mean across pods** | 9.8% → 10.9% (t=420–960); 9.4% → 14.7% (t=1030–1200, after the stall). Per-pod CPU 0.785 → 0.787 cores |
| Live traffic | the load driver's own clients | median late p95 **62.6 → 63.6 ms**; a **2 s stall at p95 1,340 ms** from t=973; 0 failed requests |

### Other explanations, and why they were ruled out

- **Too much load:** requests alone needed about 3.17 of the 4 CPU cores (about 1.67 ms of CPU per request, measured in each run). The control ran the same load cleanly.
- **Prometheus or Grafana stealing CPU:** `PROTECT_PROM=1` drops the high-cardinality series in Alloy before remote write, so Prometheus sees about the same load in both runs (head series: 125k in the control, 109k in the hypothesis).
- **GC pauses:** at most 3.6% of wall time, about 0.15% on average.
- **Background noise on the nodes:** every control was clean. For L5, the app nodes were also given 4 CPUs instead of 3 to reduce that noise.

### What it does and doesn't show

**Shown:**

- High-cardinality labels make the running app pay for its own metrics: **heap** clearly (about 480 MiB against 40 MiB), and **CPU** slightly (about 1–4 points of mean throttling, about 1 ms of median p95).
- Pods carrying that state stalled in 2 of 3 L5 runs (both from t=973) and in 1 of 2 L4 runs. Controls, F1 and F5 never did.
- An SDK allow-list View (F1) removes all of it, even with the bad labels still in the code.

**Not shown:**

- **That agent back pressure causes the app's cost.** The agent was crashed or idle during every stall (review 3.4).
- That outages are *reliable*: only one run (L4) had timeouts.
- What triggers the t=973 stalls.
- That any of this is what happened in production. Production already buckets response time (section 4b).

**Confound:** the normal-agent levels (L1–L3) also had lower load (60–70%) and 3-CPU nodes. There is no L5-load run with the normal agent, so "agent profile decides the outcome" is not established.

### Recreate from scratch

```sh
make prereqs                        # k3d + helm (Homebrew)
APP_NODE_CPUS=4 make up             # cluster: control plane, 2 app nodes (4 CPUs each), tainted agent node
make monitoring                     # Prometheus (1 GiB, remote-write receiver) + Grafana + dashboards
make agent-constrained PROTECT_PROM=1   # Alloy: 250m CPU / 384 Mi, memory_limiter, drops bad series before Prometheus
make app                            # build + deploy transactions-api (4 replicas) and the load-test driver
make ui                             # console :8080, Grafana :3000, Prometheus :9090
```

Then, in the console (http://localhost:8080):

1. Open **Hypothesis tests** and set the capacity to 2,350.
2. Choose **L5**.
3. Click **Run control** and wait for its report (about 22 min).
4. Click **Run hypothesis** (about 22 min). Each run resets everything first.
5. Compare the two reports, or `make reports` and open the files in `./reports/`.

The hypothesis report's verdict section reports the control run it compared against.

**Pod-label test with the 300-pod fleet (section 4b):**

```sh
make fleet-node                     # once: tainted node for the simulated pods (1.75 GB, 2 CPUs)
make prod-pods                      # real replicas stamp only their own pod name (VIRTUAL_PODS=1)
make agent-constrained PROTECT_PROM=1   # the protect rule also keeps fleet series out of Prometheus
# control: fleet off -> run the hypothesis preset with response_time precision "band" and 0 GUID labels
make fleet-off
# fleet run: same labels, 300 simulated pods sharing the agent
make fleet-on                       # 2 x 150 pods; then optionally: make appended-labels N=4
make lab-pods                       # afterwards: back to 50 pod names per replica for L1-L5
```

The report of a fleet run finds its control automatically: the same labels on the real replicas, with no fleet.

---

## 1. Hypothesis

High-cardinality metric labels (here, `response_time` as a float label, plus GUID-style labels appended by observability tooling) make the OpenTelemetry SDK hold and export ever-larger payloads. When the agent can't keep up, the resulting back pressure costs the app pod CPU and/or memory. That competes with live request handling and degrades response times.

It has two parts, and they're tested separately:

| Part | Question | Status |
|---|---|---|
| **A** | Does cardinality create back pressure on the metrics pipeline (agent)? | ✅ Reproduced (L1, L2) |
| **B** | Does that back pressure degrade the live app? | ⚠️ **The app is affected, but not through back pressure.** Its cost comes from the SDK's own state: heap about 480 MiB, throttling +1–4 points (mean across pods), median p95 +1 ms. Stalls in 1 of 2 L4 runs and 2 of 3 L5 runs. The agent was crashed or idle during each stall. ❌ No stall with the normal agent (L1–L3, confounded by lower load). ✅ Removed by fix F1 |

## 2. Setup

| Component | Configuration |
|---|---|
| Cluster | k3d on Docker Desktop (8 GB VM). Control-plane node, **2 app-pool nodes** (2.25 GB each, capped at **3 real CPUs** for L1–L4 and **4 CPUs from L5**), **1 agent-pool node** (1.25 GB, 2 CPUs, tainted, Alloy only) |
| App | .NET 10 `transactions-api`, **4 replicas**, each **1 CPU / 768 Mi** limit. .NET GC heap hard limit = 75% of the container limit = **576 MiB** |
| Simulated pods | L1–L5: 50 virtual `pod` label values per replica = **200 pods**. This models "each process emits many pod values", not production's own-pod identity. From section 4b: `VIRTUAL_PODS=1` (own pod name) plus a **300-pod simulated fleet** (`make fleet-on`) |
| Request cost | `CPU_WORK_ITERATIONS=1000` ⇒ **≈1.7 ms CPU per request** ⇒ capacity **≈2,350–2,410 req/s**, measured in every report and limited by the 4 × 1-core CPU limits |
| OTel SDK | 1.21, **cumulative** temporality, OTLP/HTTP, export **every 10s**, timeout **8s**, `OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY=in_memory` |
| Cardinality limit | `OTEL_CARDINALITY_LIMIT=200000`, **scoped to `http.transactions` only** (`OTEL_CARDINALITY_LIMIT_SCOPE=transactions`) |
| Metric | `http_transactions_total{consume_type(4), state(4), product(10), pod(200), [response_time], [GUID labels]}`, plus the `http_transaction_duration_seconds` histogram `{consume_type, state, product}` |
| Histogram buckets | L1–L5 and F1/F5: `5, 10, 25, 50, 100, 250, 500 ms, 1, 2.5, 5, 10 s`. From section 4b: `10, 25, 40, 50, 60, 70, 80, 100, 250 ms, 1, 3 s`. Both have 11 boundaries = 12 buckets + sum + count = **14 series per label combination** |
| Agent | Grafana Alloy v1.21.3, "normal" profile: 1 GiB, no CPU limit, batch 10s, OTLP receiver max request 64 MiB → remote_write |
| Prometheus | 1 GiB limit. `PROTECT_PROM=1` means Alloy drops high-cardinality series just before remote_write, so Prometheus and Grafana stay up while the app → agent path is unchanged |
| Load | In-cluster .NET driver, open-model arrival rate, **3s client timeout**. Business outcomes are all HTTP 200, so every error is overload |

## 3. Method

**Constant-rate soak tests**, not ramps. A ramp raises load and cardinality at the same time, so their effects can't be separated (see run C below). In a soak the request rate is fixed, so anything that gets worse over time is caused by growing cardinality and export cost.

Each level is run twice from the console's **Hypothesis tests** panel:
- **Control:** bounded labels (`consume_type, state, product, pod`)
- **Hypothesis:** all labels + `response_time` (6 dp, ≈ unique per request) + N unique GUID labels

Every run starts with **Reset everything**:
1. Restart the app replicas, which clears the SDK series.
2. Restart Alloy.
3. Wipe Prometheus.
4. Apply the labels, then wait one export cycle.

Each report compares the first and last quarter of the run (after a 60s warm-up) for CPU, throttling, export cost, p95 and failures.

| Level | Load | Duration | Hypothesis labels | Agent |
|---|---|---|---|---|
| **L1** ✅ | 60% (1,400 req/s) | 10 min | + `response_time` | normal |
| **L2** ✅ | 60% (1,400 req/s) | 10 min | + 3 unique GUID labels | normal |
| **L3** ✅ | 70% (1,650 req/s) | 15 min | + 5 unique GUID labels | normal |
| **L4** ⚠️ ×2 | 70% (1,650 req/s) | 15 min | + 5 unique GUID labels | constrained (250m CPU / 384 Mi + memory_limiter) |
| **L5** ✅ | 80% (1,900 req/s) | 20 min | + 5 unique GUID labels | constrained (app nodes at 4 CPUs) |

**Rule:** if the app stays healthy (verdict NOT CONFIRMED or PARTIAL), the level passes and the next one runs.

---

## 4. Results

### L1: 1,400 req/s, 10 min, `response_time` label

| | Control (bounded) | **Hypothesis** |
|---|---|---|
| Client errors | 0 | **0** |
| p95 early → late | 61 → 62 ms | **62 → 62 ms** (worst 1-s p95: 149 ms) |
| App CPU early → late (4 pods) | 2.31 → 2.39 cores | 2.47 → 2.41 cores |
| Pod throttling early → late | 0.4% → 1.3% | 4.5% → 2.5% |
| .NET heap (start → peak) | 24 → 27 MiB | **24 → 153 MiB** |
| Export duration early → late | 2 → 3 ms | **27 → 1,737 ms** (peak 8,012 ms = timeout) |
| Export payload per replica | 8k points / 1.2 MB | **200k points (SDK cap) / 34.9 MB** |
| Export failures / retries | 0 / 0 | **54 / 18** |
| Alloy (normal, 1 GiB) | healthy | **OOMKilled 4×** |
| Verdict | healthy | NOT CONFIRMED (headline: "Alloy restarted during the run") |

### L2: 1,400 req/s, 10 min, `response_time` + 3 unique GUID labels

| | Control (bounded) | **Hypothesis** |
|---|---|---|
| Client errors / worst p95 | 0 / 64 ms | **0 / 70 ms** |
| p95 early → late | 62 → 62 ms | **62 → 62 ms** |
| App CPU early → late (4 pods) | 2.29 → 2.29 cores | 2.33 → 2.37 cores |
| Pod throttling early → late | 1% → 1% | 2% → 3% |
| .NET heap (direct polls, peak) | 35 → 36 MiB | **70 → 383 MiB** (66% of the 576 MiB GC limit) |
| App container memory peak | 73 MiB | **449 MiB** of 768 MiB |
| Export duration early → late | 2 → 3 ms | **20 → 3,033 ms** (peak 8,016 ms = timeout) |
| Export payload per replica | 8k points / 1.2 MB | **200k points / 67.1 MB** (just under Alloy's 64 MiB = 67.1 MB request limit) |
| Export failures / retries | 0 / 0 | **94 / 34** |
| Alloy (normal, 1 GiB) | healthy (351 MiB) | **OOMKilled 5×** (peak 936 MiB) |
| Verdict | healthy | NOT CONFIRMED: "export cost grew but did not affect the app" |

**L2 hypothesis, app heap by minute** (max across replicas, MiB). It was still climbing when the run ended:

| min | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| heap | 70 | 111 | 167 | 194 | 280 | 301 | 325 | 345 | 370 | 383 | 383 |

### L3: 1,650 req/s, 15 min, `response_time` + 5 unique GUID labels

| | Control (bounded) | **Hypothesis** |
|---|---|---|
| Requests | 1,485,001 ok, 0 failed, worst 1-s p95 80 ms | **1,485,001 ok, 0 failed**, worst 1-s p95 87 ms |
| p95 early → late | 62 → 62 ms | **62 → 63 ms** |
| App CPU early → late (4 pods) | 2.72 → 2.77 cores (≈69%) | 2.77 → 2.70 cores |
| Pod throttling early → late | 4% → 10% | 8% → 7% |
| .NET heap (direct polls, peak) | 37 MiB | **94 → 476 MiB, then flat** (83% of the 576 MiB GC limit) |
| App container memory peak | ~70 MiB | **534 MiB** of 768 MiB |
| Export duration early → late | 2.6 → 2.5 ms | **744 → 17 ms** (peak 8,022 ms). It *fell* once Alloy was down and exports failed fast |
| Export payload per replica | 8k points / 1.2 MB | **200k points / 67.06 MB**, the same ceiling as L2 |
| Export failures / retries | 0 / 0 | **259 / 13** (export pressure began at t=224s) |
| Alloy (normal, 1 GiB) | healthy (361 MiB) | **OOMKilled 4×, then CrashLoopBackOff** (peak 992 MiB) |
| Verdict | healthy | NOT CONFIRMED: "export cost grew but did not affect the app" |

**L3 hypothesis, app heap by minute** (max across replicas, MiB). It leveled off when each pod hit the 200k-series cap:

| min | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9–15 |
|---|---|---|---|---|---|---|---|---|---|---|
| heap | 94 | 170 | 276 | 316 | 352 | 386 | 453 | 475 | 476 | 469–476 |

### L4: 1,650 req/s, 15 min, `response_time` + 5 unique GUID labels, **constrained agent**

| | Control (bounded) | **Hypothesis** |
|---|---|---|
| Requests | 1,485,001 ok, **0 failed**, worst 1-s p95 82 ms | 1,482,797 ok, **2,205 timeouts (3s)**, worst 1-s p95 **3,000 ms** |
| Degradation | none | **t=183s**: p95 1.0 → 3.0 s for about 10 s (61% failing at the worst second); second blip at t=224s (p95 ≈ 690 ms) |
| Replicas | healthy | **All 4 unresponsive at once.** The driver's /admin/stats polls timed out, and readiness probes failed on all 4 pods at 19:42:37–41. No restarts (too short for liveness) |
| p95 early → late (soak quarters) | 62 → 62 ms | 215 → 63 ms (the stall fell inside the first quarter) |
| App CPU early → late (4 pods) | 2.71 → 2.68 cores | 2.83 → 2.72 cores. **During the stall: 0.8–1.0 core per pod (the limit)** |
| Pod throttling | 4% → 3% | **17% → 7%**, with **33–64% spikes during the stall** (cAdvisor) |
| Request-only CPU at 1,650 req/s | 2.71 cores | 2.81 of 4 cores, so **not load saturation** |
| .NET heap (direct polls, peak) | 36 MiB | 100 → **488 MiB, then flat** (85% of the 576 MiB GC limit) |
| Export pressure | none | **Began at t=46s** (before the stall). Peak 8,682 ms, **334 failures / 67 retries**, 200k points / 67.0 MB |
| Alloy (constrained: 250m / 384 Mi) | 293 MiB, 0 refused | **Refused up to 10,764 points/s** (memory_limiter), then **OOMKilled 6×** |
| Report verdict | NOT CONFIRMED (healthy) | Originally NOT CONFIRMED (analysis gap); **after the fix: CONFIRMED — transient outage at constant load, control clean** |

**Why the report originally said NOT CONFIRMED (now fixed).** The soak rule compares the first and last quarter, and requires latency to be *higher at the end*. Here the stall happened in the first quarter. The app then recovered, because Alloy crash-looped: exports failed fast and the pressure lifted (insight 10). The ramp rule's criteria are all met:
- degradation ✔
- export pressure, at ≥50% of the timeout plus retries ✔
- CPU contention, with throttling up to 95% ✔
- pressure *before* the degradation (t=46s vs t=183s) ✔
- not load saturation (2.81 < 3.6 cores) ✔

**Mechanism (revised after review 3.4).** The original reading was that, while the constrained agent was alive but refusing or slow, the replicas kept retrying and serializing 67 MB payloads. **The data doesn't support it.** Alloy's memory is missing (container not running) for most samples from t=144 to t=224, and accepted and refused points both end at t=115. So at the time of the stall (t=183), Alloy was crash-looping, not refusing. What the stalled pods share with the later L5 stalls is the SDK's own state: about 200k series, a heap of about 480 MiB, collected every 10 s. GC pause metrics travel through Alloy and were lost when it failed, so GC's share in this run is unmeasured. In later runs it was at most 3–4%.

**Why all 4 at once.** Reset everything restarted all replicas together, so their 10s export timers and series growth are aligned. A production rollout lines pods up the same way.

### L4 repeat (identical settings)

| | Repeat control (bounded) | **Repeat hypothesis** |
|---|---|---|
| Requests | 1,485,017 ok, 0 failed, worst 1-s p95 **377 ms** (a spike with no metrics pressure) | 1,485,001 ok, **0 failed**, worst 1-s p95 128 ms |
| Degradation / stall | none | **none** |
| p95 early → late | 62 → 68 ms | 62 → 63 ms |
| App CPU early → late | 2.81 → 2.73 cores | 2.75 → 2.73 cores |
| Pod throttling early → late | **17% → 11%** (noisier than the first control's 4% → 3%) | 13% → 6% |
| .NET heap (peak) | 40 MiB | 97 → **477 MiB, then flat** |
| **GC pause (direct polls, new)** | peak 1.5%, 16 gen2 | **peak 3.2%**, avg 0.14%, 35 gen2 |
| Export pressure | none | began at t=51s; peak 8,017 ms, **330 failures / 56 retries**, 200k points / 66.4 MB |
| Alloy (constrained) | 374 MiB, 0 refused | **refused up to 18,619 points/s**, OOMKilled 6× |
| Verdict | healthy | NOT CONFIRMED (control found and clean; no degradation) |

**Reading the two L4 pairs together.** The pipeline side reproduced exactly: the same failures, retries, refusals, OOMKills and heap. The app outage did not. GC was negligible in the repeat. Both the repeat control and the first hypothesis run show that these 3-CPU nodes see bursts of CPU contention with or without metrics pressure. The first outage is therefore best described as **metrics pressure coinciding with (and possibly amplifying) a contention burst**, not a deterministic effect at this level.

### L5: 1,900 req/s, 20 min, `response_time` + 5 unique GUID labels, constrained agent (2026-11-15)

**Changed before this run:** app nodes went from 3 to 4 CPUs (`APP_NODE_CPUS=4 make node-cpus`) to cut background contention. The app's own 1-core limit per replica is unchanged. The agent profile was constrained + protect.

| | Control (bounded) | **Hypothesis** |
|---|---|---|
| Requests | 2,280,002 ok, 0 failed, worst 1-s p95 177 ms | 2,280,018 ok, **0 failed**, worst 1-s p95 **1,340 ms** |
| Degradation / stall | none (0 s above 250 ms) | **2 s at p95 ≥ 1 s** (t=976–977), 7 s above 250 ms |
| p95 early → late | 63 → 63 ms | 63 → **87 ms** |
| App CPU early → late | 3.13 → 3.15 cores | 3.18 → 3.20 cores |
| Pod throttling early → late | 34% → 33% | 34% → **42%** |
| .NET heap (peak) | 41 MiB | **482 MiB** (84% of the GC limit) |
| GC pause | peak 0.6%, 23 gen2 | peak 3.6%, avg 0.15%, 40 gen2 |
| Export pressure | none; 1.2 MB, 8.2k points | began at t=42s; peak 8,014 ms, **452 failures / 45 retries**, 200k points / 67.0 MB |
| Alloy (constrained) | 297 MiB, 0 refused | **refused up to 11,162 points/s**, OOMKilled 5× (all in the first ~4 min) |
| Verdict | healthy | **CONFIRMED: transient outage at constant load** (control clean) |

**What the stall looked like** (from the driver's 2-second pod polls):

- **Before:** exports had been failing fast (about 25–80 ms) since about t=330s. Alloy was running (about 50 MiB) but **accepted and refused nothing** from t≈125 on. The reason exports fail is not yet known (now recorded; see section 4b).
- **t=973s:** the first second above 100 ms (142 ms). **Repeat 1 also began degrading at exactly t=973** (134 ms), so this looks like a specific trigger about 16 minutes in, not chance. It is not the load driver (`sent` steady at 1,898–1,908/s), not Alloy (flat, idle), not a gen2 GC (none on any pod at t=974–987), and not the exports (latency rose before the slow exports at t=976). The trigger is unidentified.
- **t≈972s:** in-flight requests rose from about 70 to 1,650.
- **t=974–979s:** **all four replicas** were at 0.96–1.01 cores, throttled 67–96%.
- **During the stall:** the two replicas that exported at t=976 took 1,057 ms and 1,240 ms to do it, against about 30 ms just before. The exports at t=981–985 took 480–750 ms.
- **By t≈983s:** latency had recovered.

**Interpretation:**

- The stall happened while every replica was carrying about 200k series of SDK state. Something at t=973 then pushed all four replicas over their CPU limit at once.
- The slow exports *during* the stall are a symptom of the CPU contention, not its trigger.
- **The steady-state difference is small.** Mean throttling across pods was 10.9% against 9.8% before the stall, and median late p95 was 63.6 ms against 62.6 ms. The earlier "+9 points / +24 ms" figures were the most-throttled pod and the stall averaged into a mean (review 3.1–3.2).

### L5 repeats and fix runs F1 and F5 (same rate, duration and agent; the L5 control is reused)

| | Control | Hypothesis (1st) | Repeat 1 | Repeat 2 | **F1: allow-list fix** | **F5: latency band** |
|---|---|---|---|---|---|---|
| Labels the app adds | bounded | + `response_time`(6dp) + 5 GUIDs | same | same | **same** (dropped by the SDK View) | `response_time` as **5 bands**, no GUIDs |
| Client failures | 0 | 0 | 0 | 0 | 0 | 0 |
| Worst 1-s p95 | 177 ms | **1,340 ms** (2 s ≥ 1 s) | 497 ms | 169 ms | 182 ms | 164 ms |
| Seconds with p95 > 250 ms | 0 | 7 | 3 | 0 | 0 | 0 |
| p95 early → late, mean of per-second p95 | 63 → 63 | 63 → 87 | 64 → 73 | 63 → 64 | 63 → 63 | 63 → 63 |
| **Late p95, median** | **62.6** | **63.6** | **63.7** | **63.6** | **62.8** | **62.8** |
| First second above 100 ms (after warm-up) | t=598 (177 ms, single) | **t=973** | t=323 (single), **t=973** | t=490, 613 (single) | none | t=541 (single) |
| Throttling early → late, **worst pod** per poll (report statistic) | 34 → 33% | 34 → 42% | 42 → 51% | 34 → 42% | 31 → 31% | 30 → 21% |
| **Throttling, mean across pods**, t=420–960 / t=1030–1200 | **9.8 / 9.4%** | **10.9 / 14.7%** | **11.2 / 18.4%** | **14.1 / 17.3%** | **10.7 / 9.6%** | **8.6 / 5.9%** |
| Per-pod CPU, t=420–960 | 0.785 | 0.787 | 0.787 | 0.799 | 0.791 | 0.780 |
| App CPU, late quarter | 3.15 | 3.20 | 3.23 | 3.20 | 3.16 | 3.11 |
| App heap (peak) | 41 MiB | 482 MiB | 481 MiB | 477 MiB | **41 MiB** | 49 MiB |
| Export points / payload | 8.2k / 1.2 MB | 200k / 67 MB | 200k / 66 MB | 200k / 67 MB | **8.2k / 1.2 MB** | 15.8k / 2.8 MB |
| Export failures / retries | 0 / 0 | 452 / 45 | 455 / 68 | 455 / 56 | **0 / 0** | 0 / 2 |
| Alloy | healthy | refused up to 11.2k/s early; **OOMKilled 5×**; accepted/refused nothing after t≈125 | refused up to 10.8k/s early; **OOMKilled 6×**; nothing after t≈140 | refused up to 10.6k/s; **OOMKilled 5×**; nothing after t≈385 | **healthy, 0 refused** | healthy (brief refusals, up to 882 points/s) |
| Verdict | healthy | CONFIRMED (stall) | NOT CONFIRMED (Alloy restarted) | NOT CONFIRMED | healthy | healthy |

**Reading it:**

- **CPU contention: consistent in direction, small in size.** Mean throttling across pods ended higher than control, F1 and F5 in all 3 hypothesis runs. But before the t=973 event the difference is 1–4 points, about the size of run-to-run drift (F5 ended 4 points *below* control). There is only one control. Most of the late-quarter difference in the first run and repeat 1 comes *after* the t=973 stall. (The report's "throttling" figure is the most-throttled pod per poll; reports now show the mean too.)
- **Latency impact: intermittent, and both L5 stalls began at t=973.** That points to a specific trigger about 16 minutes into the run, which only hurts pods that are carrying the 200k-series state. It isn't yet identified.
- **Alloy restart counts:** the Prometheus-based `alloyRestartsDelta` undercounts while Alloy is down. Kubernetes records 5, 6 and 5 OOMKills. Reports now take the Kubernetes `restartCount`.
- **F5 (latency as 5 bands, no GUIDs) is safe at this level.** Export points rose to about 2× control (15.8k; only 2 bands occur at normal latency) and the payload to 2.8 MB. There were 0 export failures and no app impact. Note that `PROTECT_PROM` drops every series carrying `response_time`, so the band series didn't reach Prometheus here. Their Prometheus cost is estimated (about 2× the bounded series), not measured.
- **F1 is the strongest single result.** With the same bad labels still in the code, a View dropping them brings *every* signal back to control. That rules out "something else about those runs" and identifies the fix.

**Histogram accuracy (measured in F1, server-side histogram vs the driver's client-side percentiles):**

| | Histogram (`histogram_quantile`) | Driver (client) |
|---|---|---|
| p50 | 34 ms | 37 ms |
| p95 | **89 ms** | **63 ms** |
| p99 | 98 ms | 69 ms |

- **Where requests fall:** 78% are under 50 ms and 100% under 100 ms. That leaves p95 and p99 inside one wide bucket (50–100 ms), where linear interpolation overestimates.
- **What's useful anyway:** the histogram is very stable (p95 between 88.3 and 88.8 ms all run), so it's good for trends and alerts. Its absolute values are only as precise as the buckets.
- **Cost:** 8,960 series in Prometheus: 160 label combinations × 14 (12 buckets + sum + count) × 4 replicas. Of those, 7,680 are `_bucket` series.

### L1 → L5 trend (hypothesis runs)

| | L1 | L2 | L3 | **L4** | **L5** |
|---|---|---|---|---|---|
| Load | 1,400 req/s (60%) | 1,400 req/s (60%) | 1,650 req/s (70%) | 1,650 req/s (70%) | 1,900 req/s (80%) |
| Extra labels | — | 3 GUIDs | 5 GUIDs | 5 GUIDs | 5 GUIDs |
| Agent | normal | normal | normal | **constrained** | **constrained** (nodes at 4 CPUs) |
| Peak export payload | 34.9 MB | 67.1 MB | 67.06 MB (ceiling) | 67.0 MB | 67.0 MB |
| Export failures / retries | 54 / 18 | 94 / 34 | 259 / 13 | 334 / **67** | 452 / 45 |
| Alloy | OOMKilled 4× | OOMKilled 5× | OOMKilled 4× + crash loop | refusing 10.7k pts/s, then OOMKilled 6× (crash-looping at the stall) | refusing about 11k pts/s early, OOMKilled 5–6×, then idle |
| Peak app heap | 153 MiB | 383 MiB | 476 MiB (83%) | 488 MiB (85%) | 482 MiB (84%) |
| Peak pod throttling (direct polls) | 52% | 56% | 100% | 95% (cAdvisor 33–64% during the stall) | 100% (all 4 replicas during the stall) |
| **App p95 / client errors** | flat / 0 | flat / 0 | flat / 0 | **3,000 ms / 2,205 timeouts** (first run); flat / 0 (repeat) | **stalls from t=973 in 2 of 3 runs** (worst 1,340 ms) / 0; median late p95 about 1 ms above control |

### L1 → L2: what the GUID labels changed

| | L1 hypothesis | L2 hypothesis | Change |
|---|---|---|---|
| Bytes per exported point | ≈175 | ≈335 | **×1.9** (3 GUIDs ≈ +40 B each, plus encoding overhead) |
| Export payload per replica | 34.9 MB | 67.1 MB | ×1.9 |
| Export failures | 54 | 94 | ×1.7 |
| Late-run export duration | 1.7 s | 3.0 s | ×1.7 |
| Peak app heap | 153 MiB | 383 MiB | **×2.5** |
| Alloy OOMKills | 4 | 5 | |
| App p95 / errors | flat / 0 | flat / 0 | **no change** |

---

## 4b. Pod-label hypothesis: a 300-pod fleet (2026-11-15)

**Why:** production already buckets response time, so the float `response_time` label is not the production cause. What's left is the **pod label** and the labels the observability team appends. Each production pod stamps only its own name, so no single pod sends much. The open question is whether the **aggregate** of many pods overwhelms the agent, and whether that pushes back on every app.

**Setup (all in PR "fleet simulator"):**

- **Simulated fleet** (`make fleet-node`, `make fleet-on`): 2 fleet replicas × 150 = **300 simulated pods**.
  - **Placement:** they run on their own tainted node (1.75 GB, 2 CPUs), never on the app nodes.
  - **Per pod:** its own OTel SDK pipeline and resource (`service.instance.id` = pod name), and its own OTLP export to Alloy every 10s at its own phase.
  - **Labels:** production-style: `consume_type, state, product`, its **own** `pod` name, and `response_time` as **5 bands**.
  - **Series:** the duration histogram, plus 100 extra series standing in for runtime, HTTP and dependency metrics.
- **Real replicas production-like** (`make prod-pods`): each of the 4 real replicas stamps only its own pod name (`VIRTUAL_PODS=1`), with `response_time` as bands.
- **Histogram buckets retuned** around the real latencies: `10, 25, 40, 50, 60, 70, 80, 100, 250 ms, 1, 3 s`. This keeps the same bucket count and fixes the 89-against-63 ms p95 overstatement.
- **Prometheus:** the `PROTECT_PROM` agent configs also drop the simulated pods' series (by `otel_scope_name`) before Prometheus. Alloy still receives and converts all of them.
- **Reports:** they record the fleet size. A fleet run's control is the same labels on the real replicas, with no fleet.
- **Added after the review (deployed 2026-11-15 15:16 UTC, after the P300 pair):**
  - **Why exports fail.** Each failed HTTP attempt is recorded by outcome: `HTTP <code>` (the request reached the agent), `timeout / cancelled`, `connection: <error>`, or "not sent" (the export failed before any HTTP request). This applies to the real replicas and to the fleet, in reports and in `/admin/stats`.
  - **Appended identity labels** (`make appended-labels N=4`): 0–8 labels such as `k8s_node_name`, `k8s_pod_uid` and `container_id`, added to every series on the real replicas (`APPENDED_LABELS`) and the fleet (`FLEET_APPENDED_LABELS`). One value per pod, so they make series heavier without multiplying them. This models labels the observability team appends. Re-run it after `make fleet-on`, which re-applies the manifest.
  - **Reports** show throttling as the worst pod *and* the mean across pods, p95 as the mean *and* the median, and Alloy restarts from the Kubernetes `restartCount`.
  - **Fleet churn** retires pods in the background, and never overlaps ticks.

**First observation (idle, before any test):** with the constrained agent (250m CPU / 384 MiB) and 300 pods:
- Alloy refused about **29k points/s** and accepted about 1k/s.
- About 55% of fleet exports failed.
- **The real replicas' metrics stopped reaching Prometheus too.** Aggregate overload blinds monitoring for *every* pod, not only the ones causing it.

**Runs:** the P300 control (no fleet) and the P300 fleet run, at 1,900 req/s for 20 min with the constrained agent. Results are below the criteria.

**Criteria, written down at 14:30 UTC on 2026-11-15, before the fleet run started.** This follows section 6.1 of [review-2026-11-15.md](review-2026-11-15.md). The fleet run is compared with the P300 control, which has the same labels on the real replicas and no fleet.

- **Prediction:** the agent is overwhelmed, with refusals and failed fleet exports, and the real replicas' metrics are lost. **The app is unaffected**, because each real replica holds only a few hundred series. Every app-side effect so far needed large per-pod SDK state (review 3.4).
- **"App affected":**
  - **Fail:** any second at p95 ≥ 1 s, or any client failure, with a clean control.
  - **Possible small effect:** mean throttling *across pods* at least 3 points above control in both t=420–960 and t=1030–1200, **and** a median late p95 at least 2 ms above control.
  - **No effect:** otherwise. There's only one control, so effects of a few points are within run-to-run noise.
- **Pipeline signals:**
  - Alloy refused points/s.
  - The fleet export failure rate.
  - Alloy OOMKills, counted from the Kubernetes termination records (`restartCount`), not `alloyRestartsDelta`, which undercounts.
  - Whether the real replicas' series reach Prometheus at all.
- **Limits of this run:**
  - The constrained agent (250m / 384 MiB) is far below the about 17k points/s that 300 pods × about 580 points send every 10 s. This run can only show that an undersized agent is overwhelmed.
  - Appended labels aren't modelled.
  - The fleet's series are dropped before Prometheus.

### P300 results (2026-11-15, 14:27–15:09 UTC)

**Verdict against the criteria: no effect on the app. The prediction held.** The agent was overwhelmed and monitoring went dark for every pod, including the 4 real replicas, while the app served every request.

| | P300 control (no fleet) | **P300 fleet (300 simulated pods)** |
|---|---|---|
| Requests | 2,280,005 ok, 0 failed, worst 1-s p95 236 ms (a 7 s blip at t=606–612) | 2,280,001 ok, **0 failed**, worst 1-s p95 292 ms (4 s above 100 ms at t=575–580) |
| Seconds at p95 ≥ 1 s | 0 | **0** |
| Throttling, mean across pods, t=420–960 / t=1030–1200 | 12.6 / 15.1% | **11.0 / 13.4%** (below control) |
| Throttling, worst pod, late quarter | 42% | 32% |
| Per-pod CPU, t=420–960 | 0.795 | 0.790 |
| Late p95, median | 63.0 ms | **62.8 ms** |
| App heap (peak) | 35 MiB | **35 MiB** |
| GC pause peak | 4.2% | 1.0% |
| Real replicas' exports | 533 points / 116 KB, 0 failures, max 46 ms | 539 points / 117 KB, **464 failures, 1,204 retries**, mean 1.3–1.6 s, peak 8,017 ms (the timeout) |
| Fleet exports during the run | — | 36,881 sent, **36,143 failed (98.0%)**, 92,719 HTTP retries, 569 points per pod |
| Alloy | healthy; 46 points/s refused at t<30 (leftover from switching the fleet off) | memory_limiter engaged at 340–357 MiB of 384 MiB for about 10 min, **refusing 56k–95k points/s** (peak 96,670) and accepting about 0; then **OOMKilled, 6 restarts**; its own metrics stopped at t≈690 |
| Real replicas' metrics reaching Prometheus | throughout | **only about t=600–780** (a short window after an Alloy restart). Dark for about 18 of 20 minutes |
| Fleet series in Prometheus | — | none (the drop rule works) |
| Report verdict | healthy; NOT CONFIRMED | NOT CONFIRMED ("export cost grew but did not affect the app"); headline "Alloy restarted during the run"; control found and clean |

**What this shows:**

1. **Agent overload by itself does not slow the app**, at least not under these OTel .NET semantics with small per-pod state. The real replicas' exports spent the whole run failing, timing out at 8 s and retrying 1,204 times, which is exactly the back-pressure path the original hypothesis described. With about 540 points per export, it cost nothing measurable: the heap stayed at 35 MiB and CPU, throttling and latency matched the control. Combined with review 3.4, that settles it: **the cost of cardinality to the app is the size of the SDK's own state, not the retry or back-pressure path.**
2. **Agent overload costs visibility, for everyone.** 98% of the fleet's exports and almost all of the real replicas' exports were lost. In production this would look like dashboards going blank for the whole service, not like slow requests. If the production symptom was *slow requests*, agent overload alone doesn't explain it.
3. **Retries multiply the agent's inbound load.** The fleet offered about 17k points/s (300 × 569 points per 10 s), but Alloy was refusing 56k–95k points/s, because `in_memory` retries re-offered the same payloads about 5 times over. A refusing agent plus client retries is a feedback loop on the agent, even when it's harmless to the apps.
4. **Scale check.** The constrained agent (250m CPU / 384 MiB) is a deliberately undersized stand-in. 300 pods × about 570 points is a small fleet by production standards, and this agent couldn't take it. Sizing the agent and the fleet from production figures (series per pod, Alloy limits and replica count) is the next step before this test says anything about production's agent.

**First reading of the new failure-reason counters** (15:17 UTC, fleet still on, Alloy in CrashLoopBackOff after 7 OOMKills): every export from the real replicas and from the fleet failed as **`timeout / cancelled`** after the full 8 s. A Service whose only pod is crash-looping has no ready endpoints, so connections hang instead of being refused. Each export then occupies the background export thread for 8 of every 10 seconds, waiting on the network, not on CPU. The 35 ms failures seen in the L4/L5 runs are a different case, and will be classified the next time a high-cardinality run is made.

**What it leaves open:** why the production app got slow. The candidates still standing are a raised `CardinalityLimit` (run A), and large per-pod SDK state from appended labels or high-resolution histograms. Both are about what each pod holds, which is what the production facts in [next-steps.md](next-steps.md) are meant to establish.

## 5. Earlier runs and what they taught us

| Run | What happened | Lesson |
|---|---|---|
| **A. "Default Response Labels"** (ramp, `OTEL_CARDINALITY_LIMIT` applied to **all** instruments) | Collapsed at **100–250 req/s**. Pods hung with **CPU pinned at the 1-core limit**, logs full of `System.OutOfMemoryException`, then `Internal CLR error`, and liveness-probe kills (exit `Error`, *not* `OOMKilled`). Container memory was only ~65–120 MiB. | The OTel .NET SDK **pre-allocates metric-point storage sized by the cardinality limit for every metric stream**. 200k × ~30 instruments reserved **≈560 MiB, 97% of the GC heap limit, at startup**. Any new series then ran out of heap. **This is a real production failure mode:** a high cardinality limit leaves almost no heap headroom, and Kubernetes memory metrics don't show it. Fixed here by scoping the limit to `http.transactions` (idle heap 561 → 26 MiB). |
| **B. "TestWith New Settings"** (ramp to 250 req/s, 2.4 min) | No effect. App at 6% CPU, 7k series per replica. | Too short and too light. Request CPU was raised (`CPU_WORK_ITERATIONS` 50 → 1000) so the pods do realistic work. |
| **C. "New Run Make Fail"** (fast ramp 150 → 3,150 req/s, 15s steps) | Collapsed at 2,750–2,950 req/s. The tool's verdict said "CONFIRMED", but that was **wrong**: CPU was **4.0 of 4 cores at 2,350 req/s from request work alone**, and exports stayed under 100 ms *while* the app degraded. They only reached 8.8s *after* the collapse. Alloy and Prometheus were OOMKilled. | **A ramp can't separate load from cardinality.** The verdict was fixed: export pressure must start *before* the degradation, and CPU from requests alone, measured early in the run, must be under the limit. The method switched to constant-rate soaks. |
| **After L1** | **Alloy crash-looped for 25+ minutes after the test ended** (7 restarts), until the app pods were restarted. | The app replicas still held 200k cumulative series and kept retrying 35 MB exports every 10s, so the agent OOMs on every start. **The pipeline doesn't recover until the apps restart.** In production that's a metrics outage lasting until the next deploy. |

---

## 6. Insights

1. **The export path really is non-blocking, and that protects latency while there's CPU headroom.** Request threads only do an in-memory `Counter.Add`. Exports run on a background thread, and with Alloy crashed or slow they mostly **wait** on the network rather than burn CPU. That's why serializing 35–67 MB every 10s didn't show up as CPU growth (about 2.3–2.4 cores of 4 throughout).
2. **The app is harmed through memory, not CPU.** Cumulative temporality keeps every series ever seen. Retries (`in_memory`) hold failed payloads. GUID labels make every series bigger. The heap grows roughly with *series × bytes per series*, plus payloads held for retry.
3. **The .NET GC heap limit (75% of the container limit) is the real ceiling, and it's invisible to Kubernetes.** At that limit, allocations throw `OutOfMemoryException` and the GC thrashes at 100% CPU. Health checks then time out, and the pod is killed by the liveness probe with exit `Error`. **It never appears as `OOMKilled`**, so container-memory dashboards and OOMKill alerts miss it.
4. **High cardinality limits are dangerous by themselves.** Raising `OTEL_CARDINALITY_LIMIT` reserves heap up front for every instrument it applies to, before any traffic arrives (run A).
5. **The agent is the first casualty.** Even a 1 GiB Alloy with no CPU limit OOMs on 4 × 35–67 MB pushes every 10s. A constrained agent (L4/L5) fails sooner and refuses data early in the run. *(Revised:)* the refusals were over within the first 2–7 minutes, and didn't coincide with any app stall.
6. **Failures are sticky.** Because the app keeps cumulative state, the agent can't recover until the app restarts (see "After L1" above).
7. **Payload size scales with label bytes, not just series count.** After the SDK cap is reached, the series count stops growing, but GUID labels still nearly double each export (≈175 → ≈335 bytes per point).

8. **The SDK cardinality cap bounds the app's memory.** At L3 the heap leveled off at about 470 MiB once each pod reached 200k series. The higher the limit, the higher that ceiling, and above about 576 MiB the app fails (see run A). The limit has two effects: it reserves heap at startup, and it caps the growth later.
9. **Exports appear capped at about 64 MiB per request.** L2 and L3 peaked at the same 67.06–67.10 MB (64 MiB = 67,108,864 B) despite different label sizes. Larger exports seem to fail before they're sent (either the exporter's buffer limit or Alloy's 64 MiB `max_request_body_size`). The result is silent metric loss, not pressure on the app.
10. **Agent failure reduces the app's export cost.** With Alloy crash-looping or idle, exports fail instantly, so late-run export time *dropped* (L3: 744 → 17 ms). In OTel .NET, push back from a dead agent turns into data loss, not app CPU. *(Revised:)* in L4/L5 the exports kept failing in about 35 ms for 13+ minutes while Alloy was up and counting nothing. **Why** is unknown. One hypothesis: payloads just over Alloy's 64 MiB `max_request_body_size` (the peak recorded was 67,042,833 B, the limit is 67,108,864 B) were rejected before Alloy counted them. Export failure reasons are now recorded to settle it.

11. ~~**The agent's failure mode decides whether the app gets hurt.**~~ **Withdrawn (review 3.4).** It was claimed that an agent that refuses or slows keeps the app retrying, and "that caused the L4 outage". In fact Alloy was crash-looping at the L4 stall (t=183) and idle at the L5 stalls (t=973). The comparison behind it (normal agent L1–L3 against constrained L4–L5) is also confounded by load (60–70% against 70–80%) and node CPUs (3 against 4).
12. **Synchronized pods fail together.** Replicas started at the same time (a rollout, or this reset) export on aligned timers, so the heavy export and GC cycles hit every pod at once, taking out the whole Service rather than one pod.
13. **The app-side cost of cardinality lives in the SDK, not in the network path** (review 3.4). Every app-side effect seen (heap about 480 MiB, small throttling increase, stalls) occurred in pods holding about 200k series. It persisted while the agent was crashed or idle, and disappeared with F1. So the questions for production are about **series per pod** and **the cardinality limit**, not only about the agent.
14. **Pick robust statistics.** A worst-pod throttling average and a mean of per-second p95 both exaggerate short or single-pod events. Reports now also show the mean across pods and the median p95. Judge effects against more than one control.

## 7. What to check in production

Possibly relevant to the 2 clusters × 75 pods with degraded response times:

- [ ] **OTel cardinality limit:** is it raised (`SetCardinalityLimit`, `SetMaxMetricPointsPerMetricStream`, or a View with `CardinalityLimit`), and for which instruments? A high limit on many instruments means a large heap reserved at startup (run A). **Top candidate now that bucketing is confirmed.**
- [ ] **Series per pod:** count the series for one pod's `instance` across all its metrics. App-side effects here needed about 200k per pod.
- [ ] **How response time is bucketed:** a histogram, a band label, or rounding to N decimals? Rounding still grows during incidents.
- [ ] **Distinct `pod` values against running pods**, for the biggest metric. A ratio well above 1 means churn or peer-pod labels.
- [ ] **Temporality:** cumulative or delta? Cumulative keeps every series forever (until restart).
- [ ] **Export interval and retry settings** (`OTEL_METRIC_EXPORT_INTERVAL`, `OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY`).
- [ ] **Which labels the observability team appends, and their cardinality:** anything per-request (IDs, GUIDs, URLs, durations as labels)?
- [ ] **.NET heap against 75% of the pod memory limit:** `dotnet_gc_last_collection_heap_size_bytes`, or `process.runtime.dotnet.gc.heap.size`.
- [ ] **GC pause time and gen2 collections:** a large heap means longer gen2 pauses, which add directly to request latency. *Not yet measured in these reports; worth adding.*
- [ ] **Pod restarts with exit `Error` after liveness-probe failures**, not `OOMKilled`.
- [ ] **Agent (Alloy or Collector) restarts and OOMKills, and OTLP request sizes.**
- [ ] **CPU throttling** (`container_cpu_cfs_throttled_periods_total`) if pods run close to their CPU limits.

## 8. Next steps

Pick-up notes, cluster state and the open decisions are in [next-steps.md](next-steps.md). In order:

1. ~~Finish the P300 pair~~ **Done: no effect on the app; agent overwhelmed; monitoring dark.** (section 4b)
2. **Get the production facts:** the cardinality limit settings, series per pod, the bucketing form, the appended labels and where they're added, and Alloy's limits and replicas. Then size the fleet and agent from them.
3. **Re-test run A with production's real cardinality limit settings.** It needs no bad label and doesn't show as `OOMKilled`.
4. **Deploy the export-failure-reason recording** and find out why exports fail in about 35 ms while Alloy counts nothing.
5. **One longer L5 hypothesis (40 min or more) and a second L5 control**, to see whether the t=973 event recurs on a period, and to measure run-to-run noise.
6. **L5 with the normal agent**, to separate agent profile from load level.
7. **Appended identity labels** (`make appended-labels N=4`) with the fleet, once production's list is known.

Done earlier: soak verdict with a control (now CONFIRMED needs a clean control), GC pause recording, L4 repeat, L5 ×3, fix runs F1 and F5.

## 8b. Corrections log

| Date | Correction | Source |
|---|---|---|
| 2026-11-15 | "CPU contention 3 of 3, +9 to +18 points" was the **most-throttled pod** per poll. The mean across pods is +1–4 points before the t=973 event. | [review](review-2026-11-15.md) 3.1 |
| 2026-11-15 | "+24 ms steady-state p95" was the stall averaged into a mean. The median shift is about 1 ms. | review 3.2 |
| 2026-11-15 | Both L5 stalls began at t=973. The trigger is unidentified. | review 3.3 |
| 2026-11-15 | "Agent refusal caused the outage" (insight 11, L4 mechanism) withdrawn: the agent was crash-looping or idle at every stall. | review 3.4 |
| 2026-11-15 | Alloy restarts in the L5 repeats are 6 and 5, not 1 and 0 (`alloyRestartsDelta` undercounts). | review 5.5 |
| 2026-11-15 | Histogram cost is 8,960 series (14 per combination), not 7,680. | review 5.6 |
| 2026-11-15 | Production already buckets response time. The float label isn't the production cause. | user, review 5.7 |

## 9. Reports

All reports are in `./reports/` (gitignored). Refresh them with `make reports`, and open one with `open reports/<file>.html`.

| Run | Report file |
|---|---|
| **P300 fleet** (300 simulated pods) | `reports/20261115-144847-p300-fleet-300-simulated-pods-1900-req-s.html` |
| P300 control (no fleet) | `reports/20261115-142737-p300-control-no-fleet-1900-req-s-20m-con.html` |
| **L5 F5 latency band** | `reports/20261115-134526-l5-f5-latency-band-hypothesis-1900-req-s.html` |
| **L5 F1 allow-list fix** | `reports/20261115-132242-l5-f1-allow-list-fix-hypothesis-1900-req.html` |
| L5 repeat 2 hypothesis | `reports/20261115-130130-l5-repeat-2-hypothesis-1900-req-s-20m-co.html` |
| L5 repeat 1 hypothesis | `reports/20261115-124020-l5-repeat-1-hypothesis-1900-req-s-20m-co.html` |
| **L5 hypothesis** (stall) | `reports/20261115-121217-l5-hypothesis-1900-req-s-20m-constrained.html` |
| L5 control | `reports/20261115-114959-l5-control-1900-req-s-20m-constrained-ag.html` |
| L4 repeat hypothesis | `reports/20261114-203146-l4-repeat-hypothesis-1650-req-s-15m-cons.html` |
| L4 repeat control | `reports/20261114-201541-l4-repeat-control-1650-req-s-15m-constra.html` |
| **L4 hypothesis** (outage) | `reports/20261114-193930-l4-hypothesis-1650-req-s-15m-constrained.html` |
| L4 control | `reports/20261114-192325-l4-control-1650-req-s-15m-constrained-ag.html` |
| L3 hypothesis | `reports/20261114-190418-l3-hypothesis-1650-req-s-15m-normal-agen.html` |
| L3 control | `reports/20261114-184305-l3-control-1650-req-s-15m-normal-agent.html` |
| L2 hypothesis | `reports/20261114-175400-l2-hypothesis-1400-req-s-10m-normal-agen.html` |
| L2 control | `reports/20261114-174256-l2-control-1400-req-s-10m-normal-agent.html` |
| L1 hypothesis | `reports/20261114-171704-l1-hypothesis-1400-req-s-10m-normal-agen.html` |
| L1 control | `reports/20261114-170432-l1-control-1400-req-s-10m-normal-agent.html` |
| C. Ramp false positive | `reports/20261114-163859-new-run-make-fail.html` |
| A. GC heap pre-allocation | `reports/20261114-154311-default-response-labels.html` |

Runs named `validation: …`, `instrumentation-check …`, `post-fix-check`, `lock-check` and `report-smoke-test*` were tooling checks, not experiments.
