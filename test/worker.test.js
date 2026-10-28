import test from 'node:test';
import assert from 'node:assert/strict';
import worker from '../src/worker.js';

test('health describes the preview honestly and cannot be cached', async () => {
  const r = await worker.fetch(new Request('https://saidnest.com/api/health'), {});
  assert.equal(r.status, 200);
  assert.equal(r.headers.get('cache-control'), 'no-store');
  const body = await r.json();
  assert.equal(body.service, 'saidnest');
  assert.equal(body.release, 'preview');
  assert.ok(Object.values(body.capabilities).every(value => value === false));
});
test('recording uploads are rejected without touching assets', async () => {
  const r = await worker.fetch(new Request('https://saidnest.com/api/recordings', { method: 'POST', body: 'private audio' }), {});
  assert.equal(r.status, 405);
  assert.equal(r.headers.get('allow'), 'GET, HEAD');
});
test('unknown API routes fail closed rather than serve a website', async () => {
  const r = await worker.fetch(new Request('https://saidnest.com/api/repository'), {});
  assert.equal(r.status, 404);
  assert.match(r.headers.get('content-type'), /application\/json/);
});
test('canonical redirect preserves the path and query on the known domain', async () => {
  const r = await worker.fetch(new Request('https://www.saidnest.com/app/?view=timeline'), {});
  assert.equal(r.status, 308);
  assert.equal(r.headers.get('location'), 'https://saidnest.com/app/?view=timeline');
});
test('asset response remains intact while receiving browser protections', async () => {
  const r = await worker.fetch(new Request('https://saidnest.com/'), { ASSETS: { fetch: async () => new Response('page', { status: 200, headers: { 'Content-Type': 'text/html' } }) } });
  assert.equal(await r.text(), 'page');
  assert.match(r.headers.get('content-security-policy'), /frame-ancestors 'none'/);
  assert.equal(r.headers.get('permissions-policy'), 'camera=(), microphone=(), geolocation=()');
  assert.equal(r.headers.get('x-content-type-options'), 'nosniff');
});
test('HEAD sends headers without a response body', async () => {
  const r = await worker.fetch(new Request('https://saidnest.com/api/health', { method: 'HEAD' }), {});
  assert.equal(r.status, 200);
  assert.equal(await r.text(), '');
});
