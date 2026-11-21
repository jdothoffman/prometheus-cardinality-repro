# Experiment log: metrics pressure vs the live app

**Date:** 2026-11-14 · **Environment:** local k3d cluster (this repo) · **Status (revised 2026-11-15 after [an independent review](review-2026-11-15.md)):** L1–L5 complete, L4 ×2, L5 ×3, fix runs F1 and F5 done, a 300-pod fleet test done, and the run-A sweep of the SDK cardinality limit done (section 4c). **Production already buckets response time**, so the float `response_time` label is not the production cause. The suspect is now the pod and appended labels (section 4b). **In this repo the app-side cost comes from each pod's own SDK state (about 200k series), not from agent back pressure. It is small in steady state, with latency stalls in 2 of 5 constrained-agent runs. F1 removes it.**

## TL;DR

- **Run-A sweep (section 4c): a raised cardinality limit on every instrument takes the app down by itself, with bounded labels and about 160 real series per instrument.** The SDK allocates each metric stream's storage up front (`limit + 2` metric points), so the heap is set by the limit, not by the series: about **4 KiB of GC heap per unit of limit** across this app's metric streams. 2k, 10k, 50k and 100k passed with no cost beyond the heap (13 / 44 / 197 / 393 MiB, throttling and median p95 within noise). **200k failed:** heap at 99% of the GC limit, gen2 collections back to back, 25 liveness-probe kills in 10 minutes, 90% of requests failed, container memory at 160–170 MiB of 768 MiB and **no `OOMKilled`**. For this app the line is about 145k. With the SDK default of 2,000, the reserved heap is 13 MiB, so the cause can only be this if production raised the limit.
- **40-minute L5 pair (section 4, "L5 × 40 min"): no stall in either run, and the export failures are explained.** The hypothesis run carried 200k series and a 480 MiB heap per pod for 40 minutes with **0 failed requests, a worst 1-second p95 of 77 ms**, and throttling inside the spread of the controls. The t=973 stall did not recur: **2 of 4 hypothesis runs stalled; no bounded run went above 236 ms.** From minute 6 on, every export was **dropped by the OTel SDK before sending**, because the payload passed the OTLP exporter's **64 MiB `MaxRequestSizeBytes`**. That is the "35 ms failure" of L4/L5, and it silently took the pod's *own* metrics (heap, GC, requests) out of Prometheus for the last 34 minutes while Alloy sat idle. With three bounded runs to compare against, the labels' steady-state CPU effect at 80% load is **within noise**, and the median p95 effect is at most about 1 ms. The reliable app-side costs are **memory** and **going dark**.
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
  - **Throttling (mean across pods):** 9.3–12.6% across three bounded runs, against 10.8–14.1% in the four hypothesis runs (t=420–960). Within noise.
  - **Per-pod CPU:** within 1–2% of control.
  - **Median p95:** 0–1 ms higher (62.5–63.1 ms in bounded runs, 62.8–63.6 ms in hypothesis runs).
  - The earlier "+9 to +18 points of throttling" and "+24 ms p95" were the worst pod and the stall averaged in (review 3.1–3.2).
- **Latency stalls: intermittent.**
  - **L4 first run:** a 10 s outage with 2,205 timeouts.
  - **L5:** 2 of 4 runs stalled, both **beginning at the same second (t=973)**. The 40-minute hypothesis run and both controls were clean, so the trigger isn't periodic, and it isn't identified (review 3.3, 40-min pair).
  - **No stalls in any control, in F1 or in F5.**
- **Mechanism, corrected.** The agent was **not** pushing back when the app stalled.
  - **L5 runs:** Alloy accepted and refused nothing after about t=105–385 and sat at about 50 MiB, because the app's exports **never left the pod**: the SDK dropped each 200k-point payload as larger than its 64 MiB request limit (verified by the failure reasons recorded in the 40-min run).
  - **L4 outage:** Alloy was crash-looping.
  - **What every app-side effect shares:** each pod holding about 200k series and a 460 MiB heap, and collecting them every 10 s. That is the cost of cardinality inside the SDK (review 3.4).
- **Fix F1 proven (SDK allow-list View, `make fix-app`).** The app *still adds* `response_time` and 5 GUIDs, but the View drops them before export. Every signal returned to control level:
  - 8.2k points / 1.2 MB per export, 0 failures
  - Alloy healthy
  - heap 41 MiB
  - median p95 63 ms
