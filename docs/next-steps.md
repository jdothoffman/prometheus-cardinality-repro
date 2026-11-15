# Next steps: picking up the experiment

**Updated:** 2026-11-15, after L5. Results and evidence are in [experiment-log.md](experiment-log.md).

## Where we are

- **Reproduced, every time:** high-cardinality labels put back pressure on the metrics pipeline.
  - `response_time` as a float label, plus GUID labels, pushes every replica to the OTel SDK's 200k-series cap.
  - Each replica then sends 35–67 MB per export, every 10s.
  - Exports time out and retry, and Alloy is OOMKilled 4–6 times per run.
  - App heap reaches 83–85% of its GC limit.
- **Shown:** that pressure causes resource contention on the live app. **CPU throttling was higher than control in 3 of 3 L5 runs. Latency stalls were intermittent: 2 of 5 constrained-agent runs.** Every control was clean.

  | Level | Load | Hypothesis result | Control |
  |---|---|---|---|
  | L4 | 70% | 10 s outage, 2,205 timeouts | clean |
  | L4 repeat | 70% | no degradation | clean (some noise) |
  | **L5** | **80%** | **2 s stall** (p95 1,340 ms, 0 errors). Late-run throttling 42% vs 33%. | clean |
  | L5 repeat 1 | 80% | p95 bump to 497 ms; throttling 51% | (same control) |
  | L5 repeat 2 | 80% | no latency effect; throttling 42% | (same control) |
  | **L5 F1 fix** | 80% | same bad labels, dropped by the SDK View: **everything at control level** (throttling 31%) | (same control) |
  | **L5 F5 band** | 80% | `response_time` as 5 bands, no GUIDs: **healthy** (2× control points, 0 export failures) | (same control) |

  CPU contention is systematic. Latency impact is intermittent. A reliable *outage* is not shown. **The allow-list fix (F1) removes the contention entirely.**
- **Ruled out as the main cause:** GC (at most 3.2% pause).
- **Still possible:** background CPU contention in the cluster. A control run alone showed throttling bursts.

## State things were left in

