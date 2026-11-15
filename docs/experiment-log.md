# Experiment log: metrics pressure vs the live app

**Date:** 2026-11-14 · **Environment:** local k3d cluster (this repo) · **Status:** L1–L5 complete, L4 ×2, L5 ×3, fix run F1 done. **Resource contention on the app: 3 of 3 L5 hypothesis runs. Latency stalls: 2 of 5 constrained-agent hypothesis runs. F1 (SDK allow-list View) removes it all. All controls were clean.**

## TL;DR

- **L5 ×3 (2026-11-15, 80% load, app nodes at 4 CPUs): resource contention every time; latency impact some of the time.**
  - **CPU contention: 3 of 3 runs.** Late-quarter throttling was **42%, 51% and 42%**, against 33% in the control and 31% in F1. App CPU was 3.20–3.23 cores, against 3.15. App heap was 477–482 MiB, against 41 MiB.
  - **Latency: 1 stall, 1 bump, 1 clean.**
    - First run: a 2-second stall at p95 1,340 ms.
    - Repeat 1: p95 up to 497 ms, with late p95 73 ms against 63 ms in the control.
    - Repeat 2: no visible latency effect.
    - None of the runs had a failed request.
  - **Pipeline: 3 of 3.** 452–455 export failures per run, Alloy refusing 10.6–11.2k points/s.
- **Fix F1 proven (SDK allow-list View, `make fix-app`).** The app *still adds* `response_time` and 5 GUIDs, but the View drops them before export. Every signal returned to control level:
  - 8.2k points / 1.2 MB per export, 0 failures
  - Alloy healthy
  - heap 41 MiB
  - throttling 31%
  - p95 63 ms
- **What we can claim:** high-cardinality labels make the running app pay for its metrics in CPU and memory **every time** the agent pushes back. That cost sometimes surfaces as latency stalls (2 of 5 runs, one with timeouts). It doesn't reliably cause outages at these levels. A tag allow-list in the SDK removes the cost entirely.
- **L4 app degradation: seen once, not reproduced.**
  - **First L4 run** (constrained agent, a constant 1,650 req/s = 70% load): **all 4 replicas went down for about 10 seconds**. p95 reached 3s, there were 2,205 timeouts, and pods were pinned at their 1-core CPU limit while requests alone needed about 70% of it. Export pressure started 2 minutes before the stall, and the control was clean. Re-analysis says CONFIRMED.
  - **L4 repeat**, with identical settings: **0 errors**, flat latency. The pipeline-side pressure was the same or stronger (330 export failures, 56 retries, Alloy refusing up to 18.6k points/s and OOMKilled 6×, app heap 477 MiB).
  - **New GC data** from the repeat: GC paused at most **3.2%** of wall time, so GC isn't a major factor.
  - **The environment is noisy**: the repeat *control* had 17% early throttling and a 377 ms p95 spike with no metrics pressure.
  - **Conclusion so far:** metrics back pressure *can* coincide with an outage of the live app, but at L4 it isn't systematic (1 of 2 runs). More runs, or a harder level, are needed to measure how often it happens.
- **Cardinality → back pressure on the agent: reproduced at every level (L1–L5).**
  - With `response_time` as a float label, every replica reaches the OTel SDK cap (200k series) and pushes **35–67 MB every 10s**.
  - Exports hit the 8s timeout and fail or retry.
  - **Grafana Alloy was OOMKilled 4–6 times per run**, on both the normal (1 GiB) and constrained profiles.
- **With the normal agent, the app was never harmed (L1–L3).** The normal Alloy *crashes*, after which exports fail instantly and the app's export work actually gets *lighter*. The constrained agent *refuses and slows* (503s), so the app retries and keeps that work going: the only condition under which an outage was seen.
- **Memory pressure is real but capped.** The app's .NET heap rose to **476–488 MiB (83–85% of the 576 MiB GC limit)**, then leveled off when the SDK's 200k-series cap was reached. A run with the cap applied to every instrument (run A) did exhaust the heap: `OutOfMemoryException` and a total outage, **with no `OOMKilled` visible in Kubernetes**.
- **The pipeline does not self-heal.** After L1–L3, Alloy kept crash-looping until the app pods restarted. Apps holding cumulative series keep re-sending huge payloads.
- **Histogram finding (from F1):** the bucketed histogram keeps response-time visibility, but its p95 read **89 ms against the true 63 ms**. Most requests fall in the 50–100 ms bucket, so interpolation overestimates. Bucket boundaries have to be placed around the latencies and SLOs you care about, or exponential histograms used. Details: [proving-the-fix.md](proving-the-fix.md).
- **F5 (latency band label) passed:** about 2× control points, 0 export failures, no app impact. It's a safe way to keep latency *as a label*.
- **Next:** better histogram buckets, then optionally fix-run F2 and the F3 step-down. See [next-steps.md](next-steps.md).