- **F5 (latency as 5 bands) is safe:** about 2× control points, 0 export failures, no app impact.
- **Run A is confirmed by the sweep (section 4c).** The cardinality limit applied to every instrument at 200k reserves **about 560 MiB, 97% of the GC heap, at startup**. The result is a total outage through liveness-probe restarts, and **no `OOMKilled` in Kubernetes**. It needs no bad label at all.
- **Histogram accuracy:** the old buckets put p95 at 89 ms against a true 63 ms. The buckets are now shaped around real latencies (section 4b).
- **Next:** see [next-steps.md](next-steps.md):
  1. get the production facts (cardinality limit, series per pod, appended labels, Alloy limits, exporter version, pods whose series went stale)
  2. ~~the run-A sweep~~ **done (section 4c)**; once production's limit and the instruments it applies to are known, read the risk off the sweep and re-test with production's exact settings
  3. size the fleet and agent from production figures, then repeat P300

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
| Agent state | exporter failures and retries; Alloy's accepted/refused points; Kubernetes restarts | 0 → **452 failures / 45 retries**. Alloy refused up to 11k points/s early, was OOMKilled 5×, then from t≈125 **accepted and refused nothing**: the SDK was dropping every export inside the pod, as larger than its 64 MiB request limit |
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

- High-cardinality labels make the running app pay for its own metrics: **heap** clearly (about 480 MiB against 40 MiB), and **CPU** at most slightly: the 1–4 points of mean throttling are within the spread of the bounded runs, and the median p95 is at most about 1 ms higher (40-min pair).
- Pods carrying that state stalled in 2 of 4 L5 runs (both from t=973; not in the 40-minute run) and in 1 of 2 L4 runs. Controls, F1 and F5 never did.
- Once a pod's export passes the SDK's 64 MiB request limit, **all** of its metrics stop arriving, silently, while the agent sits idle (40-min pair).
- An SDK allow-list View (F1) removes all of it, even with the bad labels still in the code.

**Not shown:**

- **That agent back pressure causes the app's cost.** The agent was crashed or idle during every stall (review 3.4).
- That outages are *reliable*: only one run (L4) had timeouts.
- What triggers the t=973 stalls. A 40-minute run didn't reproduce them.
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
| **B** | Does that back pressure degrade the live app? | ⚠️ **The app is affected, but not through back pressure.** Its cost comes from the SDK's own state: heap about 480 MiB, throttling +1–4 points (mean across pods), median p95 +1 ms. Stalls in 1 of 2 L4 runs and 2 of 4 L5 runs (none in the 40-minute run). The throttling and p95 shifts are within the noise of three bounded runs. The agent was crashed or idle during each stall. ❌ No stall with the normal agent (L1–L3, confounded by lower load). ✅ Removed by fix F1 |

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
- **t=973s:** the first second above 100 ms (142 ms). **Repeat 1 also began degrading at exactly t=973** (134 ms), so this looks like a specific trigger about 16 minutes in, not chance. It is not the load driver (`sent` steady at 1,898–1,908/s), not Alloy (flat, idle), not a gen2 GC (none on any pod at t=974–987), and not the exports (latency rose before the slow exports at t=976). The trigger is unidentified. The 40-minute pair (below) didn't reproduce it.
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
- **Latency impact: intermittent, and both L5 stalls began at t=973.** That points to a specific trigger about 16 minutes into the run, which only hurts pods that are carrying the 200k-series state. It isn't yet identified. A 40-minute hypothesis run and a second control (next subsection) were clean, so it's not periodic.
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

### L5 × 40 min: does the t=973 stall recur, and how noisy is the environment? (criteria written before the run)

**Written at 16:01 UTC on 2026-11-15, before the runs started.** Two runs, same settings as L5 (1,900 req/s, constrained + protect agent, app nodes at 4 CPUs, `VIRTUAL_PODS=50`), but **40 minutes** each:

1. **40-min control** (bounded labels). It doubles as the second control the review asked for.
2. **40-min hypothesis** (`response_time` 6 dp + 5 unique GUID labels). The first run on the build that records *why* exports fail.

**Questions and how they'll be read:**

- **Does the t=973 event recur?** Look for the first second at p95 > 100 ms after warm-up, in both runs.
  - **In the hypothesis only, near t=973 and/or again near t≈1946 or 2919:** a periodic trigger that only hurts pods carrying the 200k-series state.
  - **In the hypothesis only, at an unrelated time:** the stalls are real but not periodic.
  - **In the control too, near t=973:** an environmental trigger (something on the nodes at ~16 minutes), not the labels.
  - **Nowhere:** two previous stalls at the same second remain unexplained; at least the effect isn't reliable.
- **Run-to-run noise:** compare the two controls' **mean throttling across pods** over matching windows (t=420–960 and t=1030–1200) and their **median p95**. The difference between controls is the noise floor; a hypothesis effect counts only if it exceeds it.
- **Why exports fail:** the hypothesis run's "Why the app's exports failed" finding classifies the ~35 ms failures seen in L4/L5 (`HTTP <code>` vs `timeout` vs `connection`).
- **Verdict:** the automated verdict applies as before. A CONFIRMED needs the 40-min control clean.

