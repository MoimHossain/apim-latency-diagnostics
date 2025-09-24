import http from 'k6/http';
import { check, sleep } from 'k6';
import { uuidv4 } from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.4/index.js';

/*
 k6-azmon.js
 Pushes each request's latency to Azure Application Insights via Track Trace ingestion API.

 Chosen target: Application Insights (faster to wire than custom Log Analytics table—no schema creation).
 We use the public ingestion endpoint:
   https://dc.services.visualstudio.com/v2/track

 Required env vars:
  TARGET_URL            : API under test
  APPINSIGHTS_IKEY      : Instrumentation Key (GUID) or Connection String primary key part (prefer non-secret ikey; do NOT include full connection string here)
 Optional env vars:
  VUS (default 5)
  DURATION (default 30s)
  THINK_MS (default 0)
  BATCH_SIZE (default 1)  : Number of request telemetry items to send in one ingestion call (1 = per-request)
  FLUSH_INTERVAL_MS (default 2000) : Force flush buffer at this max interval
  SAMPLING (0-1, default 1) : 1 means send all, 0.1 = 10% sampling
  MAX_BUFFER (default 5000): Max telemetry items to keep before forced flush
  CLOUD_ROLE (default k6-loadgen): Value for Application Insights tag ai.cloud.role

 Output format: Application Insights 'MessageData' items (traces) with customDimensions including:
  correlationId, durationMs, status, url, vu, iteration, startTime, requestBodyBytes
 Query example (Kusto in App Insights):
  traces | where customDimensions["testType"] == "k6-azmon" | order by timestamp desc | take 50

 NOTE: Using instrumentation key is simplest. If you only have a connection string, extract the InstrumentationKey= value.
 Security: This script sends data directly to App Insights ingestion; no secrets other than iKey. iKey is not highly sensitive but treat it with care.
*/

export const options = {
  vus: Number(__ENV.VUS || 5),
  duration: __ENV.DURATION || '30s'
};

const TARGET_URL = __ENV.TARGET_URL;
const IKEY = __ENV.APPINSIGHTS_IKEY; // instrumentation key
if (!TARGET_URL) { throw new Error('TARGET_URL is required'); }
if (!IKEY) { throw new Error('APPINSIGHTS_IKEY is required'); }

const BATCH_SIZE = Number(__ENV.BATCH_SIZE || 1);
const FLUSH_INTERVAL_MS = Number(__ENV.FLUSH_INTERVAL_MS || 2000);
const SAMPLING = Number(__ENV.SAMPLING || 1);
const MAX_BUFFER = Number(__ENV.MAX_BUFFER || 5000);
const THINK_MS = Number(__ENV.THINK_MS || 0);
const CLOUD_ROLE = __ENV.CLOUD_ROLE || 'k6-loadgen';

const ingestionUrl = 'https://dc.services.visualstudio.com/v2/track';
let buffer = [];
let lastFlush = Date.now();
let sentCount = 0;
let flushCalls = 0;
let failedItems = 0;
// Each request simply posts to TARGET_URL/{guid} (no query logic needed)

function isoNow() { return new Date().toISOString(); }

function makeTelemetry(item) {
  return {
    name: 'Microsoft.ApplicationInsights.Message',
    time: item.time,
    iKey: IKEY,
    tags: {
      'ai.cloud.role': CLOUD_ROLE,
      'ai.operation.id': item.correlationId,
      'ai.operation.parentId': item.correlationId.substring(0,16),
      'ai.internal.sdkVersion': 'k6:custom'
    },
    data: {
      baseType: 'MessageData',
      baseData: {
        message: `k6 request ${item.correlationId}`,
        severityLevel: 1,
        properties: {
          testType: 'k6-azmon',
          correlationId: item.correlationId,
            url: item.url,
          status: String(item.status),
          durationMs: String(item.durationMs),
          vu: String(item.vu),
          iteration: String(item.iteration),
          startTime: item.startTime,
          requestBodyBytes: String(item.requestBodyBytes),
          sampling: String(SAMPLING)
        }
      }
    }
  };
}

function flush(force=false) {
  if (buffer.length === 0) return;
  if (!force && buffer.length < BATCH_SIZE && (Date.now()-lastFlush) < FLUSH_INTERVAL_MS) return;
  const batchSize = buffer.length;
  const payload = JSON.stringify(buffer);
  const res = http.request('POST', ingestionUrl, payload, { headers: { 'Content-Type': 'application/x-json-stream' } });
  if (res.status >= 400) {
    failedItems += batchSize;
    console.error(`AppInsights ingestion failed status=${res.status} items=${batchSize} body=${res.body}`);
  } else {
    sentCount += batchSize;
  }
  flushCalls += 1;
  buffer = [];
  lastFlush = Date.now();
}