## How we recreated it, and how it shows impact on the running app

In this repo, `transactions-api` plays the part of the production app. It serves live HTTP transactions with a 3s client timeout, and it ships its metrics the way production does: OTel SDK → Alloy agent on its own node pool → Prometheus.

### The causal chain we're testing

```
bad labels in app code ──► more series in the app's OTel SDK ──► bigger export every 10s (built in the app pod)
        │                                                              │
        │                                  agent can't keep up ◄───────┘
        │                                  (refuses / slows / OOMs)
        ▼                                          │
app pod spends CPU + heap on metrics ◄── retries, payloads held in memory
        │
        ▼
less CPU left for requests ──► throttling ──► higher latency ──► stalls / timeouts
```

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
| Agent push-back | exporter failures and retries; Alloy's refused-points metric | 0 → **452 failures / 45 retries**; Alloy refusing up to 11k points/s, OOMKilled 5× |
| App memory | .NET GC heap, polled from each pod | 41 → **482 MiB** (84% of the GC limit) |
| App CPU contention | the pod's cgroup `cpu.stat`: share of time throttled at its 1-core limit | **33% → 42%** (last quarter) |
| Live traffic | the load driver's own clients | p95 **63 → 87 ms** (last quarter); a **2s stall at p95 1,340 ms**; 0 failed requests |

### Other explanations, and why they were ruled out

- **Too much load:** requests alone needed about 3.17 of the 4 CPU cores (about 1.67 ms of CPU per request, measured in each run). The control ran the same load cleanly.
- **Prometheus or Grafana stealing CPU:** `PROTECT_PROM=1` drops the high-cardinality series in Alloy before remote write, so Prometheus sees about the same load in both runs (head series: 125k in the control, 109k in the hypothesis).
- **GC pauses:** at most 3.6% of wall time, about 0.15% on average.
- **Background noise on the nodes:** every control was clean. For L5, the app nodes were also given 4 CPUs instead of 3 to reduce that noise.

### What it does and doesn't show

**Shown:**

- High-cardinality labels make the running app pay for its own metrics in CPU and memory.
- When the agent pushes back (refuses or slows), that cost competes with live requests and makes the app measurably slower: +9 points of throttling and +24 ms p95 at 80% load.
- That cost also makes stalls more likely: 2 of 3 constrained-agent hypothesis runs.

**Not (yet) shown:**

- That outages are *reliable*: only one run (L4) had timeouts.
- That this is what happened in production. That depends on the production facts in [next-steps.md](next-steps.md), decision 5: agent sizing, OTel retry setting, cardinality limit and CPU headroom.

**Depends on conditions:** with a generously sized agent that just crashes (L1–L3), exports fail fast and the app was never harmed.

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

---

## 1. Hypothesis

High-cardinality metric labels (here, `response_time` as a float label, plus GUID-style labels appended by observability tooling) make the OpenTelemetry SDK hold and export ever-larger payloads. When the agent can't keep up, the resulting back pressure costs the app pod CPU and/or memory. That competes with live request handling and degrades response times.

It has two parts, and they're tested separately:

| Part | Question | Status |
|---|---|---|
| **A** | Does cardinality create back pressure on the metrics pipeline (agent)? | ✅ Reproduced (L1, L2) |
| **B** | Does that back pressure degrade the live app? | ✅ **CPU contention: 3 of 3 L5 runs** (+9 to +18 points of throttling against control). ⚠️ **Latency: intermittent** (a 10s outage in 1 of 2 L4 runs; a 2s stall in 1 of 3 L5 runs). ❌ Never with the normal agent (L1–L3). ✅ Removed by fix F1 |

