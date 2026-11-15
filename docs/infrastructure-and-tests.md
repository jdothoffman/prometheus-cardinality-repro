# Infrastructure and tests

Exact stack, layout, and test procedures for this cardinality repro. For the incident narrative and how to interpret results, see the [README](../README.md).

## Pipeline

```
k6 or transactions-driver  ──HTTP──▶  transactions-api ×4   ──OTLP/HTTP every 10s──▶  Grafana Alloy  ──remote_write──▶  Prometheus  ──▶  Grafana
                                      (.NET OTel SDK)                                  (agent pool)                     (monitoring)
k6 ──remote_write (own latency)──────────────────────────────────────────────────────────────────────────────────────────▶ Prometheus
```

Optional: `transactions-fleet` on a separate node pool pushes additional OTLP load into Alloy (`make fleet-on`). It simulates 300 pods, each with its own SDK pipeline and export, so Alloy sees a production-sized fleet.

Results and their interpretation: [experiment-log.md](experiment-log.md). Pick-up notes: [next-steps.md](next-steps.md).

## Versions

| Component | Version / pin |
|---|---|
| Cluster | k3d → k3s `rancher/k3s:v1.36.6-k3s1` (`cluster/k3d-config.yaml`) |
| kube-prometheus-stack | Helm chart **93.3.0** (`Makefile` `KPS_VERSION`) |
| Grafana Alloy | Helm chart **1.15.0** (`ALLOY_VERSION`); app image Grafana Alloy v1.21.x |
| App / driver / fleet | .NET **10** (`net10.0`), image `transactions-api:local` |
| OpenTelemetry .NET | **1.21.0** (Hosting, AspNetCore, Runtime, OTLP exporter) |
| k6 | `grafana/k6:1.8.3` |

All of the above are free / open source.

## Cluster and node pools

Cluster name: `cardinality` (kubectl context `k3d-cardinality`).

| Node | Pool label | Memory / CPUs | What runs there |
|---|---|---|---|
| `k3d-cardinality-server-0` | control plane | 1536m RAM (about 94% used) | k3s; `transactions-driver`; **Prometheus** (as scheduled 2026-11-15) |
| `k3d-cardinality-agent-0/1` | `pool=app` | 2304m RAM each, **3 CPUs** each by default; **4 CPUs since L5** (`APP_NODE_CPUS=4 make node-cpus`) | `transactions-api`, k6; also Grafana, kube-state-metrics, Alertmanager, the Prometheus operator, CoreDNS and metrics-server (unpinned, so they share CPU with the app: a source of background noise) |
| `k3d-agentpool-0` | `pool=agent`, taint `pool=agent:NoSchedule` | 1280m RAM, **2 CPUs** | **Grafana Alloy only** |
| `k3d-fleetpool-0` (optional) | `pool=fleet`, taint `pool=fleet:NoSchedule` (applied with `kubectl taint` by `make fleet-node`: passing `--node-taint` to `k3d node create` duplicated it and crash-looped the kubelet) | 1792m RAM, **2 CPUs** | `transactions-fleet` |

These nodes add up to about 9 GB of memory limits on a Docker Desktop VM of 8 GB. That works because limits aren't all used at once, but there's little headroom. A VM of 12 GB or more is more comfortable.

Bring-up:

```sh
make prereqs   # brew install k3d helm
make all       # up + monitoring + agent + app
make ui        # :8080 console, :3000 Grafana, :9090 Prometheus
```

## Namespaces and components

### `demo` — application under test

| Workload | Role | Replicas | Resources | Notes |
|---|---|---|---|---|
| `transactions-api` | HTTP API that emits high-cardinality metrics | 4 | limit **1 CPU / 768Mi** | `APP_ROLE=api`, scheduled on `pool=app` |
| `transactions-driver` | Load-test console + open-model load generator + HTML reports | 1 | request 500m CPU, limit **768Mi** | `APP_ROLE=driver`, on control-plane node; PVC `transactions-driver-data` (1Gi) |
| `transactions-fleet` (optional) | Extra simulated pods pushing OTLP | default 2 × `FLEET_PODS=150` | limit **1 CPU / 768Mi** | `APP_ROLE=fleet`, on `pool=fleet` |
| `k6-load` Job (optional) | Constant-rate CLI load | 1 | — | Alternative to the UI driver |

Services:

- `transactions-api` — ClusterIP for normal traffic
- `transactions-api-headless` — per-replica stats / label control (`publishNotReadyAddresses: true`)
- `transactions-driver` — console UI
- `transactions-fleet-headless` — fleet `/admin/stats`