**Results (written 17:50 UTC on 2026-11-15).** Control 16:02–16:42 UTC, hypothesis 16:43–17:23 UTC, run back to back through the prepared-run API with "Reset everything" before each. The figures come from the reports' embedded run data (`tools/report-stats.py`); windows are seconds since the run started.

| | 40-min control | **40-min hypothesis** |
|---|---|---|
| Requests | 4,556,740 ok, 0 failed | 4,556,101 ok, **0 failed** |
| Worst 1-second p95 | 100 ms (one second) | **77 ms** |
| Seconds with p95 > 100 ms after warm-up | 0 | **0** |
| Throttling, mean across pods: t=420–960 / 1030–1200 / last quarter (1815–2400) | 9.3 / 7.7 / 11.3% | 10.8 / 8.9 / **8.6%** |
| Median p95: t=420–960 / 1030–1200 / last quarter | 63.0 / 62.6 / 62.5 ms | 62.8 / 62.9 / 63.1 ms |
| App heap, peak (direct polls) | 37 MiB | **480 MiB** (container 530 MiB). The report's own "heap peak" shows 56 MiB, because it comes from Prometheus and the pod was dark after minute 6 |
| GC pause, peak / average | 2 / 0% | 3.2 / 0.1% |
| Exports per replica | 8.2k points / 1.2 MB, 0 failures, max 132 ms | 200,223 points; payload **67,018,320 B** at minute 5 (171,871 points), then **every export failed**: 935 failures / 46 retries, 50–140 ms each |
| Why exports failed | — | **not sent (dropped before any HTTP request): 844**; timeout / cancelled: 100; HTTP 503: 23; connection: 25 |
| Alloy | healthy, 0 restarts, 292 MiB peak | refused up to 6,690 points/s, **OOMKilled 5×** by t≈230, then **idle at 85 MiB for the remaining 36 minutes** |
| Replicas' metrics reaching Prometheus | throughout (head series 136k) | until t≈360, then **nothing for the last 34 minutes** (head series frozen at 118k) |
| Report verdict | no degradation; NOT CONFIRMED | NOT CONFIRMED ("export cost grew but did not affect the app"); control found and clean |

**Scored against the criteria:**

1. **Does the t=973 event recur? No. Nowhere.** Neither run had a single second above 100 ms after warm-up, so there is nothing near t=973, 1,946 or 2,919 in either. By the criteria: the two earlier stalls at the same second remain unexplained, and the effect isn't reliable. Across all runs at this level, **2 of 4 hypothesis runs stalled (both from t=973, peaking at 1,340 and 497 ms)**, while the five bounded runs (L5 control, F1, F5, P300 control, this control) never went above 236 ms; the P300 control's 7 s blip at t=606–612 is the longest excursion any of them had.
2. **Run-to-run noise.** The two L5 controls (20 min and 40 min) give mean throttling of 9.8 against 9.3% at t=420–960, and 9.4 against 7.7% at t=1030–1200. The P300 control, also bounded and at the same rate (with `VIRTUAL_PODS=1`), gave 12.6 and 15.2%. So **bounded runs vary by about 3 points in the first window and 7 in the second**, and one run's windows drift by about 4 points (this control: 7.7–11.7%). The hypothesis runs' 10.8–14.1% at t=420–960 sit inside that spread, and this hypothesis run's last quarter (8.6%) came out **below** its control (11.3%). The median p95 of bounded runs spans 62.5–63.1 ms, of hypothesis runs 62.8–63.6 ms. **The steady-state CPU effect of the labels at this load is not distinguishable from noise. The median p95 effect is at most about 1 ms, at the edge of it.** The clear, repeatable app-side effect is the heap.
3. **Why exports fail: answered, and it is the SDK, not the agent.** 844 of 992 failed attempts were "not sent": the export failed before any HTTP request. The cause is in the OpenTelemetry .NET OTLP exporter: `OtlpExporterOptions.MaxRequestSizeBytes` defaults to **64 MiB (67,108,864 B)**, and "a batch whose serialized payload exceeds this size is not sent, and is dropped in its entirety" (exporter 1.18.0 changelog; the default was 128 MiB before that). Each replica's payload reached 67,018,320 B at 171,871 points in minute 5. By minute 6, with 198k–200k points, it was over the limit. From then on, every 10 s, each replica serialized its 200k points (50–140 ms of CPU), found the request too large, and dropped it: the counter, the histogram **and the runtime metrics** (heap, GC) in the same batch. That is why Alloy sat idle at 85 MiB while the app counted a failure every 10 s, what the "35 ms failures" of L4/L5 were, and why the replicas were dark in Prometheus for 34 minutes. The other 148 failures (timeouts, 503s, connection errors) all fall in the first five minutes, while Alloy was OOM-looping. Alloy's own `max_request_body_size` was never involved: the requests never left the pod.
4. **Verdict:** NOT CONFIRMED by the report's rule, with a clean 40-min control. That is the right call: nothing happened to the app's requests.

