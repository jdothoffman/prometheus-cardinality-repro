# Traces, and linking them to metrics without adding cardinality

**Added:** 2026-11-21. Tempo 2.10.10, Grafana 13.3.1 (kube-prometheus-stack 93.3.0), Prometheus 3.16, OTel .NET 1.21.

## The short answer

Yes. The link between a metric and a trace is an **exemplar**. When a request is traced, the OTel SDK keeps that request's `trace_id` next to the histogram bucket (or counter) the request was counted in. The exemplar travels with the sample as **side data, not as a label**:

- the series stays `http_transaction_duration_seconds_bucket{product, consume_type, state, le, …}`, and no new series is created;
- Prometheus keeps exemplars in **one fixed-size ring buffer** for all series (`exemplars.maxSize`, 100,000 here), and the oldest are overwritten first;
- Grafana draws each exemplar as a diamond on the graph, with a **View trace** link to Tempo.

The reverse link (trace → metric) needs nothing stored at all. Grafana maps a span's attributes to the metric's labels (`service.name` → `job`, `transaction.product` → `product`, …) and opens the metric slice the request belongs to.

```
                       ┌──── spans (10% sampled) ───────────────────────────────┐
transactions-api ──OTLP┤                                                        ▼
  OTel SDK             └──── metrics + exemplars {trace_id, span_id} ──▶ Alloy ─┬──▶ Tempo ◀──── TraceQL, trace by id,
                                                                                │              TraceQL metrics (Drilldown)
                                                                                └──▶ Prometheus ◀── PromQL, query_exemplars
                                                                                     (exemplar ring buffer)      │
                                                                       Grafana ◀────────────────────────────────┘
                              diamond on a latency graph ──"View trace"──▶ trace waterfall ──"Related metrics"──▶ p95 for that product
```

## What to put where

The rule that keeps cardinality bounded: **labels describe a group of requests; spans describe one request.**

| Data | Metric label? | Span attribute? |
|---|---|---|
| product, channel (`consume_type`), outcome (`state`), route | yes (bounded) | yes, with the same values, so trace → metric links work |
| exact response time | **no**: it's the histogram's value | yes (`transaction.response_time_ms`) and the span's duration |
| transaction id, request id, trace id, session id, tenant id | **never** | yes (`transaction.id`): searchable with TraceQL |
| pod | resource attribute → `instance` | resource attribute (`service.instance.id`) |

This is the cardinality incident turned around. The `response_time` float label and the GUID labels (`request_id`, `trace_id`, …) under test in this repo are exactly the per-request detail that belongs on a span. As span attributes they cost Tempo storage per sampled trace and Prometheus nothing.

## What exemplars cost (they're bounded, not free)

| Where | Cost | Bounded by |
|---|---|---|
| **App (OTel SDK)** | One exemplar reservoir per metric point: one slot per histogram bucket (12 for this histogram), one for a counter. Only requests inside a **sampled** span are offered (`ExemplarFilterType.TraceBased`). | **The series you already have.** With bounded labels this is small. With 200k series per pod it adds heap on top of the state the run-A sweep measured. Fix cardinality first, and don't turn exemplars on as a diagnostic in the middle of an explosion. |
| **Export payload** | At most one exemplar per bucket per series per export (a trace id, a span id, a value and a timestamp). | Series × buckets |
| **Alloy** | Passes exemplars through to remote_write (`send_exemplars = true`, also the default) | — |
| **Prometheus** | A fixed ring buffer shared by all series | `exemplars.maxSize` (100k). No new series. |
| **Tempo** | Storage and CPU per **sampled span** | Sampling ratio × request rate, not labels |

Tempo's `metrics-generator` runs only the `local-blocks` processor (k8s/tempo.yaml). That keeps recent spans inside Tempo so TraceQL metrics can be computed when a query runs. **Span metrics and service graphs are deliberately off.** Those processors remote-write series to Prometheus with span attributes as labels, which is a second way to recreate the problem. If you enable them later, keep their dimensions to bounded attributes, and use Tempo 3's `max_cardinality_per_label` override.

## Sampling: configurable, capped at 75%

The share of transactions that are traced is configurable, from 0 to **75% at most**. Every way of setting it enforces the cap:

| How | Setting | Above 75% |
|---|---|---|
| At startup | `TRACE_SAMPLE_RATIO` (with `TRACING_ENABLED=true`); `make traces-on RATIO=0.25` | `make` refuses; the app clamps to 0.75 and logs a warning |
| Live, all replicas | Load-test console → **Trace sampling** → *Apply sampling* (driver `POST /api/sampling?ratio=`) | The field stops at 75; the driver answers 400 |
| Live, one replica | `POST /admin/sampling?ratio=` on the replica; `GET` shows `{enabled, ratio, max}` | 400, and nothing changes |

