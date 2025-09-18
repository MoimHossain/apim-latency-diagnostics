# RPS / Latency Benchmark Tools

This folder contains scripts to benchmark latency to Azure API Management (APIM).

## k6 Quick Start (Bash / Linux / macOS / WSL)

## **Pull k6 image:**
```bash
    docker pull grafana/k6:latest
```

## **Run a short test (30s, 5 VUs default):**

Regular mode:

```bash
wsl docker run --rm -e TARGET_URL="https://solarapimuat.azure-api.net/casper/transaction" -e VUS=100 -e DURATION=1m -v /mnt/c/GitHub/moimhossain/apim-latency-diagnostics/RPS-Benchmark/scripts:/scripts grafana/k6:latest run /scripts/k6-basic.js       

## **Capture Top Slow Requests (one-liner)**

Collect top 10 slow requests (track up to 500, only consider >=100ms):

```bash
wsl docker run --rm -e TARGET_URL="https://solarapimuat.azure-api.net/casper/transaction" -e VUS=100 -e DURATION=1m -e TOP_N=10 -e MAX_TRACK=500 -e SLOW_MS=100 -v /mnt/c/GitHub/moimhossain/apim-latency-diagnostics/RPS-Benchmark/scripts:/scripts grafana/k6:latest run /scripts/k6-slow-capture.js
```

Artifacts produced: `slow-requests.json` in the mounted scripts folder plus console summary.

Env vars:
- `TOP_N` number of entries to output
- `MAX_TRACK` size of internal worst list
- `SLOW_MS` minimum duration to consider (ms, 0 = all)

## **Log Every Slow Request (line-by-line)**

One-liner (logs each request >=150ms as it happens; collects up to 2000 worst):
```bash
wsl docker run --rm -e TARGET_URL="https://solarapimuat.azure-api.net/casper/transaction" -e VUS=100 -e DURATION=30s -e SLOW_MS=150 -e TOP_N_SUMMARY=10 -e MAX_COLLECT=2000 -v /mnt/c/GitHub/moimhossain/apim-latency-diagnostics/RPS-Benchmark/scripts:/scripts grafana/k6:latest run /scripts/k6-slow-log.js
```

Sample console line:
```
SLOW_REQ corr=6a9e3c4e-b0f9-4b71-8f4e-0b1d8d7c1d3c dur_ms=412 status=200
```

After test, artifacts in `scripts/`:
- `slow-requests-lines.txt` (all logged slow lines)
- `slow-requests-top.json` (top N JSON summary)

Extract the 5 worst from the text file (WSL bash example):
```bash
grep '^corr=' /mnt/c/GitHub/moimhossain/apim-latency-diagnostics/RPS-Benchmark/scripts/slow-requests-lines.txt | awk '{print $1,$2,$3}' | sed 's/corr=//;s/dur_ms=//;s/status=//' | sort -k2 -nr | head -5
```

Or from console buffer (if you captured output):
```bash
grep 'SLOW_REQ corr=' run.log | awk '{for(i=1;i<=NF;i++){if($i~"^corr=")c=$i; if($i~"^dur_ms=")d=$i; if($i~"^status=")s=$i;} print c,d,s;}' | sed 's/corr=//;s/dur_ms=//;s/status=//' | sort -k2 -nr | head -10
```
```

## **WSL run with Enhanced Stats**

Enhanced stats mode (adds latency percentiles):

```bash
wsl bash -c 'docker run --rm -e TARGET_URL="https://solarapimuat.azure-api.net/casper/transaction" -e VUS=100 -e DURATION=1m -v /mnt/c/GitHub/moimhossain/apim-latency-diagnostics/RPS-Benchmark/scripts:/scripts grafana/k6:latest run --summary-trend-stats "avg,min,med,p(75),p(90),p(95),p(99),p(99.5),p(99.9),max" /scripts/k6-basic.js'
```


## Interpreting Output

k6 prints a summary when the test ends. Key fields:

- **http_req_duration:** Total time (client perspective) per request (connect + TLS + TTFB + body).
- **http_req_connecting / tls_handshaking:** Add `--summary-trend-stats="avg,min,med,p(90),p(95),p(99),p(99.9),max"` for deeper stats.
- **http_reqs:** Count of requests sent.
- **checks:** Success ratio.

**Example to expand stats:**
```bash
docker run --rm \
  -e TARGET_URL="$TARGET_URL" \
  -v "$(pwd)":/scripts \
  grafana/k6:latest run --summary-trend-stats "avg,min,med,p(90),p(95),p(99),p(99.9),max" /scripts/k6-basic.js
```

## Correlation & Tracing

Each request sets:

- `x-correlation-id`: a GUID also used as `transactionId` in the POST body.
- `traceparent`: Simplified W3C trace context (same GUID for trace + span root) to help correlation if APIM policy forwards it.