| Thing | State |
|---|---|
| k3d cluster `cardinality` | **Stopped, not deleted.** Nodes, images and saved runs are kept. |
| Port-forwards (`make ui`) | Stopped |
| Agent profile | **constrained + protect** (Alloy 250m CPU / 384Mi, memory_limiter, `response_time`/GUID series dropped before Prometheus) |
| App | 4 replicas, `CPU_WORK_ITERATIONS=1000` (≈ 2,350 req/s capacity), bounded labels |
| Run history | On the driver's PVC (survives restarts). HTML copies are in `./reports/` (gitignored). |
| Node CPUs | App nodes at **4 CPUs** since L5 (`APP_NODE_CPUS=4 make node-cpus`). This is not the Makefile default of 3, so pass the variable again if you re-create the cluster. |
| Git | L5 repeat and F1 results in `docs/` are uncommitted (after PR #8). |

## Resume

```sh
cd ~/repos/grafanPromPoc
k3d cluster start cardinality
kubectl --context k3d-cardinality get pods -A     # wait until everything is Running (a few minutes)
make ui                                           # console :8080, Grafana :3000, Prometheus :9090
```

Then, in the console (http://localhost:8080):

1. Check the **Hypothesis tests** panel's agent check. It should say *constrained*.
2. Click **Reset everything** before the first run.
3. Use the level presets (L1–L5), with **Run control** first and then **Run hypothesis**.

Prometheus history may be gone after the restart. That doesn't matter, because each preset resets Prometheus anyway.

If the CPU caps on the nodes were lost (check with `docker inspect k3d-cardinality-agent-0 --format '{{.HostConfig.NanoCpus}}'`, which should give 3000000000), run `make node-cpus`.

## Decisions needed

### 1. Which experiment next? (main decision)

> **Update after the L5 repeats and F1:**
> - **Done:** the L5 repeats and F1.
> - **Result:** contention 3 of 3, latency stalls 1 of 3. The "≥2 of 3" bar is met for *contention* but not for *stalls*.
> - **The fix:** F1 removes everything.
> - **Recommended next:**
>   1. ~~F5 (latency band label)~~ **Passed:** about 2× control points, 0 export failures, no app impact.
>   2. Better histogram buckets: the current ones overstate p95 (89 ms against 63 ms).
>   3. F3 step-down, if you want to know which precision is "safe".
>   4. Bring the production questions (decision 5) to the observability team.
>
> **Earlier update after L5:** option C (more CPU per app node) is done, and L5 is done. **Recommended next: two more L5 hypothesis runs** (≈ 22 min each; L5 control can be reused). Run them as a single **Run hypothesis** in the console, or repeat that click. If at least one degrades, L5 meets the "≥2 of 3" bar, and you can move on to the fix runs in [proving-the-fix.md](proving-the-fix.md).

Original options (2026-11-14):

| Option | What it tells us | Time |
|---|---|---|
| **A. More L4 repeats** (2–3 more hypothesis runs) | How often the outage happens at 70% load | ≈ 18 min per run |
| **B. L5** (1,900 req/s = 80%, 20 min, constrained) | Whether less CPU headroom makes the effect reliable | ≈ 46 min per control + hypothesis pair |
| **C. Quiet the environment first** (see decision 2), then A or B | Whether the outage is caused by the metrics, or by metrics *plus* background noise | +15 min setup |

**Recommendation: C, then B.** With less background noise, any outage that remains is easier to attribute to the metrics. Then a harder level gives the effect a better chance of showing every time.

### 2. If quieting the environment: how?

- **More CPU per app node** (`APP_NODE_CPUS=4 make node-cpus`). This is the easiest option, and Docker has 15 CPUs. It lowers contention from the other pods on those nodes. It doesn't change the app's own 1-core limit, so it doesn't make the test easier for the app.
- **Pin Prometheus, Grafana and kube-state-metrics off the app pool.** The default monitoring profile doesn't pin them, so they can share nodes with the app. Pinning them to the server node puts them next to the load driver. Giving them a dedicated node uses more Docker memory (8 GB total).
- **Both.** This is the cleanest option.

**Recommendation: more CPU per app node first.** It's one command and can be undone.

### 3. What counts as "systematic"?

Agree on this *before* the next runs, so the results can't be read to fit. Suggested bar: an outage in **≥ 2 of 3 hypothesis runs**, with **every matching control clean**.

### 4. Step labels down now, or wait?

The step-down is now run F3 in [proving-the-fix.md](proving-the-fix.md).

The original plan was to step labels down once app degradation was reliable. But the **pipeline-side** damage is reliable already, so the step-down could be measured against pipeline metrics instead:

- export size
- export failures and retries
- Alloy OOMKills
- app heap

The order would be `response_time` 6 → 3 → 2 → 1 dp → off, then the GUID labels 5 → 0.

**Recommendation: do it now as well.** It answers the production question ("which labels are safe?") whether or not the app outage turns out to be systematic.

### 5. Facts to get from production (observability team)

The repro can only be as faithful as these settings. Each one changes which failure mode applies:

- Alloy (or other agent) **CPU and memory limits** and its **memory_limiter** settings. Does it get OOMKilled, or does it refuse with 503? Only *refusal* caused app pressure here.
- Whether the apps set **`OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY`**. Retries are what turn agent push-back into work for the app.
- The apps' **`CardinalityLimit`** or OTel View settings, and the export interval.
- **Which labels the observability team appends**, and where (SDK resource attributes, or added by the agent).
- Whether app pods share nodes with the agent, Prometheus or other heavy pods.

### 6. Housekeeping

- Commit `docs/experiment-log.md` and `docs/next-steps.md` via a branch and a PR. I can open it on request.
- Delete the merged remote branch `feature/stall-verdict-gc`.
- When the experiment is over, use `make down` to delete the cluster completely. Today's runs only stopped it.