## 2. Setup

| Component | Configuration |
|---|---|
| Cluster | k3d on Docker Desktop (8 GB VM). Control-plane node, **2 app-pool nodes** (2.25 GB each, capped at **3 real CPUs** for L1–L4 and **4 CPUs from L5**), **1 agent-pool node** (1.25 GB, 2 CPUs, tainted, Alloy only) |
| App | .NET 10 `transactions-api`, **4 replicas**, each **1 CPU / 768 Mi** limit. .NET GC heap hard limit = 75% of the container limit = **576 MiB** |
| Simulated pods | 50 virtual `pod` label values per replica = **200 pods** |
| Request cost | `CPU_WORK_ITERATIONS=1000` ⇒ **≈1.7 ms CPU per request** ⇒ capacity **≈2,350–2,410 req/s**, measured in every report and limited by the 4 × 1-core CPU limits |
| OTel SDK | 1.21, **cumulative** temporality, OTLP/HTTP, export **every 10s**, timeout **8s**, `OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY=in_memory` |
| Cardinality limit | `OTEL_CARDINALITY_LIMIT=200000`, **scoped to `http.transactions` only** (`OTEL_CARDINALITY_LIMIT_SCOPE=transactions`) |
| Metric | `http_transactions_total{consume_type(4), state(4), product(10), pod(200), [response_time], [GUID labels]}` |
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

**Mechanism (most likely, not fully proven).** While the constrained agent was alive but refusing or slow, each replica was retrying multi-MB exports and serializing up to 67 MB payloads, plus garbage-collecting those large buffers. All on the same 1-core budget as request handling, so the pods hit their CPU limit and requests queued past the 3s timeout. GC pause metrics travel through Alloy and were lost when it failed, so GC's share is unmeasured.

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

- **Before:** exports had been failing fast (about 25–80 ms) since about t=330s. Alloy was alive (about 50 MiB) but rejecting.
- **t≈972s:** in-flight requests rose from about 70 to 1,650.
- **t=974–979s:** **all four replicas** were at 0.96–1.01 cores, throttled 67–96%.
- **During the stall:** the two replicas that exported at t=976 took 1,057 ms and 1,240 ms to do it, against about 30 ms just before. The exports at t=981–985 took 480–750 ms.
- **By t≈983s:** latency had recovered.

**Interpretation:**

- The stall happened while every replica was carrying the metrics overhead. The 200k-point payload is still built every 10 seconds even when the agent rejects it, so headroom was about 20%. Something then pushed all four replicas over the limit at once.
- The slow exports *during* the stall may be a symptom of the CPU contention rather than its trigger. They don't show that the exports started it.
- The **steady-state** difference is the stronger evidence: +9 points of throttling and +24 ms of p95 in the last quarter of the run, at identical load, with a clean control.

### L5 repeats and fix runs F1 and F5 (same rate, duration and agent; the L5 control is reused)

| | Control | Hypothesis (1st) | Repeat 1 | Repeat 2 | **F1: allow-list fix** | **F5: latency band** |
|---|---|---|---|---|---|---|
| Labels the app adds | bounded | + `response_time`(6dp) + 5 GUIDs | same | same | **same** (dropped by the SDK View) | `response_time` as **5 bands**, no GUIDs |
| Client failures | 0 | 0 | 0 | 0 | 0 | 0 |
| Worst 1-s p95 | 177 ms | **1,340 ms** (2 s ≥ 1 s) | 497 ms | 169 ms | 182 ms | 164 ms |
| Seconds with p95 > 250 ms | 0 | 7 | 3 | 0 | 0 | 0 |
| p95 early → late | 63 → 63 | 63 → **87** | 64 → **73** | 63 → 64 | 63 → 63 | 63 → 63 |
| **Throttling early → late** | 34 → 33% | 34 → **42%** | 42 → **51%** | 34 → **42%** | 31 → 31% | 30 → 21% |
| App CPU, late quarter | 3.15 | 3.20 | 3.23 | 3.20 | 3.16 | 3.11 |
| App heap (peak) | 41 MiB | 482 MiB | 481 MiB | 477 MiB | **41 MiB** | 49 MiB |
| Export points / payload | 8.2k / 1.2 MB | 200k / 67 MB | 200k / 66 MB | 200k / 67 MB | **8.2k / 1.2 MB** | 15.8k / 2.8 MB |
| Export failures / retries | 0 / 0 | 452 / 45 | 455 / 68 | 455 / 56 | **0 / 0** | 0 / 2 |
| Alloy | healthy | refusing 11.2k/s, 5 restarts | refusing 10.8k/s, 1 restart | refusing 10.6k/s | **healthy, 0 refused** | healthy (brief refusals, up to 882 points/s) |
| Verdict | healthy | CONFIRMED (stall) | NOT CONFIRMED (Alloy restarted) | NOT CONFIRMED | healthy | healthy |

