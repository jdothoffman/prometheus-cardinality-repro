1. ~~Fix the soak verdict~~ **Done.** A degradation during a soak, with export pressure first, contention, no load saturation, **and a clean control at the same rate, duration and agent**, now means CONFIRMED. Stored runs can be re-analyzed (`POST /api/runs/{id}/reanalyze`). Re-analysis: L4 → CONFIRMED; L1–L3 unchanged; the ramp false positive (run C) is no longer CONFIRMED.
2. ~~Record GC pauses~~ **Done.** The driver now records GC pause % and gen2 collections in its direct pod polls, and reports chart them. That applies from the next run.
# Experiment log: metrics pressure vs the live app

**Date:** 2026-11-14 · **Environment:** local k3d cluster (this repo) · **Status:** L1–L4 complete. **L4 reproduced app degradation (one run; repeat pending)**

## TL;DR

- **🎯 L4 reproduced the full hypothesis (once; repeat pending).** At a constant 1,650 req/s (70% load) with the **constrained agent**, the high-cardinality run took **all 4 replicas down for about 10 seconds**:
  - p95 went to 3s, with **2,205 client timeouts** and 61% failing at the worst second
  - readiness probes failed on every pod
  - pods were **pinned at their 1-core CPU limit and throttled 20–64%**, while requests alone needed about 70% of it
  - export pressure began about 2 minutes **before** the stall

  **The control (same load, same agent, bounded labels) had 0 errors.** The report originally said NOT CONFIRMED because of a gap in the soak rule (it only looked for late-run degradation). After the fix, **re-analysis says "Metrics back pressure: CONFIRMED — transient outage at constant load"**, with the matching control found automatically and confirmed clean.
- **Cardinality → back pressure on the agent: reproduced at every level (L1–L4).**
  - With `response_time` as a float label, every replica reaches the OTel SDK cap (200k series) and pushes **35–67 MB every 10s**.
  - Exports hit the 8s timeout and fail or retry.
  - **Grafana Alloy was OOMKilled 4–6 times per run**, on both the normal (1 GiB) and constrained profiles.
- **With the normal agent, the app was never harmed (L1–L3).** The normal Alloy *crashes*, after which exports fail instantly and the app's export work actually gets *lighter*. The constrained agent *refuses and slows* (503s), so the app retries and keeps that work going. **That's what turned pipeline pressure into app contention.**
- **Memory pressure is real but capped.** The app's .NET heap rose to **476–488 MiB (83–85% of the 576 MiB GC limit)**, then leveled off when the SDK's 200k-series cap was reached. A run with the cap applied to every instrument (run A) did exhaust the heap: `OutOfMemoryException` and a total outage, **with no `OOMKilled` visible in Kubernetes**.
- **The pipeline does not self-heal.** After L1–L3, Alloy kept crash-looping until the app pods restarted. Apps holding cumulative series keep re-sending huge payloads.
- **Next:**
  1. Fix the soak verdict so it catches transient stalls.
  2. Record GC pauses from direct pod polling (the Prometheus path was lost with the agent).
  3. **Repeat L4** to confirm it's reproducible.
  4. Then **step labels back down** to find the way back to normal.

---

## 1. Hypothesis

High-cardinality metric labels (here, `response_time` as a float label, plus GUID-style labels appended by observability tooling) make the OpenTelemetry SDK hold and export ever-larger payloads. When the agent can't keep up, the resulting back pressure costs the app pod CPU and/or memory. That competes with live request handling and degrades response times.

It has two parts, and they're tested separately:

| Part | Question | Status |
|---|---|---|
| **A** | Does cardinality create back pressure on the metrics pipeline (agent)? | ✅ Reproduced (L1, L2) |
| **B** | Does that back pressure degrade the live app? | ✅ **Reproduced at L4** (constrained agent): a 10s outage of all replicas. One run so far; repeat pending. ❌ Not with the normal agent (L1–L3) |

## 2. Setup