**Where the earlier runs sit** (same rate and agent; windows as above, computed with `tools/report-stats.py`, so a figure may differ by 0.1 from one quoted earlier; the t=1030–1200 window of the two stalled runs includes the stall's tail):

| Run | Throttling mean, t=420–960 | t=1030–1200 | Median p95, t=420–960 | t=1030–1200 | Worst 1-s p95 | Longest run of seconds over 100 ms |
|---|---|---|---|---|---|---|
| L5 control (20 min) | 9.8% | 9.4% | 62.8 ms | 62.8 ms | 177 ms | 1 s |
| 40-min control | 9.3% | 7.7% | 63.0 | 62.6 | 100 ms | 0 |
| P300 control (bounded, `VIRTUAL_PODS=1`) | 12.6% | 15.2% | 63.1 | 63.0 | 236 ms | 7 s (t=606–612) |
| F1 (bad labels dropped by the View) | 10.7% | 9.5% | 62.9 | 62.8 | 182 ms (in warm-up) | 0 |
| F5 (5 bands) | 8.6% | 5.9% | 63.0 | 63.0 | 164 ms | 1 s |
| L5 hypothesis | 10.9% | 14.6% | 63.0 | 63.3 | **1,340 ms** | **14 s (t=973–986)** |
| L5 repeat 1 | 11.1% | 18.3% | 63.3 | 63.6 | **497 ms** | **6 s (t=973–978)** |
| L5 repeat 2 | 14.1% | 17.3% | 63.0 | 63.4 | 169 ms | 1 s |
| **40-min hypothesis** | 10.8% | 8.9% | 62.8 | 62.9 | **77 ms** | 0 |

**Re-reading the two stalls with this run's data.** Every high-cardinality run, stalled or not, reached the 64 MiB limit at the same point (minute 5–6), so the drop isn't what sets a stall off at t=973. What the two stalls still have in common: the driver's send rate held at 1,898–1,908/s; in-flight requests rose at t=972, one second before p95 did; at the next poll (t=976) **all four replicas, on both nodes, were at 0.99–1.01 cores and 77–100% throttled**; GC pauses stayed at or below 2.4%, with no gen2 collection during the stall; and the exporter's serialize-and-drop pass took 1,057–1,240 ms (first run) and 500–753 ms (repeat 1) instead of 50–140 ms, so the export thread was starved like everything else (a symptom, not the trigger). Something outside the request path raised every replica's CPU demand at once, in two of four hypothesis runs and in no bounded run (the P300 control's 7 s blip peaked at 236 ms, with no sign of all four pods saturating). Node-level metrics from those runs can't be checked now: "Reset everything" restarts Prometheus at the start of each run, so its history only covers the current run. If a stall shows up again, the report needs the node's own CPU and pressure metrics next to the pods'.

**What this changes:**

- The reliable app-side effects of high cardinality are **memory** (about 480 MiB of SDK state per pod) and **loss of the pod's own metrics**, silently, once its export passes 64 MiB. Latency and CPU effects at 80% load are within noise, apart from intermittent stalls that did not repeat in 40 minutes.
- "The pod goes dark" is a production check in its own right: with OTLP push there is no `up` series for a pod; its series simply go stale. Any pod that was alive while its metrics stopped is a candidate for this mechanism (section 7).
- A per-pod export budget follows from the limit: 64 MiB ÷ bytes per point. At about 390 B per point (this run: 5 GUID labels) that is about 172k points; at about 175 B (one float label, L1) about 380k. With bounded labels and the SDK's default limit of 2,000 points per instrument, a pod can't get near it, which brings the question back to **which limits production raised** (run A).

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
| **App p95 / client errors** | flat / 0 | flat / 0 | flat / 0 | **3,000 ms / 2,205 timeouts** (first run); flat / 0 (repeat) | **stalls from t=973 in 2 of 4 runs** (worst 1,340 ms; none in the 40-min run) / 0; median late p95 0–1 ms above control |

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

**First reading of the new failure-reason counters** (15:17 UTC, fleet still on, Alloy in CrashLoopBackOff after 7 OOMKills): every export from the real replicas and from the fleet failed as **`timeout / cancelled`** after the full 8 s. A Service whose only pod is crash-looping has no ready endpoints, so connections hang instead of being refused. Each export then occupies the background export thread for 8 of every 10 seconds, waiting on the network, not on CPU. The 35 ms failures seen in the L4/L5 runs are a different case, and will be classified the next time a high-cardinality run is made. **Classified in the 40-min pair: the SDK dropped them inside the pod, before any HTTP request, because the payload was over its 64 MiB request limit.**