**Reading it:**

- **CPU contention is systematic.** Throttling was higher than control in all 3 hypothesis runs, by 9–18 points, at identical load.
- **Latency impact is intermittent.** Whether the extra contention shows up as latency depends on timing: it needs replicas to hit their CPU limit together.
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
- **Cost:** 7,680 series in Prometheus in total (160 label combinations × 12 × 4 replicas).

### L1 → L5 trend (hypothesis runs)

| | L1 | L2 | L3 | **L4** | **L5** |
|---|---|---|---|---|---|
| Load | 1,400 req/s (60%) | 1,400 req/s (60%) | 1,650 req/s (70%) | 1,650 req/s (70%) | 1,900 req/s (80%) |
| Extra labels | — | 3 GUIDs | 5 GUIDs | 5 GUIDs | 5 GUIDs |
| Agent | normal | normal | normal | **constrained** | **constrained** (nodes at 4 CPUs) |
| Peak export payload | 34.9 MB | 67.1 MB | 67.06 MB (ceiling) | 67.0 MB | 67.0 MB |
| Export failures / retries | 54 / 18 | 94 / 34 | 259 / 13 | 334 / **67** | 452 / 45 |
| Alloy | OOMKilled 4× | OOMKilled 5× | OOMKilled 4× + crash loop | **refusing 10.7k pts/s**, then OOMKilled 6× | refusing 11.2k pts/s, OOMKilled 5× |
| Peak app heap | 153 MiB | 383 MiB | 476 MiB (83%) | 488 MiB (85%) | 482 MiB (84%) |
| Peak pod throttling (direct polls) | 52% | 56% | 100% | 95% (cAdvisor 33–64% during the stall) | 100% (all 4 replicas during the stall) |
| **App p95 / client errors** | flat / 0 | flat / 0 | flat / 0 | **3,000 ms / 2,205 timeouts** (first run); flat / 0 (repeat) | **1,340 ms stall (2 s) / 0**; late-quarter p95 87 vs 63 ms in control |

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

**First observation (idle, before any test):** with the constrained agent (250m CPU / 384 MiB) and 300 pods:
- Alloy refused about **29k points/s** and accepted about 1k/s.
- About 55% of fleet exports failed.
- **The real replicas' metrics stopped reaching Prometheus too.** Aggregate overload blinds monitoring for *every* pod, not only the ones causing it.

**Runs:** the P300 control (no fleet) and the P300 fleet run, at 1,900 req/s for 20 min with the constrained agent. Results to follow.

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
5. **The agent is the first casualty.** Even a 1 GiB Alloy with no CPU limit OOMs on 4 × 35–67 MB pushes every 10s. A constrained agent (L4/L5) will fail sooner, and refuse data with 503s. That produces more retries, which means more app memory.
6. **Failures are sticky.** Because the app keeps cumulative state, the agent can't recover until the app restarts (see "After L1" above).
7. **Payload size scales with label bytes, not just series count.** After the SDK cap is reached, the series count stops growing, but GUID labels still nearly double each export (≈175 → ≈335 bytes per point).

8. **The SDK cardinality cap bounds the app's memory.** At L3 the heap leveled off at about 470 MiB once each pod reached 200k series. The higher the limit, the higher that ceiling, and above about 576 MiB the app fails (see run A). The limit has two effects: it reserves heap at startup, and it caps the growth later.
9. **Exports appear capped at about 64 MiB per request.** L2 and L3 peaked at the same 67.06–67.10 MB (64 MiB = 67,108,864 B) despite different label sizes. Larger exports seem to fail before they're sent (either the exporter's buffer limit or Alloy's 64 MiB `max_request_body_size`). The result is silent metric loss, not pressure on the app.
10. **Agent failure reduces the app's export cost.** With Alloy crash-looping, exports fail instantly (connection refused), so late-run export time *dropped* (L3: 744 → 17 ms). In OTel .NET, push back from a *dead* agent turns into data loss, not app CPU. A *slow or refusing* agent (L4's constrained profile, with 503s and retries) is the case that keeps payloads in app memory.

