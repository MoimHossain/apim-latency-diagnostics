import http from 'k6/http';
import { check, sleep } from 'k6';
import { uuidv4 } from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';

/*
 Basic k6 script to exercise APIM POST endpoint with correlation headers.
 Environment variables (set via Docker -e or local 'k6 run'):
  TARGET_URL  (required) e.g. https://solarapimuat.azure-api.net/casper/transaction
  VUS         (default 5)
  DURATION    (default 30s) k6 duration format
  THINK_MS    (default 0) per-iteration sleep in milliseconds

 Generates a unique x-correlation-id and traceparent per request for later correlation in APIM/App Insights.
*/

export const options = {
    vus: Number(__ENV.VUS || 5),
    duration: __ENV.DURATION || '30s',
    thresholds: {
        http_req_failed: ['rate<0.01'],
        http_req_duration: ['p(95)<1000', 'p(99)<3000']
    }
};

function makeTraceParent(guid) {
    const hex = guid.replace(/-/g, '');
    const traceId = (hex + '0'.repeat(32)).substring(0, 32);
    const spanId = hex.substring(0, 16);
    return `00-${traceId}-${spanId}-01`;
}

export default function () {
    const url = __ENV.TARGET_URL;
    if (!url) {
        throw new Error('TARGET_URL env var is required');
    }
    const corr = uuidv4();
    const payload = JSON.stringify({
        transactionId: corr // reuse correlation id as transactionId to simplify debugging
    });
    const headers = {
        'Content-Type': 'application/json',
        'x-correlation-id': corr,
        'traceparent': makeTraceParent(corr)
    };
    // No subscription key header required in this variant.

    const res = http.post(url, payload, { headers });

    check(res, {
        'status is 200/201/202': r => [200,201,202].includes(r.status)
    });

    const think = Number(__ENV.THINK_MS || 0);
    if (think > 0) sleep(think / 1000);
}