**What it leaves open:** why the production app got slow. The candidates still standing are a raised `CardinalityLimit` (run A), and large per-pod SDK state from appended labels or high-resolution histograms. Both are about what each pod holds, which is what the production facts in [next-steps.md](next-steps.md) are meant to establish.

## 4c. Run-A sweep: the cardinality limit by itself (2026-11-15)

**Why:** run A (section 5) showed that `OTEL_CARDINALITY_LIMIT=200000` applied to *every* instrument made the SDK reserve about 560 MiB, 97% of the GC heap, before any traffic arrived. The app then failed with `OutOfMemoryException` and no `OOMKilled`. That needs no bad label, which makes it the strongest remaining candidate for production. The sweep maps the whole curve, so production's limit can be read off it once known.

**Criteria and predictions, written at 19:27 UTC on 2026-11-15, before the first run (19:28).**

**Settings, the same for every run:**

- **Labels:** bounded (`consume_type, state, product, pod`), and **`VIRTUAL_PODS=1`** (`make prod-pods`): each replica stamps only its own pod name, as production does. That gives about 160 series per instrument, below even the SDK's default limit of 2,000, so no value in the sweep overflows and the only thing that changes is the limit.
- **Load and agent:** 1,900 req/s (80% of capacity), **10 minutes** each, constrained + protect agent, 4 replicas, app nodes at 4 CPUs. Each run is a prepared run ("Reset everything" restarts the pods, which keep the environment set below).
- **The limit:** `OTEL_CARDINALITY_LIMIT_SCOPE=all` (every instrument: the app's, ASP.NET Core's and the runtime's, about 30) with `OTEL_CARDINALITY_LIMIT` at **2,000** (the SDK default), **10,000**, **50,000**, **100,000** and **200,000**, in that order.
- **Control:** first, the repo's usual setting (`OTEL_CARDINALITY_LIMIT=200000` on `http.transactions` only), same labels, rate and duration.
- **Measured:** the idle heap of each replica 60 s after the limit is applied, before load (direct `/admin/stats` polls), against the 576 MiB GC heap limit (75% of the 768 MiB container limit); then the report of each soak. Statistics with `tools/report-stats.py --windows 120-600`.

**A value fails** if its soak has any client errors, a stall (5 s or more over 100 ms after warm-up), a pod restart, or `OutOfMemoryException` in the pod logs. **A value has a steady-state cost** if the mean throttling across pods or the median p95 over t=120–600 is outside the noise floor measured in section 4 (3 points of throttling, 0.5 ms of median p95: the spread of three bounded runs) compared with the sweep's control. The report's automated verdict looks for "a control" by labels and duration, so for these runs it may pick the previous sweep value; it is not used here.

**Predictions:**

- Idle heap grows **linearly** with the limit: about 2.8 KB per unit (run A: 560 MiB ÷ 200,000 across about 30 instruments), on top of about 26 MiB. So about 32 MiB at 2k, 55 MiB at 10k, 160 MiB at 50k, 300 MiB at 100k and **about 580 MiB at 200k, at or over the GC limit**.
- **200k fails**, as in run A, probably at startup or within the first minute of load.
- **2k–50k pass** with no steady-state cost.
- **100k:** the open case. It should start with about 270 MiB of headroom. If it costs anything, it will show as more GC time (gen2 collections must walk the reserved storage) and, from that, throttling or p95.

### Run-A sweep results (2026-11-15, 19:28–20:41 UTC)

**Verdict: 200k on every instrument fails, as predicted; 2k–100k pass with no steady-state cost.** The 200k run lost 90% of its requests to liveness-probe restarts with only about 160 real series per instrument, so the failure needs no bad label. The cost is linear in the limit at about **4 KiB of GC heap per unit of limit**, steeper than the 2.8 KB predicted, which puts the failure threshold for this app at a limit of about **145k** on every instrument.

| Limit (scope) | Heap under load, per pod | Share of 576 MiB GC limit | Requests failed | Stalls (≥ 5 s over 100 ms) | Restarts | Throttling, mean across pods (t=120–600) | Median p95 | Result |
|---|---|---|---|---|---|---|---|---|
| Control: 200k (`http.transactions` only) | 30–34 MiB | 6% | 0 | none | 0 | 11.7% | 62.7 ms | — |
| 2,000 (all) | 13 MiB | 2% | 0 | none | 0 | 9.8% (−1.9) | 63.1 ms (+0.4) | pass, no cost |
| 10,000 (all) | 43–45 MiB | 8% | 0 | none | 0 | 11.9% (+0.2) | 62.7 ms (0) | pass, no cost |
| 50,000 (all) | 196–197 MiB | 34% | 0 | none | 0 | 11.6% (−0.1) | 63.0 ms (+0.3) | pass, no cost |
| 100,000 (all) | 391–395 MiB | 68% | 0 | none | 0 | 10.0% (−1.7) | 63.1 ms (+0.4) | pass, no cost |
| **200,000 (all)** | **567–569 MiB** | **99%** | **1,015,285 of 1,132,024 (90%)** | 13, longest 23 s | **25 liveness kills, then CrashLoopBackOff** | not measurable (pods unreachable 70–80% of polls) | 2,668 ms | **fail** |

Heap is the peak of direct `/admin/stats` polls during the soak. Differences in brackets are against the control; the noise floor is 3 points of throttling and 0.5 ms of median p95. All runs: 1,900 req/s, 10 minutes, bounded labels, `VIRTUAL_PODS=1`, constrained + protect agent; head series about 104.5k in every run; 0 export failures except 9 at 200k; 0 Alloy restarts.

**What happened at 200k.** Every pod started its soak with the heap at 560 MiB, 97% of the GC limit, before any request arrived. Under load the GC ran gen2 collections back to back (up to 64 in one process lifetime, pause time up to 41%), `/healthz` timed out, and the kubelet killed the container: **25 times in 10 minutes, every one recorded as "failed liveness probe"**, until two pods were in CrashLoopBackOff. Of the failed requests, 997k were connection errors (no ready pod) and 23k were 3-second timeouts. The container's own memory stayed at 160–170 MiB of its 768 MiB limit throughout, so **nothing showed as `OOMKilled` and container-memory dashboards saw nothing unusual.** The GC heap counts the reserved metric storage at once, while the kernel charges the container only for pages that have been written.

**Why it costs heap with almost no series.** The SDK allocates each metric stream's storage up front: `AggregatorStore` creates `new MetricPoint[cardinalityLimit + 2]` and an `int[]` of the same length when the stream is created (OpenTelemetry .NET 1.21.0, `src/OpenTelemetry/Metrics/AggregatorStore.cs`). So the limit, not the number of series, sets the heap, multiplied by the number of streams it applies to.

**Against the predictions:**

- **Linear: yes; slope: no.** Under load the heap is about 5 MiB + 4 KiB × limit (2k: 13, 10k: 44, 50k: 197, 100k: 393 MiB). The prediction of 2.8 KB per unit came from run A's idle figure, which under-read (next point). Extrapolated, 200k needs about 780 MiB, well past the 576 MiB limit, and the limit is reached at about 145k.
- **The idle reading is not a reliable measure.** It came out 4.6 / 39 / 53 / 387 / 561 MiB, not a straight line: 50k read 53 MiB idle against 197 MiB under load. `GC.GetGCMemoryInfo()` describes the heap as of the last garbage collection, and with no traffic a collection may not have run since the storage was allocated. Under load collections are frequent, so the soak figures are the ones to use. The pre-registered design measured idle heap; that part of the design was wrong, and the table uses the soak peaks instead.
- **200k fails: yes**, within the first 10 seconds of load (p95 1,474 ms at t=9, 69% failed by t=11), not at startup: all four pods passed their readiness probes and served the first seconds of load.
- **2k–50k pass with no cost: yes.** **100k (the open case): passes with no cost**, gen2 at most 1 and GC pause at most 1%, with 32% of the GC heap left.
- **`OutOfMemoryException`: yes.** The sweep's restore step replaced the 200k pods before their logs were read, so the value was rerun for 3 minutes (20:43–20:48 UTC, same settings) with the logs captured every 15 s. All four pods threw `System.OutOfMemoryException` inside request handling (Kestrel's "unhandled exception was thrown by the application"), and three of them also crashed with `Fatal error. Internal CLR error (0x80131506)` during exception dispatch. Every pod was restarted twice in 3 minutes, each time with exit code 137 (killed after failing its liveness probe), never `OOMKilled`. The rerun's restarts and termination reasons match the soak's.

