"""Generates monitoring/dashboards/traces.json ("Trace Correlation"): python3 tools/gen-traces-dashboard.py monitoring/dashboards/traces.json"""
import json, sys
PROM = {"type": "prometheus", "uid": "prometheus"}
TEMPO = {"type": "tempo", "uid": "tempo"}
SEL = 'product=~"${product:regex}", consume_type=~"${consume_type:regex}"'
TQL_SEL = ('resource.service.name="transactions-api" && kind=server'
           ' && span.transaction.product=~"${product:regex}" && span.transaction.consume_type=~"${consume_type:regex}"')
SELECT = " | select(span.transaction.state, span.transaction.product, span.transaction.consume_type, resource.service.instance.id)"
DRILL = ("/d/trace-correlation/trace-correlation?${__url_time_range}&${product:queryparam}&${consume_type:queryparam}"
         "&${min_duration:queryparam}&var-trace_id=${__value.raw}")
panels = []
def add(p): p["id"] = len(panels) + 1; panels.append(p); return p
def row(title, y): add({"type": "row", "title": title, "collapsed": False, "gridPos": {"x": 0, "y": y, "w": 24, "h": 1}, "panels": []})
def prom(ref, expr, legend, exemplar=True, **kw):
    t = {"refId": ref, "datasource": PROM, "expr": expr, "legendFormat": legend, "exemplar": exemplar, "range": True}
    t.update(kw); return t

row("Metrics: every diamond is one sampled request — hover it, then \"View trace\"", 0)
add({"type": "timeseries", "title": "Response time p50 / p95 / p99 (with exemplars)",
     "description": "From the bounded histogram http_transaction_duration_seconds. Exemplars are trace ids the OTel SDK kept for sampled requests, one per bucket per export; they are not labels, so they add no series.",
     "datasource": PROM, "gridPos": {"x": 0, "y": 1, "w": 16, "h": 9},
     "fieldConfig": {"defaults": {"unit": "s", "custom": {"lineWidth": 2, "fillOpacity": 0, "spanNulls": False}}, "overrides": []},
     "options": {"legend": {"displayMode": "list", "placement": "bottom"}, "tooltip": {"mode": "multi"}},
     "targets": [prom(r, f'histogram_quantile({q}, sum by (le) (rate(http_transaction_duration_seconds_bucket{{{SEL}}}[1m])))', f"p{int(q*100)}", exemplar=(r == "B"))
                 for r, q in (("A", 0.5), ("B", 0.95), ("C", 0.99))]})
add({"type": "text", "title": "How to drill in", "gridPos": {"x": 16, "y": 1, "w": 8, "h": 9},
     "options": {"mode": "markdown", "content": "\n".join([
        "**Metric → sample → trace**, like Application Insights' *Drill into samples*:",
        "",
        "1. **Diamonds** on the charts are exemplars: real sampled requests. Hover one and click **View trace**.",
        "2. **Samples** below lists sampled transactions slower than *min duration* (top bar) for the selected product/channel. Click a **Trace ID** to open its waterfall at the bottom of this page.",
        "3. In a trace, a span's **Related metrics** link goes back to p95 and throughput *for that product and channel*.",
        "4. For breakdowns and comparisons (*which attribute makes the slow requests slow?*), open [**Traces Drilldown**](/a/grafana-exploretraces-app/explore): select a duration range on its histogram, then **Comparison** / **Traces**.",
        "",
        "Needs `make tracing` and `make traces-on`. Only *sampled* requests (`TRACE_SAMPLE_RATIO`) have traces; the metrics count every request."])}})
add({"type": "heatmap", "title": "Response-time distribution (buckets/s, with exemplars)",
     "datasource": PROM, "gridPos": {"x": 0, "y": 10, "w": 12, "h": 9},
     "options": {"calculate": False, "yAxis": {"unit": "s", "axisPlacement": "left"}, "rowsFrame": {"layout": "auto"},
                 "color": {"mode": "scheme", "scheme": "Blues", "steps": 64, "exponent": 0.5, "fill": "dark-blue", "reverse": False},
                 "cellGap": 1, "exemplars": {"color": "rgba(255,120,0,0.9)"}, "tooltip": {"mode": "single", "yHistogram": True},
                 "legend": {"show": True}, "filterValues": {"le": 1e-9}},
     "targets": [prom("A", f'sum by (le) (rate(http_transaction_duration_seconds_bucket{{{SEL}}}[1m]))', "{{le}}", format="heatmap")]})