| Component | Configuration |
|---|---|
| Cluster | k3d on Docker Desktop (8 GB VM). Control-plane node, **2 app-pool nodes** (2.25 GB each, capped at **3 real CPUs**), **1 agent-pool node** (1.25 GB, 2 CPUs, tainted, Alloy only) |
| App | .NET 10 `transactions-api`, **4 replicas**, each **1 CPU / 768 Mi** limit. .NET GC heap hard limit = 75% of the container limit = **576 MiB** |
| Simulated pods | 50 virtual `pod` label values per replica = **200 pods** |
| Request cost | `CPU_WORK_ITERATIONS=1000` ⇒ **≈1.7 ms CPU per request** ⇒ capacity **≈2,350–2,410 req/s**, measured in every report and limited by the 4 × 1-core CPU limits |
| OTel SDK | 1.21, **cumulative** temporality, OTLP/HTTP, export **every 10s**, timeout **8s**, `OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY=in_memory` |
| Cardinality limit | `OTEL_CARDINALITY_LIMIT=200000`, **scoped to `http.transactions` only** (`OTEL_CARDINALITY_LIMIT_SCOPE=transactions`) |
| Metric | `http_transactions_total{consume_type(4), state(5), product(10), pod(200), [response_time], [GUID labels]}` |
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
| **L4** 🎯 | 70% (1,650 req/s) | 15 min | + 5 unique GUID labels | constrained (250m CPU / 384 Mi + memory_limiter) |
| L5 | 80% (1,900 req/s) | 20 min | + 5 unique GUID labels | constrained |

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

### L1 → L4 trend (hypothesis runs)

| | L1 | L2 | L3 | **L4** |
|---|---|---|---|---|
| Load | 1,400 req/s (60%) | 1,400 req/s (60%) | 1,650 req/s (70%) | 1,650 req/s (70%) |
| Extra labels | — | 3 GUIDs | 5 GUIDs | 5 GUIDs |
| Agent | normal | normal | normal | **constrained** |
| Peak export payload | 34.9 MB | 67.1 MB | 67.06 MB (ceiling) | 67.0 MB |
| Export failures / retries | 54 / 18 | 94 / 34 | 259 / 13 | 334 / **67** |
| Alloy | OOMKilled 4× | OOMKilled 5× | OOMKilled 4× + crash loop | **refusing 10.7k pts/s**, then OOMKilled 6× |
| Peak app heap | 153 MiB | 383 MiB | 476 MiB (83%) | 488 MiB (85%) |
| Peak pod throttling (direct polls) | 52% | 56% | 100% | 95% (cAdvisor 33–64% during the stall) |
| **App p95 / client errors** | flat / 0 | flat / 0 | flat / 0 | **3,000 ms / 2,205 timeouts** |

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

1. **Fix the soak verdict** to catch transient stalls. The rule should be: any degradation milestone during a soak, plus export pressure that started first, plus contention, plus no load saturation, **plus a clean control at the same level**, means CONFIRMED.
2. **Record GC pause % and gen2 collections in the driver's direct pod polls.** The Prometheus copies of those metrics are lost when the agent fails.
3. **Repeat L4** (constrained agent) at least once, to show the outage is reproducible.
4. **Then step labels back down** with the same L4 settings, re-running after each step, to find the way back to normal:
   1. `response_time` 6 → 3 → 2 → 1 dp → off
   2. GUID labels 5 → 3 → 0 (or pooled values)
   3. then `pod`, if needed

   Use **Reset everything** before each run.
5. **Optional:**
   - L5 (80% load, 20 min)
   - stagger pod starts to test the "synchronized pods" effect
   - a delta-temporality run
   - OTel's default cardinality limit (2,000) as a mitigation run

## 9. Reports

All reports are in `./reports/` (gitignored). Refresh them with `make reports`, and open one with `open reports/<file>.html`.

| Run | Report file |
|---|---|
| **L4 hypothesis** | `reports/20261114-193930-l4-hypothesis-1650-req-s-15m-constrained.html` |
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