**What it means for production.** A raised cardinality limit applied to every instrument is enough on its own to take a .NET service down, with bounded labels and without any `OOMKilled`. For this app (about 30 metric streams, 768 MiB pods) the line is between 100k and 200k; for production it scales with **the number of streams the limit applies to × the limit**, against 75% of the pod memory limit. With the SDK default (2,000 per stream) the reserved heap is about 13 MiB, so if production never raised the limit, this is not the cause. The fact to get is therefore exactly section 7's first item: the limit, and which instruments it applies to.

**Reports:** `reports/20261115-192946-ra-control-…`, `-194206-ra-all-2000-…`, `-195412-ra-all-10000-…`, `-200618-ra-all-50000-…`, `-201825-ra-all-100000-…`, `-203041-ra-all-200000-…` (section 9).

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
4. **High cardinality limits are dangerous by themselves.** Raising `OTEL_CARDINALITY_LIMIT` reserves heap up front for every instrument it applies to, before any traffic arrives (run A). The sweep (section 4c) measured it: about 4 KiB of GC heap per unit of limit across this app's metric streams, linear from 2k to 100k, and an outage at 200k with only about 160 real series per instrument.
5. **The agent is the first casualty.** Even a 1 GiB Alloy with no CPU limit OOMs on 4 × 35–67 MB pushes every 10s. A constrained agent (L4/L5) fails sooner and refuses data early in the run. *(Revised:)* the refusals were over within the first 2–7 minutes, and didn't coincide with any app stall.
6. **Failures are sticky.** Because the app keeps cumulative state, the agent can't recover until the app restarts (see "After L1" above).
7. **Payload size scales with label bytes, not just series count.** After the SDK cap is reached, the series count stops growing, but GUID labels still nearly double each export (≈175 → ≈335 bytes per point).