- **Changing it live.** A change takes effect on the next request; there's no restart. The driver re-applies the console's choice to replicas that restart with the environment default, as it does for labels.
- **Tracing on or off.** `TRACING_ENABLED=false` (the manifest default, and `make traces-off`) builds no tracing pipeline at all, as in every earlier experiment run. With it off, a live change answers 409. `make traces-on` turns it on.

**The sampler** (`CappedRatioSampler`, [app/Sampling.cs](../app/Sampling.cs)) decides once per trace, when the request arrives:

| Incoming request | Decision |
|---|---|
| no `traceparent` (root) | The configured ratio, via the SDK's `TraceIdRatioBasedSampler` on the trace id this SDK just generated |
| `traceparent` with the sampled flag | The configured ratio, by **a random draw** |
| `traceparent` without the sampled flag | Drop: an upstream "no" can only lower the rate |
| child span in the same process | Follow the parent, so a trace is kept or dropped whole |

**The usual `ParentBased(TraceIdRatioBased)` sampler can't enforce a cap.**

- It trusts the caller's sampled flag. Any client sending `traceparent: 00-…-01` gets 100% of its requests traced.
- Deciding by trace id doesn't fix that, because there the caller picks the id. The SDK keeps any id whose first 8 bytes are below `ratio × 2⁶³`, so `00000000…` ids are always traced.
- **The cost of not trusting the flag:** an upstream-sampled trace can lack this service's spans when the draw says no.

**What the cap means.** It bounds the probability that a transaction is traced. The observed share scatters around the ratio over short windows. At 75% and 300 tx/s, one-minute readings stayed within 74–77%.

**Why exemplars always resolve.** The decision is made before the request runs, so every exemplar comes from a request whose trace was exported. Clicking a diamond never lands on "trace not found", except after Tempo's 6 h retention.

**The metrics still count every request.** Only the samples are a share, like Application Insights' sampled telemetry next to its pre-aggregated metrics. The overview dashboard plots the configured ratio, the observed share (`transactions_traced_total ÷ http_transactions_total`) and the cap.

**Trade-off: head sampling misses rare outliers.** At 10%, a rare slow outlier is traced only 1 time in 10.

- Tail sampling in Alloy (keep every error and every slow trace) fixes that, but it breaks two things above.
- The SDK must then send every span to the agent, which is more than the 75% cap.
- Exemplars are taken before the keep/drop decision, so some diamonds would point at dropped traces.
- Choose head sampling for exact links and a hard cap, or tail sampling for complete outliers.

**Tested on 2026-11-21.** These ran against one replica, with the trace ids looked up in Tempo. The table shows the share of requests traced:

| Configured | No `traceparent` (k6, 300 tx/s, 68,122 tx) | Sampled flag, random ids | Sampled flag, `00000000…` ids | Not-sampled flag |
|---|---|---|---|---|
| 10% | — | 11.7% (35/300) | 7.3% (22/300) | 0% (0/300) |
| 75% | **75.2%** (per replica 75.0–75.4%) | 75.9% (759/1,000) | 73.3% (733/1,000) | 0% (0/1,000) |

- **Values above the cap were refused.** 0.76, 0.9, 1e9, −0.1 and NaN got 400 from both the replica and the driver. `TRACE_SAMPLE_RATIO=0.9` at startup ran at 0.75 and logged a warning.
- **The cap's load on the pipeline.** At 75% and 300 tx/s the pipeline carried about 700 spans/s. The constrained Alloy peaked at 282 MiB of its 384 MiB and refused nothing, and k6 saw no failures.
- **Not measured:** the app's CPU and p95 cost at 75% under the L-runs' 80% load.

## Using it: the Application Insights equivalents

| Application Insights | Here (Grafana) |
|---|---|
| Performance → operation → **Drill into samples** (pick a duration range, get a list of requests) | **Traces Drilldown** (`/a/grafana-exploretraces-app`, ships with Grafana 13): *Duration* tab, drag across the slow part of the histogram, then *Traces* for the samples or *Comparison* for which attributes the slow requests have in common. Or the **Trace Correlation** dashboard: *Min duration* plus product/channel give the *Slow samples* list |
| **End-to-end transaction details** (waterfall) | The trace view: click a Trace ID (the dashboard opens it at the bottom of the page) or an exemplar's *View trace* |
| A point on a metric chart → the requests behind it | **Exemplar diamonds** on *Response time p95*, the heatmap and *Transactions / s* |
| Related items / back to the metric | *Related metrics* on each span: p95 and throughput for its product and channel, plus CPU throttling of the app pods |
| Failures blade | *Failed samples* on the dashboard (`status = error`), *Errors* in Traces Drilldown |
| Search by a custom property | TraceQL: `{ span.transaction.id = "…" }`, `{ span.transaction.product = "mortgage" && duration > 50ms }` |
| Logs inside the transaction view | **Not set up.** It needs Loki plus `tracesToLogs` on the Tempo datasource, with trace ids in the log records |
| Application Map | **Not set up.** It needs Tempo's service-graphs processor (bounded by pairs of services, but it writes to Prometheus) |