export default function() {
  if (SAMPLING < 1 && Math.random() > SAMPLING) {
    // still execute request but skip telemetry
    doRequest(false);
  } else {
    doRequest(true);
  }
  if (THINK_MS > 0) sleep(THINK_MS/1000);
  flush();
}

let iterationCounter = 0;

function doRequest(track) {
  const corr = uuidv4();
  const start = Date.now();
  const bodyObj = { transactionId: corr };
  const body = JSON.stringify(bodyObj);
  const headers = {
    'Content-Type': 'application/json',
    'x-correlation-id': corr,
    'traceparent': makeTraceParent(corr)
  };
  const base = TARGET_URL.replace(/\/+$/, '');
  const requestUrl = `${base}/${corr}`;
  const res = http.post(requestUrl, body, { headers });
  const dur = Date.now() - start;
  check(res, { 'status<400': r => r.status < 400 });
  if (track) {
    const telemetry = makeTelemetry({
      correlationId: corr,
      durationMs: dur,
      status: res.status,
      url: requestUrl,
      vu: __VU,
      iteration: iterationCounter++,
      startTime: new Date(start).toISOString(),
      requestBodyBytes: body.length,
      time: isoNow()
    });
    buffer.push(telemetry);
    if (buffer.length >= MAX_BUFFER) flush(true);
  }
}

function makeTraceParent(guid) {
  const hex = guid.replace(/-/g, '');
  const traceId = (hex + '0'.repeat(32)).substring(0, 32);
  const spanId = hex.substring(0, 16);
  return `00-${traceId}-${spanId}-01`;
}

export function handleSummary(data) {
  flush(true);
  let summary = '';
  try { summary = textSummary(data, { indent: ' ', enableColors: false }); } catch (e) { summary = '(k6 summary unavailable ' + e + ')'; }
  const ingestion = `ingestion totalSent=${sentCount} failedItems=${failedItems} flushCalls=${flushCalls} sampling=${SAMPLING}`;
  if (__ENV.PRETTY_SUMMARY) {
    // Build concise pretty block focusing on key latency stats
    const m = data.metrics || {};
    function gv(name, key) { return m[name] && m[name].values && (m[name].values[key] !== undefined) ? m[name].values[key] : undefined; }
    const reqs = gv('http_reqs','count') || 0;
    const rps = gv('http_reqs','rate') || 0;
    const durMed = gv('http_req_duration','med');
    const durP90 = gv('http_req_duration','p(90)');
    const durP95 = gv('http_req_duration','p(95)');
    const durMax = gv('http_req_duration','max');
    const waitMed = gv('http_req_waiting','med');
    const waitP95 = gv('http_req_waiting','p(95)');
    const iterMed = gv('iteration_duration','med');
    const errRate = gv('http_req_failed','rate');
    // Data metrics
    const recvBytes = gv('data_received','count');
    const recvRate = gv('data_received','rate');
    const sentBytes = gv('data_sent','count');
    const sentRate = gv('data_sent','rate');
    function humanBytes(b){
      if(b===undefined) return 'n/a';
      const units=['B','KB','MB','GB'];
      let v=b, i=0; while(v>=1024 && i<units.length-1){ v/=1024; i++; }
      return v.toFixed(2)+units[i];
    }
    function fmt(v, digits=2){ return (v===undefined)?'n/a':Number(v).toFixed(digits); }
    const concise = [
      '===== k6 QUICK SUMMARY =====',
      `Requests: total=${reqs} rps=${fmt(rps,2)}`,
      `Latency(ms): med=${fmt(durMed)} p90=${fmt(durP90)} p95=${fmt(durP95)} max=${fmt(durMax)}`,
      `Waiting(ms): med=${fmt(waitMed)} p95=${fmt(waitP95)}`,
      `Iteration(ms): med=${fmt(iterMed)}`,
      `Data: recv=${humanBytes(recvBytes)} (${fmt(recvRate,2)}B/s) sent=${humanBytes(sentBytes)} (${fmt(sentRate,2)}B/s)`,
      `Errors: http_req_failed_rate=${fmt(errRate,4)}`,
      `Ingestion: sent=${sentCount} failed=${failedItems} flushCalls=${flushCalls} avgBatch=${flushCalls? (sentCount/flushCalls).toFixed(2):'0'} sampling=${SAMPLING}`,
      'Query: traces | where customDimensions.testType == "k6-azmon"',
      '================================='
    ].join('\n');
    const linesArr = concise.split('\n');
    console.log('');
    for (const line of linesArr) {
      console.log(line);
    }
    console.log('');
  } else {
    console.log(`\n===== k6 SUMMARY =====\n${summary}\n===== APP INSIGHTS INGESTION =====\n${ingestion}\nQuery: traces | where customDimensions.testType == \"k6-azmon\"\n=================================\n`);
  }
  return { 'note.txt': `ItemsSent=${sentCount};Failed=${failedItems};Query=traces | where customDimensions.testType == 'k6-azmon'` };
}