11. **The agent's failure mode decides whether the app gets hurt.** A *crashing* agent (normal profile, L1–L3) shields the app: exports fail instantly. An agent that *refuses or slows* (constrained profile, L4, which behaves like a memory_limiter, throttling or a slow network) keeps the app retrying and serializing big payloads on its own CPU. **That caused the L4 outage.** In production, a struggling-but-alive collector is the dangerous case.
12. **Synchronized pods fail together.** Replicas started at the same time (a rollout, or this reset) export on aligned timers, so the heavy export and GC cycles hit every pod at once, taking out the whole Service rather than one pod.

## 7. What to check in production

Possibly relevant to the 2 clusters × 75 pods with degraded response times:

- [ ] **OTel cardinality limit:** is it raised (`SetCardinalityLimit` / a View with `CardinalityLimit`), and for which instruments? A high limit on many instruments means a large heap reserved at startup.
- [ ] **Temporality:** cumulative or delta? Cumulative keeps every series forever (until restart).
- [ ] **Export interval and retry settings** (`OTEL_METRIC_EXPORT_INTERVAL`, `OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY`).
- [ ] **Which labels the observability team appends, and their cardinality:** anything per-request (IDs, GUIDs, URLs, durations as labels)?
- [ ] **.NET heap against 75% of the pod memory limit:** `dotnet_gc_last_collection_heap_size_bytes`, or `process.runtime.dotnet.gc.heap.size`.
- [ ] **GC pause time and gen2 collections:** a large heap means longer gen2 pauses, which add directly to request latency. *Not yet measured in these reports; worth adding.*
- [ ] **Pod restarts with exit `Error` after liveness-probe failures**, not `OOMKilled`.
- [ ] **Agent (Alloy or Collector) restarts and OOMKills, and OTLP request sizes.**
- [ ] **CPU throttling** (`container_cpu_cfs_throttled_periods_total`) if pods run close to their CPU limits.

## 8. Next steps

Pick-up notes, cluster state and the open decisions are in [next-steps.md](next-steps.md).

1. ~~Fix the soak verdict~~ **Done.** A degradation during a soak, with export pressure first, contention, no load saturation, **and a clean control at the same rate, duration and agent**, now means CONFIRMED. Stored runs can be re-analyzed (`POST /api/runs/{id}/reanalyze`). Re-analysis: L4 → CONFIRMED; L1–L3 unchanged; the ramp false positive (run C) is no longer CONFIRMED.
2. ~~Record GC pauses~~ **Done.** The driver now records GC pause % and gen2 collections in its direct pod polls, and reports chart them. That applies from the next run.
3. ~~Repeat L4~~ **Done: not reproduced** (0 errors). The effect on the app is intermittent at this level.
4. **Next experiments**, to tell whether the effect is real or chance:
   - **More repeats of L4 hypothesis** (2–3 more) to estimate how often the outage happens.
   - **L5** (80% load = 1,900 req/s, 20 min, constrained). Less CPU headroom (≈15%) makes any metrics overhead more likely to show.
   - **Isolate the environment**: pin Prometheus off the app nodes, or give the nodes more CPU. This reduces background contention bursts, so any remaining effect can be attributed to the metrics.
   - **Only once degradation is reliable**, step labels back down (`response_time` precision 6 → 3 → 2 → 1 → off, then the GUID labels) to find the way back to normal.
5. **Optional:**
   - L5 (80% load, 20 min)
   - stagger pod starts to test the "synchronized pods" effect
   - a delta-temporality run
   - OTel's default cardinality limit (2,000) as a mitigation run

## 9. Reports

All reports are in `./reports/` (gitignored). Refresh them with `make reports`, and open one with `open reports/<file>.html`.

| Run | Report file |
|---|---|
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
