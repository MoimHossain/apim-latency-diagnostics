import http from 'k6/http';
import { check, sleep } from 'k6';
import { uuidv4 } from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';
import { textSummary } from 'https://jslib.k6.io/k6-summary/0.0.4/index.js';

/*
 k6-slow-log.js
 Logs every request slower than SLOW_MS as a console line:
 SLOW_REQ corr=<id> dur_ms=<duration> status=<status>
 Later you can extract and sort them outside k6 (e.g., grep & sort).
 Environment vars:
  TARGET_URL   (required)
  VUS          (default 5)
  DURATION     (default 30s)
  THINK_MS     (default 0)
  SLOW_MS      (default 100) threshold for logging
  LOG_ALL      (optional any non-empty) log all requests regardless of SLOW_MS
  TOP_N_SUMMARY (default 5) number of worst to summarize post-run
*/

export const options = {
  vus: Number(__ENV.VUS || 5),
  duration: __ENV.DURATION || '30s'
};

const SLOW_MS = Number(__ENV.SLOW_MS || 100);
const LOG_ALL = !!__ENV.LOG_ALL;
const TOP_N_SUMMARY = Number(__ENV.TOP_N_SUMMARY || 5);
let collected = []; // {corr,dur,status}
const MAX_COLLECT = Number(__ENV.MAX_COLLECT || 2000);

function maybeCollect(obj){
  if (collected.length < MAX_COLLECT) collected.push(obj); else {
    // replace min if slower
    let idx=0; for (let i=1;i<collected.length;i++) if (collected[i].dur < collected[idx].dur) idx=i;
    if (obj.dur > collected[idx].dur) collected[idx]=obj;
  }
}

function makeTraceParent(guid){
  const hex = guid.replace(/-/g,'');
  return `00-${(hex+'0'.repeat(32)).substring(0,32)}-${hex.substring(0,16)}-01`;
}

export default function(){
  const url = __ENV.TARGET_URL; if(!url) throw new Error('TARGET_URL required');
  const corr = uuidv4();
  const payload = JSON.stringify({ transactionId: corr });
  const headers = { 'Content-Type':'application/json', 'x-correlation-id':corr, 'traceparent': makeTraceParent(corr) };
  const start = Date.now();
  const res = http.post(url, payload, { headers });
  const dur = Date.now() - start;
  if (LOG_ALL || dur >= SLOW_MS){
    console.log(`SLOW_REQ corr=${corr} dur_ms=${dur} status=${res.status}`);
    maybeCollect({corr, dur, status: res.status});
  }
  check(res, { 'status<400': r => r.status < 400 });
  const think = Number(__ENV.THINK_MS || 0); if (think>0) sleep(think/1000);
}

export function handleSummary(data){
  collected.sort((a,b)=>b.dur - a.dur);
  const top = collected.slice(0, TOP_N_SUMMARY);
  const lines = top.map(r=>`TOP corr=${r.corr} dur_ms=${r.dur} status=${r.status}`);
  let summary='';
  try{ summary = textSummary(data,{indent:' ',enableColors:false}); }catch(e){ summary='(summary unavailable '+e+')'; }
  console.log(`\n===== k6 SUMMARY =====\n${summary}\n===== TOP COLLECTED SLOW =====\n${lines.join('\n')}\n==============================\n`);
  return {
    'slow-requests-lines.txt': collected.map(r=>`corr=${r.corr} dur_ms=${r.dur} status=${r.status}`).join('\n'),
    'slow-requests-top.json': JSON.stringify({ top, generatedAt:new Date().toISOString(), config:{SLOW_MS, LOG_ALL, MAX_COLLECT:MAX_COLLECT} }, null,2)
  };
}