add({"type": "timeseries", "title": "Transactions / s by state (with exemplars)",
     "description": "Exemplars on a counter: one recent sampled request per series. Failed transactions' traces carry an error status.",
     "datasource": PROM, "gridPos": {"x": 12, "y": 10, "w": 12, "h": 9},
     "fieldConfig": {"defaults": {"unit": "reqps", "custom": {"lineWidth": 2, "fillOpacity": 8, "spanNulls": False}}, "overrides": []},
     "options": {"legend": {"displayMode": "list", "placement": "bottom"}, "tooltip": {"mode": "multi"}},
     "targets": [prom("A", f'sum by (state) (rate(http_transactions_total{{consume_type=~"${{consume_type:regex}}", product=~"${{product:regex}}"}}[1m]))', "{{state}}")]})

row("Samples: sampled transactions from Tempo (click a Trace ID)", 19)
def traceql_table(title, q, x, w):
    return add({"type": "table", "title": title, "datasource": TEMPO, "gridPos": {"x": x, "y": 20, "w": w, "h": 10},
        "fieldConfig": {"defaults": {}, "overrides": [
            {"matcher": {"id": "byName", "options": "traceID"},
             "properties": [{"id": "links", "value": [{"title": "Show this trace below", "url": DRILL}]}]}]},
        "options": {"showHeader": True, "cellHeight": "sm", "sortBy": [{"displayName": "Duration", "desc": True}]},
        "targets": [{"refId": "A", "datasource": TEMPO, "queryType": "traceql", "query": q, "limit": 50, "tableType": "traces"}]})
traceql_table("Slow samples (duration ≥ min duration)", "{" + TQL_SEL + " && duration >= $min_duration}" + SELECT, 0, 14)
traceql_table("Failed samples", "{" + TQL_SEL + " && status = error}" + SELECT, 14, 10)

row("Trace (end-to-end transaction)", 30)
add({"type": "traces", "title": "Trace ${trace_id}", "datasource": TEMPO, "gridPos": {"x": 0, "y": 31, "w": 24, "h": 14},
     "targets": [{"refId": "A", "datasource": TEMPO, "queryType": "traceql", "query": "${trace_id}"}]})

def query_var(name, label, metric):
    return {"name": name, "label": label, "type": "query", "datasource": PROM, "refresh": 2, "includeAll": True, "multi": True,
            "allValue": ".*", "current": {"text": ["All"], "value": ["$__all"]}, "sort": 1,
            "query": {"qryType": 1, "query": f"label_values({metric}, {name})", "refId": "PrometheusVariableQueryEditor-VariableQuery"},
            "definition": f"label_values({metric}, {name})"}
dash = {"uid": "trace-correlation", "title": "Trace Correlation", "tags": ["traces", "exemplars", "repro"],
        "timezone": "browser", "refresh": "30s", "schemaVersion": 39, "editable": True,
        "time": {"from": "now-30m", "to": "now"},
        "annotations": {"list": []},
        "links": [{"title": "Repro Overview", "type": "link", "url": "/d/repro-overview", "icon": "dashboard", "keepTime": True}, {"title": "Traces Drilldown", "type": "link", "url": "/a/grafana-exploretraces-app/explore", "icon": "external link"}],
        "templating": {"list": [
            query_var("product", "Product", "http_transaction_duration_seconds_bucket"),
            query_var("consume_type", "Channel", "http_transaction_duration_seconds_bucket"),
            {"name": "min_duration", "label": "Min duration", "type": "custom", "query": "0ms,25ms,50ms,80ms,100ms,250ms,1s",
             "current": {"text": "50ms", "value": "50ms"}, "options": []},
            {"name": "trace_id", "label": "Trace ID", "type": "textbox", "query": "", "current": {"text": "", "value": ""}}]},
        "panels": panels}
json.dump(dash, open(sys.argv[1], "w"), indent=2, ensure_ascii=False)
print(len(panels), "panels")