Driver RBAC (read-only evidence + limited reset):

- ClusterRole: get/list `pods`, `pods/log`, `events`
- Restart `transactions-api`; patch Alloy Deployment; delete `prometheus-kps-prometheus-0`

Default app env (from `k8s/app.yaml`):

| Variable | Default | Purpose |
|---|---|---|
| `CARDINALITY_MODE` | `bounded` | Bounded labels vs unbounded (`response_time`) |
| `VIRTUAL_PODS` | `50` | 4 × 50 ≈ **200** pod label values (`make prod-pods` → `1`) |
| `CPU_WORK_ITERATIONS` | `1000` | ≈1.7–2.4 ms CPU/request → ~**2350 req/s** capacity |
| `OTEL_CARDINALITY_LIMIT` | `200000` | SDK series cap (production-like; SDK default is 2000) |
| `OTEL_CARDINALITY_LIMIT_SCOPE` | `transactions` | Cap only `http_transactions_total` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://alloy.alloy.svc.cluster.local:4318` | Alloy OTLP/HTTP |
| `OTEL_METRIC_EXPORT_INTERVAL` | `10000` | Push every 10s (background) |
| `OTEL_METRIC_EXPORT_TIMEOUT` | `8000` | Per-export timeout |
| `OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY` | `in_memory` | Retries hold payloads in app memory |
| `OTEL_ALLOWED_TAGS` | unset | Fix F1 (`make fix-app`): an SDK View keeps only these tag keys |
| `APPENDED_LABELS` | `0` | 0–8 identity labels (k8s node, pod uid, container id, …) on every series (`make appended-labels N=…`) |

Fleet env (`k8s/fleet.yaml`): `FLEET_PODS=150` per replica, `FLEET_RATE_PER_POD=6`, `FLEET_RESPONSE_TIME=band` (`off` or 0–6 decimals), `FLEET_EXTRA_SERIES=100`, `FLEET_CHURN_SECONDS=0`, `FLEET_APPENDED_LABELS=0`. `make fleet-on` re-applies the manifest, so run `make appended-labels` after it.

### `alloy` — metrics agent

| Item | Value |
|---|---|
| Chart | `grafana/alloy` 1.15.0, release name `alloy` |
| Schedule | `pool=agent` + toleration for that taint |
| Ports | OTLP gRPC **4317**, OTLP HTTP **4318** |
| Pipeline (unguarded) | `otelcol.receiver.otlp` → `batch` → `exporter.prometheus` → `prometheus.remote_write` → Prometheus write API |
| Receiver body limits | **64MiB** (HTTP and gRPC), raised from Alloy defaults |

Agent profiles (`make`):

| Target | Resources | Config | Effect |
|---|---|---|---|
| `make agent` / `agent-normal` | requests 200m/256Mi, limit **1Gi** (no CPU limit) | `config.alloy` or `config-protect.alloy` | Keeps up under moderate load |
| `make agent-constrained` | limit **250m CPU / 384Mi** | `config-constrained*.alloy` + `values-constrained.yaml` | `memory_limiter` refuses (HTTP 503) early in a run, then Alloy OOMs and crash-loops or sits idle. It was intended to cause app retries and back pressure, but during the L4/L5 stalls it was crashed or idle ([review](review-2026-11-15.md) 3.4) |
| `make guard-agent` | same as normal | `config-guarded.alloy` | memory_limiter + drop series with `response_time` |
| `PROTECT_PROM=1` | — | `*-protect.alloy` | Drops high-cardinality series (`response_time` / GUID labels) **and the simulated fleet's series** (`otel_scope_name` `Transactions.Api.sim.*`) before Prometheus so Grafana stays usable |

### `monitoring` — Prometheus + Grafana

| Item | Value |
|---|---|
| Chart | `prometheus-community/kube-prometheus-stack` **93.3.0**, release `kps` |
| Prometheus | remote-write receiver **on**; scrape 10s; retention **6h**; WAL compression; storage **emptyDir** |
| Default resources | requests 250m/512Mi, limit **1Gi** memory |
| Grafana | admin / admin; dashboards via sidecar + `monitoring/dashboards/` |

Prometheus profiles:

| Target | Behavior |
|---|---|
| `make profile-oom` (default) | 1Gi limit → OOMKill → CrashLoopBackOff on WAL replay |
| `make profile-starvation` | no memory limit, `nodeSelector: pool=app` → noisy neighbour on the app pool |

