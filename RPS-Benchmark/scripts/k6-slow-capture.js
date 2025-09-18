import http from 'k6/http';
import { check, sleep } from 'k6';
import { uuidv4 } from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.4/index.js';

/*
 k6-slow-capture.js
 Tracks the top N slowest requests (by wall time) along with correlation IDs.
 Environment variables:
  TARGET_URL (required)   : Endpoint to POST
  VUS (default 5)         : Virtual users
  DURATION (default 30s)  : Test duration
  THINK_MS (default 0)    : Sleep per iteration (client think time)
  MAX_TRACK (default 200) : Maintain this many worst requests in memory
  TOP_N (default 5)       : Number of slow requests to output in summary
  SLOW_MS (default 0)     : Only consider requests >= this many ms (0 = all)

 Outputs slow-requests.json artifact with top slow entries.
 Each request sets x-correlation-id and traceparent headers and reuses correlation id as transactionId.
*/

export const options = {
  vus: Number(__ENV.VUS || 5),
  duration: __ENV.DURATION || '30s',
  thresholds: {
    http_req_failed: ['rate<0.01']
  }
};

const MAX_TRACK = Number(__ENV.MAX_TRACK || 200);
const TOP_N = Number(__ENV.TOP_N || 5);
const SLOW_MS = Number(__ENV.SLOW_MS || 0);
let slowest = []; // {corr, dur, status}

function track(corr, dur, status) {
  if (SLOW_MS && dur < SLOW_MS) return;
  if (slowest.length < MAX_TRACK) {
    slowest.push({ corr, dur, status });
  } else {
    // replace current min if this one is slower
    let idx = 0;
    for (let i = 1; i < slowest.length; i++) if (slowest[i].dur < slowest[idx].dur) idx = i;
    if (dur > slowest[idx].dur) slowest[idx] = { corr, dur, status };
  }
}

function makeTraceParent(guid) {
  const hex = guid.replace(/-/g, '');
  const traceId = (hex + '0'.repeat(32)).substring(0, 32);
  const spanId = hex.substring(0, 16);
  return `00-${traceId}-${spanId}-01`;
}

export default function() {
  const url = __ENV.TARGET_URL;
  if (!url) throw new Error('TARGET_URL env var is required');

  const corr = uuidv4();
  const payload = JSON.stringify({ transactionId: corr });
  const headers = {
    'Content-Type': 'application/json',
    'x-correlation-id': corr,
    'traceparent': makeTraceParent(corr)
  };

  const start = Date.now();
  const res = http.post(url, payload, { headers });
  const dur = Date.now() - start; // ms
  track(corr, dur, res.status);

  check(res, { 'status is 2xx/3xx': r => r.status < 400 });

  const think = Number(__ENV.THINK_MS || 0);
  if (think > 0) sleep(think / 1000);
}

export function handleSummary(data) {
  slowest.sort((a,b) => b.dur - a.dur);
  const top = slowest.slice(0, TOP_N);
  const lines = ["Top Slow Requests (corrId,duration_ms,status)"]
    .concat(top.map(r => `${r.corr},${r.dur},${r.status}`));
  let summaryText = '';
  try {
    summaryText = textSummary(data, { indent: ' ', enableColors: false });
  } catch (e) {
    summaryText = '\n(Standard summary unavailable: ' + e + ')\n';
  }
  console.log("\n===== k6 SUMMARY =====\n" + summaryText + "\n===== TOP SLOW REQUESTS =====\n" + lines.join('\n') + "\n============================\n");
  return {
    'slow-requests.json': JSON.stringify({
      topN: TOP_N,
      totalTracked: slowest.length,
      generatedAt: new Date().toISOString(),
      config: { MAX_TRACK, SLOW_MS },
      slowestSorted: top
    }, null, 2)
  };
}