## Running it

```sh
make tracing                 # Tempo, namespace tracing (prefers the idle fleet node; never the app or agent pool)
make monitoring              # Prometheus exemplar storage + Grafana's Tempo datasource and links + dashboard
make agent-constrained PROTECT_PROM=1   # or any agent profile: they all carry the traces branch now
make app && make traces-on   # RATIO=0.1 by default, at most 0.75; then change it live in the console
make load                    # or the console's Start button
make ui                      # Grafana :3000 → Dashboards → Trace Correlation
```

If Grafana doesn't show the Tempo datasource after `make monitoring`, restart it (`kubectl -n monitoring rollout restart deploy/kps-grafana`). On 2026-11-21 the datasource sidecar's live reload got a 401.

Switches: `make traces-off` (back to the setting of every earlier experiment run), `make exemplars-off` (traces without exemplars, to measure exemplars on their own). **`make app` re-applies k8s/app.yaml, which has tracing off.**

## What was checked (2026-11-21, 300 req/s k6, 4 replicas, `VIRTUAL_PODS=1`, constrained + protect agent, 10% sampling)

- **The exemplar's trace resolves.** An exemplar on `http_transaction_duration_seconds_bucket{le="0.05", product="insurance", …}` with `trace_id=815fc807…` fetched from Tempo as `POST /api/transactions` (46 ms) → `transaction.validate` (1.6 ms) + `ledger.post` (42 ms).
- **No new series.** `count(http_transaction_duration_seconds_bucket)` was 7,152, the same as the number of distinct series over the previous 30 minutes. The trace id appears only under exemplar labels.
- **Bounded storage.** Prometheus held 1,097 exemplars against its cap of 100,000, appending about 34/s.
- **Volume.** 70 spans/s into Tempo for 253 transactions/s, which is 3 spans × about 9% of requests. Tempo used 95 MiB and 17m CPU; Alloy used 184 MiB.
- **Traces Drilldown's query path works.** TraceQL `quantile_over_time(duration, .95) by (span.transaction.product)` gave 62–64 ms per product, matching the histogram's p95 of about 63 ms.
- **Grafana.** The Tempo datasource is healthy (streaming on), the Prometheus datasource carries the `trace_id` → Tempo link, and Traces Drilldown 3.2.0 is enabled.
- **Not measured yet: the app-side cost of tracing and exemplars** (CPU, heap, p95) at the 80% load of the L-runs. It needs a pre-registered pair against the bounded noise floor (about 3 points of mean throttling and 0.5 ms of median p95): `traces-off` against `traces-on RATIO=0.1`, then `exemplars-off`.

## For production

- **.NET:** `.WithTracing(…)` with the OTLP exporter and a head sampler. Use `ParentBased(TraceIdRatioBased)` only if every caller is trusted; if the rate must have a ceiling, use a sampler like `CappedRatioSampler` (app/Sampling.cs) that doesn't let a caller's `traceparent` pick the outcome. Add plus `metrics.SetExemplarFilter(ExemplarFilterType.TraceBased)` (or `OTEL_METRICS_EXEMPLAR_FILTER=trace_based`). Record histograms **inside** the request's span, so `Activity.Current` is the sampled span. See app/Api.cs.
- **Alloy:** add `traces` to the OTLP receiver output and the processors, export to Tempo with `otelcol.exporter.otlp`, and keep `send_exemplars = true` on `prometheus.remote_write`. Exemplars survive `prometheus.relabel`.
- **Prometheus:** `--enable-feature=exemplar-storage` and `storage.exemplars.exemplars-limit` (`exemplars.maxSize` in the operator). Remote-write targets other than Prometheus (Mimir, Grafana Cloud) need exemplar ingestion enabled per tenant (`max_global_exemplars_per_user` in Mimir).
- **Grafana:** the Prometheus datasource's `exemplarTraceIdDestinations` (`trace_id` → the Tempo datasource uid), and `tracesToMetrics` on the Tempo datasource (monitoring/values.yaml).
- **Tempo:** this repo pins the 2.10 line for its single-binary simplicity. Tempo 3.x replaces ingesters and `local-blocks` with a new ingest path; migrate configs with `tempo-cli migrate config`.