Dashboards:

- **Cardinality Repro** — overview
- **Cardinality Deep Dive** — flow, cardinality, Prometheus, app, Alloy (incl. export path)

## The metric under test

`http_transactions_total{consume_type, state, product, pod, [response_time], …}`

| Label | Cardinality | Notes |
|---|---|---|
| `consume_type` | 4 | api, web, mobile, batch |
| `state` | 4 | completed, pending, declined, failed |
| `product` | 10 | checking, savings, … |
| `pod` | ~200 | 4 replicas × `VIRTUAL_PODS=50`. With `make prod-pods` (`VIRTUAL_PODS=1`): each replica's own name, as in production |
| `response_time` | unbounded | float string, e.g. 6 decimals: **the repro's bug**. Production already buckets response time. `band` = 5 fixed values (`0-50ms` … `3s+`) |
| optional GUID labels | 0–5 | `request_id`, `trace_id`, `session_id`, `correlation_id`, `tenant_id` |

Bounded baseline: **4 × 4 × 10 × 200 = 32,000** series.

Also recorded for comparison: `http_transaction_duration_seconds` (histogram, labels `consume_type, state, product`; pod via `instance`). Buckets are shaped around this app's latencies: `10, 25, 40, 50, 60, 70, 80, 100, 250 ms, 1, 3 s`. The earlier `5, 10, 25, 50, 100 ms, …` overstated p95 (89 against 63 ms). That's 11 boundaries = 14 series per label combination.

Exports use **cumulative** temporality: every interval re-sends all series the SDK still holds.

## Tests we run

Primary surface: **http://localhost:8080** (`make ui`) — open-model load (arrivals keep coming at the target rate regardless of latency). Client timeout default **3s**. Business outcomes (`completed` / `pending` / …) are HTTP 200; failures are timeouts, 5xx, connection errors, or in-flight drops.

### 1. Dry run (safe)

Console: **Dry run (safe)**.

- ~60s, ramp **20 → 100 req/s**, labels locked to **bounded**, auto-stop at **5%** errors
- Then end-to-end checks: driver→API, replicas + labels, Prometheus memory, Alloy OTLP / remote_write, metric freshness, Grafana dashboard present
- Does not push the system toward a crash

### 2. Baseline ramp (bounded)

Console: labels **Bounded** → **Start test**.

- Default ramp: start **50 req/s**, **+50** every **30s**, up to **2000** (configurable)
- Finds capacity / degradation milestones (p95 threshold, first 3s timeout, 5% / 50% errors)

### 3. Bug ramp (unbounded labels)

Same ramp with **+ response_time label** (and optionally GUID extras). Compare both runs in **Run history**.

CLI equivalent of “explode labels”:

```sh
make explode   # CARDINALITY_MODE=unbounded
make calm      # back to bounded
```

### 4. Hypothesis soaks (L1–L5) — metrics back pressure

Console panel **Hypothesis tests**. Each run:

1. **Reset everything** (restart API replicas, restart Alloy, wipe Prometheus)
2. Wait for ready + one export cycle
3. Constant-rate soak at a % of measured capacity (default capacity field **2350** req/s)
4. HTML report with **Metrics back pressure** verdict (`CONFIRMED` / `PARTIAL` / `NOT CONFIRMED` / load saturation)

| Level | Load | Duration | Hypothesis labels | Required agent |
|---|---|---|---|---|
| L1 | 60% capacity | 10 min | all 5 incl. `response_time` (6 dp) | `make agent-normal PROTECT_PROM=1` |
| L2 | 60% | 10 min | + **3** unique GUID labels | normal + protect |
| L3 | 70% | 15 min | + **5** unique GUID labels | normal + protect |
| L4 | 70% | 15 min | + 5 unique GUID labels | `make agent-constrained PROTECT_PROM=1` |
| L5 | 80% | 20 min | + 5 unique GUID labels | constrained + protect |

For each level, also run **Run control (bounded)** at the same rate/duration and compare in Run history. If a level passes (app healthy or not CONFIRMED), move up.

### 5. CLI / Make reproduction path

| Step | Command | Expectation |
|---|---|---|
| Constant load | `make load` (`RATE=300`, `DURATION=30m`) | ~32k series, flat |
| Explode | `make explode` | Series grow ≈ RATE/s; OTLP payloads and Alloy/Prom memory climb |
| Observe | `make watch` / `make status` | Prometheus and/or Alloy OOM |
| Non-blocking proof | `make agent-down` | App keeps serving; latency stays healthy |
| Recover Prom | `make reset-prometheus` | Deletes pod / emptyDir TSDB |
| Pod churn | `make churn` | ~20% of pod names change every 60s |