8. **The SDK cardinality cap bounds the app's memory.** At L3 the heap leveled off at about 470 MiB once each pod reached 200k series. The higher the limit, the higher that ceiling, and above about 576 MiB the app fails (see run A). The limit has two effects: it reserves heap at startup, and it caps the growth later.
9. **The SDK drops any export over 64 MiB, whole and silently.** `OtlpExporterOptions.MaxRequestSizeBytes` (OpenTelemetry .NET OTLP exporter 1.18 and later; default 64 MiB = 67,108,864 B): a batch whose serialized payload exceeds it is not sent and is dropped in its entirety, before any HTTP request. L2, L3 and L5 all peaked at 66.5–67.1 MB for that reason, and the failure reasons recorded in the 40-min run confirmed it (844 of 992 failed attempts "not sent"). The result is silent loss of **all** the pod's metrics, not pressure on the app. *(Was: "appear capped… either the exporter's buffer limit or Alloy's `max_request_body_size`".)*
10. **Agent failure reduces the app's export cost.** With Alloy crash-looping or idle, exports fail instantly, so late-run export time *dropped* (L3: 744 → 17 ms). In OTel .NET, push back from a dead agent turns into data loss, not app CPU. *(Resolved, 40-min pair:)* in L4/L5 the exports kept failing in 35–140 ms for the rest of the run while Alloy was up and counting nothing, because the SDK dropped each one before sending (insight 9). Alloy's 64 MiB `max_request_body_size` was never reached.

11. ~~**The agent's failure mode decides whether the app gets hurt.**~~ **Withdrawn (review 3.4).** It was claimed that an agent that refuses or slows keeps the app retrying, and "that caused the L4 outage". In fact Alloy was crash-looping at the L4 stall (t=183) and idle at the L5 stalls (t=973). The comparison behind it (normal agent L1–L3 against constrained L4–L5) is also confounded by load (60–70% against 70–80%) and node CPUs (3 against 4).
12. **Synchronized pods fail together.** Replicas started at the same time (a rollout, or this reset) export on aligned timers, so the heavy export and GC cycles hit every pod at once, taking out the whole Service rather than one pod.
13. **The app-side cost of cardinality lives in the SDK, not in the network path** (review 3.4). Every app-side effect seen (heap about 480 MiB, small throttling increase, stalls) occurred in pods holding about 200k series. It persisted while the agent was crashed or idle, and disappeared with F1. So the questions for production are about **series per pod** and **the cardinality limit**, not only about the agent.
14. **Pick robust statistics.** A worst-pod throttling average and a mean of per-second p95 both exaggerate short or single-pod events. Reports now also show the mean across pods and the median p95. Judge effects against more than one control.
15. **A pod over the export limit goes dark, and nothing says so.** The drop takes the whole batch, including the runtime metrics the pod would otherwise use to report its own trouble. With OTLP push there is no `up` series for a pod; its series go stale. The only signal inside the pod is the exporter's EventSource (`RequestDiscardedDueToSizeLimit`). Watch for pods that are alive while their series are stale.
16. **Measure the noise floor before claiming a small effect.** Three bounded runs at the same rate spread 3 points of mean throttling in one window and 7 in another, and one 40-minute run drifted 4 points between windows. The hypothesis runs' +1–4 points sit inside that. The second control the review asked for turned a "consistent small effect" into "within noise".

