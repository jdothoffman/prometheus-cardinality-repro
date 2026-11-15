#!/usr/bin/env python3
"""Print the statistics the experiment log quotes, from one or more run reports.

    python3 tools/report-stats.py reports/<run>.html [more reports...] [--minutes]
                                  [--windows 420-960,1030-1200]

Each HTML report embeds the run's data (the same data its charts use). For each report
this prints:

  - requests, failed requests and the worst 1-second p95
  - the longest run of consecutive seconds with p95 > 100 ms after warm-up (t >= 60), and
    every such run of 5 s or more (a "stall"); single-second blips occur in controls too
  - per window (seconds since the run started): CPU throttling as the mean across pods
    (the mean over polls of each poll's mean), and the median / max of the per-second p95
  - export failures, retries and reasons; Alloy restarts; head series; heap peak by direct polls

--minutes adds a per-minute export timeline (max export duration, payload, points, failures,
heap, Alloy memory), which shows when a run crosses the OTLP exporter's 64 MiB request limit.

The statistics match app/Report.cs: "throttling, mean across pods" is ThrMean there, and the
median p95 is the median of per-second p95 over seconds with at least one successful request.
"""
import json
import re
import statistics as st
import sys

WARM_UP = 60          # seconds ignored at the start, as in Report.cs
STALL_MS = 100        # per-second p95 above this counts
STALL_SECONDS = 5     # ...for at least this many consecutive seconds


def load(path):
    html = open(path, encoding="utf-8").read()
    m = re.search(r"const R = (.*?);\nconst css", html, re.S)
    if not m:
        sys.exit(f"{path}: no embedded run data found")
    return json.loads(m.group(1))


def seconds_since_start(run, sample):
    scale = 1000 if sample["ts"] > 1e11 else 1
    return (sample["ts"] - run["startedAtUnix"] * scale) / scale


def throttle_mean(run, start, end):
    """Mean across pods of the throttled share, averaged over the polls in [start, end)."""
    by_poll = {}
    for s in run["replicaSamples"]:
        t = seconds_since_start(run, s)
        if start <= t < end and s.get("reachable") and s.get("cpuCores") is not None:
            by_poll.setdefault(round(t), []).append(s.get("throttledPct") or 0)
    pods = len({s["pod"] for s in run["replicaSamples"]})
    per_poll = [st.mean(v) for v in by_poll.values() if len(v) >= min(4, pods)]
    return (round(st.mean(per_poll), 1), len(per_poll)) if per_poll else (None, 0)


def p95_stats(run, start, end):
    ps = [p["p95"] for p in run["points"] if start <= p["t"] < end and p["ok"] > 0]
    return (round(st.median(ps), 1), round(max(ps))) if ps else (None, None)


def stalls(run):
    bad = [p["t"] for p in run["points"] if p["t"] >= WARM_UP and p["ok"] > 0 and p["p95"] > STALL_MS]
    episodes = []
    for t in bad:
        if episodes and t - episodes[-1][1] <= 2:
            episodes[-1][1] = t
        else:
            episodes.append([t, t])
    longest = max(episodes, key=lambda e: e[1] - e[0], default=None)
    return [(a, b) for a, b in episodes if b - a + 1 >= STALL_SECONDS], len(bad), longest


def minutes(run):
    rows, pipeline = {}, {}
    for s in run["replicaSamples"]:
        rows.setdefault(int(seconds_since_start(run, s) // 60), []).append(s)
    for s in run["pipelineSamples"]:
        pipeline[int(seconds_since_start(run, s) // 60)] = s
    print("   min | export max ms | payload bytes | points | failures | retries | heap MiB | Alloy MiB")
    for m in sorted(rows):
        v, p = rows[m], pipeline.get(m, {})
        print(f"  {m:4d} | {max((s['exportMaxMs'] or 0) for s in v):13.0f} | {max((s['exportBytes'] or 0) for s in v):13,d} | "
              f"{max((s['exportPoints'] or 0) for s in v):6,d} | {sum((s['exportFailures'] or 0) for s in v):8d} | "
              f"{sum((s['exportRetries'] or 0) for s in v):7d} | {max((s['gcHeapBytes'] or 0) for s in v) / 2**20:8.0f} | "
              f"{(p.get('alloyMemory') or 0) / 2**20:9.0f}")


def main(argv):
    show_minutes = "--minutes" in argv
    windows = [(420, 960), (1030, 1200)]
    if "--windows" in argv:
        spec = argv[argv.index("--windows") + 1]
        windows = [tuple(int(x) for x in w.split("-")) for w in spec.split(",")]
    files = [a for a in argv if a.endswith(".html")]
    if not files:
        sys.exit(__doc__)
    for f in files:
        run = load(f)
        facts = run["evidence"]["facts"]
        step = run["steps"][0]
        heap = max((s.get("gcHeapBytes") or 0) for s in run["replicaSamples"]) / 2**20
        found, seconds_over, longest = stalls(run)
        longest_txt = f"{longest[1] - longest[0] + 1} s (t={longest[0]}-{longest[1]})" if longest else "0"
        print(f"== {run['name']}  ({f})")
        print(f"   labels: {run['mode']} | agent: {run['agentProfile']} | {run['durationSeconds']} s")
        print(f"   requests ok {step['ok']:,}, failed {step['failed']}, worst 1-s p95 {step['p95']:.0f} ms; "
              f"seconds over {STALL_MS} ms after warm-up: {seconds_over}, longest run {longest_txt}; "
              f"stalls (>= {STALL_SECONDS} s): {found or 'none'}")
        for a, b in windows:
            thr, polls = throttle_mean(run, a, b)
            med, mx = p95_stats(run, a, b)
            print(f"   t={a}-{b}: throttling mean across pods {thr}% ({polls} polls); p95 median {med} ms, max {mx} ms")
        print(f"   exports: failures {facts.get('exportFailures')}, retries {facts.get('exportRetries')}, "
              f"peak {facts.get('exportPointsPeak'):,} points / {facts.get('exportBytesPeak'):,} B; "
              f"reasons {run['evidence'].get('exportFailureReasons') or {}}")
        print(f"   Alloy restarts {facts.get('alloyRestarts', facts.get('alloyRestartsDelta'))}, refused peak "
              f"{facts.get('alloyRefusedPeak')} pts/s; head series peak {facts.get('headSeriesPeak'):,}; "
              f"heap peak by direct polls {heap:.0f} MiB")
        if show_minutes:
            minutes(run)


if __name__ == "__main__":
    main(sys.argv[1:])