### 6. Mitigation comparison tests

While exploded (reset Prometheus if looping):

| Command | Layer | Effect |
|---|---|---|
| `make fix-app` | App | OTel View allow-list: `consume_type,state,product,pod` |
| `make cap-app` | App | SDK cap **2000** series/metric |
| `make guard-agent` | Agent | memory_limiter + drop `response_time` series |

Undo: `make unfix-app` / `uncap-app` / `unguard-agent`.

### 7. Fix runs (F1, F5)

Each is a hypothesis preset with a fix applied. They're compared against the same control.

- **F1:** `make fix-app`, then Run hypothesis. The SDK View drops the bad labels. Undo with `make unfix-app`.
- **F5:** Run hypothesis with `response_time` precision set to **band** and 0 GUID labels.

See [proving-the-fix.md](proving-the-fix.md).

### 8. Pod-label test with the 300-pod fleet (P300)

```sh
make fleet-node   # once per cluster
make prod-pods    # real replicas: own pod name (make lab-pods restores 50)
make fleet-off    # control: same labels (response_time band, 0 GUIDs), no fleet
make fleet-on     # fleet run: FLEET_REPLICAS (2) × 150 = 300 simulated pods → Alloy
make appended-labels N=4   # optional: identity labels on every series (real replicas + fleet)
```

A fleet run's report finds its control automatically: the same labels, with no fleet. The pass/fail criteria for the first P300 pair are in the experiment log, section 4b.

### 9. Cardinality-limit sweep (run A)

Bounded labels, `VIRTUAL_PODS=1`, the limit applied to every instrument at several values, a 10-minute soak each:

```sh
make prod-pods
kubectl --context k3d-cardinality -n demo set env deploy/transactions-api \
  OTEL_CARDINALITY_LIMIT=<n> OTEL_CARDINALITY_LIMIT_SCOPE=all
# then a prepared run (Reset everything + soak); the restarted pods keep the env
```

Measures the heap the SDK reserves at startup against the 576 MiB GC limit, and whether the app survives load. Restore with `OTEL_CARDINALITY_LIMIT=200000 OTEL_CARDINALITY_LIMIT_SCOPE=transactions`. Criteria and results: experiment log, section 4c.

### Reports

After each console run, the driver waits ~25s, collects evidence (terminations, events, crash logs, PromQL charts), and writes a self-contained HTML report.

A report also includes:
- **Why exports failed**, per failed HTTP attempt: `HTTP <code>`, `timeout / cancelled`, `connection: …`, or "not sent".
- **Throttling** as the most-throttled pod *and* the mean across pods.
- **p95** as a mean *and* a median.
- **Alloy restarts** from the Kubernetes `restartCount`.
- **The simulated fleet's size** and its export failures.

Copy reports locally with:

```sh
make reports   # → ./reports/ (gitignored)
```

Print the statistics the experiment log quotes (windowed mean throttling across pods, median p95, stalls, export failure reasons; `--minutes` adds a per-minute export timeline):

```sh
python3 tools/report-stats.py reports/<run>.html [--windows 420-960,1030-1200] [--minutes]
```

## Config file map

| Path | What it defines |
|---|---|
| `cluster/k3d-config.yaml` | k3d cluster, app-pool agents, memory |
| `Makefile` | versions, bring-up, profiles, load, mitigations |
| `k8s/app.yaml` | API + driver Deployments, Services, RBAC, PVC |
| `k8s/fleet.yaml` | Optional fleet Deployment |
| `k8s/load-k6.yaml` | k6 Job + script |
| `agent/config*.alloy` | Alloy pipelines (normal / constrained / guarded / protect) |
| `agent/values.yaml` | Alloy chart: agent pool, ports, 1Gi |
| `agent/values-constrained.yaml` | 250m / 384Mi overlay |
| `monitoring/values.yaml` | Prometheus 1Gi + remote-write receiver, Grafana |
| `monitoring/values-starvation.yaml` | Noisy-neighbour Prometheus overlay |
| `monitoring/dashboards/` | Grafana dashboard ConfigMaps |
| `app/` | .NET API, Driver, Report, Fleet, wwwroot console |

## Cleanup

```sh
make down   # k3d cluster delete cardinality
```