17. **Read the heap under load, not at idle.** `GC.GetGCMemoryInfo()` describes the heap as of the last collection. With no traffic, a collection may not have run since the SDK allocated its storage, so idle readings under-read by up to 4× (sweep, 50k: 53 MiB idle against 197 MiB under load). Run A's 2.8 KB per unit came from such a reading. Kubernetes memory under-reads too: the GC heap counts reserved storage at once, but the kernel only charges pages that have been written (100k: 391 MiB of heap, 170–227 MiB of container memory).

## 7. What to check in production

Possibly relevant to the 2 clusters × 75 pods with degraded response times:

- [ ] **OTel cardinality limit:** is it raised (`SetCardinalityLimit`, `SetMaxMetricPointsPerMetricStream`, or a View with `CardinalityLimit`), and for which instruments? A high limit on many instruments means a large heap reserved at startup (run A). **Top candidate now that bucketing is confirmed.** Estimate it as *limit × number of metric streams it applies to × about 130 bytes* (this app: about 4 KiB per unit of limit across about 30 streams; section 4c) and compare that with 75% of the pod memory limit. At the SDK default (2,000) it is negligible.
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
- [ ] **OTLP exporter version and request limit:** `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.18 and later drop any export over 64 MiB (`OtlpExporterOptions.MaxRequestSizeBytes`) before sending, silently. **Did any pod's series go stale while the pod was running?** That is the signature (insight 15), and it would have hidden the pod's heap and GC metrics during the incident.
- [ ] **CPU throttling** (`container_cpu_cfs_throttled_periods_total`) if pods run close to their CPU limits.

## 8. Next steps

Pick-up notes, cluster state and the open decisions are in [next-steps.md](next-steps.md). In order:

1. ~~Finish the P300 pair~~ **Done: no effect on the app; agent overwhelmed; monitoring dark.** (section 4b)
2. **Get the production facts:** the cardinality limit settings, series per pod, the bucketing form, the appended labels and where they're added, and Alloy's limits and replicas. Then size the fleet and agent from them.
3. ~~Run-A sweep~~ **Done (section 4c): 2k–100k pass with no cost beyond the heap; 200k fails with no `OOMKilled`; about 4 KiB of heap per unit of limit.** Next: the re-test with production's real cardinality limit settings, once known.
4. ~~Deploy the export-failure-reason recording~~ **Done: the ~35 ms failures are the SDK dropping exports over its 64 MiB request limit before sending** (section 4, "L5 × 40 min").
5. ~~One longer L5 hypothesis and a second L5 control~~ **Done: no stall in 40 minutes; the steady-state CPU effect is within the noise floor of three bounded runs; median p95 at most 1 ms higher.**
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
| 2026-11-15 | "Exports fail in about 35 ms while Alloy counts nothing; why is unknown" (insight 10): the SDK drops any export over its 64 MiB request limit before sending. Alloy's `max_request_body_size` was never reached. | 40-min pair, failure reasons |
| 2026-11-15 | "L5: 2 of 3 runs stalled" is now 2 of 4. The 40-minute hypothesis run and a second control were clean, and the steady-state CPU effect is within the spread of three bounded runs. | 40-min pair |
| 2026-11-15 | Run A's "about 2.8 KB of heap per unit of limit" came from an idle reading, which under-reads (insight 17). Under load the sweep measured about 4 KiB per unit, linear from 2k to 100k. | run-A sweep, section 4c |
| 2026-11-15 | The report's "App heap (peak)" comes from Prometheus and under-reads once the pod's exports are dropped (56 MiB shown against 480 MiB by direct polls in the 40-min hypothesis run). Quote the direct polls. | 40-min pair |

## 9. Reports

All reports are in `./reports/` (gitignored). Refresh them with `make reports`, and open one with `open reports/<file>.html`.

| Run | Report file |
|---|---|
| **Run-A sweep, 200k (all)** (fails) | `reports/20261115-203041-ra-all-200000-1900-req-s-10m-constrained.html` |
| Run-A sweep, 100k (all) | `reports/20261115-201825-ra-all-100000-1900-req-s-10m-constrained.html` |
| Run-A sweep, 50k (all) | `reports/20261115-200618-ra-all-50000-1900-req-s-10m-constrained.html` |
| Run-A sweep, 10k (all) | `reports/20261115-195412-ra-all-10000-1900-req-s-10m-constrained.html` |
| Run-A sweep, 2k (all) | `reports/20261115-194206-ra-all-2000-1900-req-s-10m-constrained-a.html` |
| Run-A sweep control (200k, `http.transactions` only) | `reports/20261115-192946-ra-control-1900-req-s-10m-constrained-ag.html` |
| **L5 40-min hypothesis** | `reports/20261115-164321-l5-40m-hypothesis-1900-req-s-40m-constra.html` |
| L5 40-min control | `reports/20261115-160209-l5-40m-control-1900-req-s-40m-constraine.html` |
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