You can query APIM / App Insights logs for a specific GUID to analyze a slow request.

## Next Steps

- Verify APIM sees the custom headers in its diagnostics logs.
- Tune VUs and duration to gather enough samples for p99 / p99.9.
- Optionally export k6 JSON (`--out json=results.json`) or to an InfluxDB/Grafana stack for visualization.

## Export JSON Results

```bash
docker run --rm \
  -e TARGET_URL="$TARGET_URL" \
  -v "$(pwd)":/scripts \
  grafana/k6:latest run --out json=/scripts/results.json /scripts/k6-basic.js
```
The `results.json` file will appear in the `RPS-Benchmark` folder.

## Troubleshooting

- **403 errors:** Ensure any required auth (not needed if your APIM endpoint is open for test scope).
- **Connection reset:** Check local network / corporate proxy; consider `--insecure-skip-tls-verify` (only for temporary debugging) if using self-signed certs.
- **Unexpected 429:** APIM rate limit or quota policy hit.

## Cleaning Up

Nothing persists beyond the run (container is `--rm`). Just delete any JSON result files you no longer need.

---

# Key Metrics Explained

- **http_req_duration:** Total end-to-end time for each HTTP request (DNS lookup + TCP connect + TLS handshake + request send + server processing + first byte + response body download). Distribution stats shown:
  - **avg:** Mean across all requests (skewed upward by long tails).
  - **min / max:** Fastest and slowest single request.
  - **med:** Median (50% of requests ≤ this).
  - **p(90) / p(95):** 90th / 95th percentile (tail latency—10% / 5% slower than these).
  - **1.58s max** means at least one outlier took that long.
  - `{ expected_response:true }` block: Same metric filtered only to responses k6 considered successful (status < 400 by default unless changed). Since failure rate is near zero, the numbers are almost identical.

- **http_req_failed:** `0.00% 4 out of 78141`: Fraction of requests that k6 judged failed (non-2xx/3xx or network error). 4 failures over 78,141 is effectively 0%.

- **http_reqs:** `78141 1301.451093/s`: Total number of HTTP requests performed, and average request rate (requests per second) over the active execution window.

### Execution Section

- **iterations:** `78141 1301.451093/s`: In the default (single script) scenario, one iteration = one full execution of your default function. Because your function performs exactly one http.post per loop, iterations == http_reqs.
  - If you had multiple requests per loop, then http_reqs > iterations. If you added logic that sometimes skips a request, http_reqs < iterations.

- **iteration_duration:** Time to execute one loop of the default function, including everything inside it: generating UUID, building payload, the HTTP call (i.e., http_req_duration), plus minimal JS overhead. That’s why iteration_duration is slightly higher than http_req_duration on average.

---

## Why Averages and Medians Differ So Much

- **med (31.61ms)** is low because the majority of requests are fast.
- **avg (75.85ms)** is much higher because a slice of requests (the slower tail up to 1.58s) pulls the mean upward.
- **p(90)=335ms** shows 10% of traffic is ≥ ~335ms. That’s your tail region worth investigating.

### Typical Causes of This Shape

- Connection reuse not fully warmed at start (early iterations faster vs later congestion?).
- Backend or APIM occasionally adding latency (queueing, policy evaluation, TLS session renegotiation).
- Occasional network jitter or GC pauses (less likely at 1.5s unless backend stalls).
- If load (VUs=100 over 1m) bursts above what APIM/backend comfortably handles, queuing emerges in the 90–95 percentile band.

### How to Validate Interpretation

- Add more breakdown metrics with:
  - Enable per-phase timing by adding custom tags or using http_req_waiting, http_req_connecting, http_req_tls_handshaking, http_req_blocked summary fields (these are already tracked; just view them in detailed output or JSON export).

### When Iterations Diverge From http_reqs

- Multiple requests per loop: iterations < http_reqs.
- Conditional branching (only some iterations fire a request).
- Use of scenarios with exec functions calling different request sets.
- Adding sleep() increases iteration duration but not request count.

### Quick Sanity Checks

- Ensure you didn’t accidentally run from cold network conditions (first seconds may show different distribution—consider excluding warm-up by using a ramp: `stages: [{duration:'30s', target:100}, {duration:'1m', target:100}]` and analyze only the steady period).
- Export JSON to inspect individual outliers.

---

## Suggested Next Step

If you want to stabilize tail measurement, switch to a constant arrival rate scenario (e.g., exactly 1300 RPS) instead of VU-based scheduling; I can add that script (`k6-constant-rate.js`) if you’d like.

Let me know if you want:

- Constant arrival rate script
- Breakdown of http_req_waiting vs others
- Guidance on correlating outliers to APIM logs