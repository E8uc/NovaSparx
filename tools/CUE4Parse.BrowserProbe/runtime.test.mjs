import assert from 'node:assert/strict';
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { chromium } from 'playwright';

const bundle = path.resolve(process.argv[2] || '');
assert.ok(fs.existsSync(path.join(bundle, '_framework/dotnet.js')), 'Pass the published AppBundle directory');
const logs = [];
let wasmBytes = 0;
const server = http.createServer((req, res) => {
  const pathname = new URL(req.url, 'http://localhost').pathname;
  if (pathname === '/') {
    res.setHeader('Content-Type', 'text/html');
    res.end('<!doctype html><meta charset="utf-8"><title>CUE4Parse browser runtime proof</title><script type="module" src="/main.js"></script>');
    return;
  }
  const file = path.resolve(bundle, '.' + decodeURIComponent(pathname));
  if (!file.startsWith(bundle + path.sep)) { res.writeHead(403).end(); return; }
  try {
    const data = fs.readFileSync(file);
    const ext = path.extname(file);
    res.setHeader('Content-Type', ext === '.wasm' ? 'application/wasm' : ext === '.js' ? 'text/javascript' : ext === '.json' ? 'application/json' : 'application/octet-stream');
    res.setHeader('Cache-Control', 'no-store');
    res.end(data);
  } catch { res.writeHead(404).end(); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
let browser;
let result;
try {
  browser = await chromium.launch({ headless: true });
  const page = await browser.newPage();
  page.on('console', message => { logs.push(message.text()); console.log(message.text()); });
  page.on('pageerror', error => logs.push(`PAGE_ERROR: ${error.stack}`));
  page.on('response', async response => {
    if (new URL(response.url()).pathname.endsWith('.wasm')) wasmBytes += Number(response.headers()['content-length'] || 0);
  });
  await page.goto(`http://127.0.0.1:${server.address().port}/`);
  await page.waitForFunction(() => ['ready','failed'].includes(globalThis.cue4parseProbe?.state), null, { timeout: 120000 });
  result = await page.evaluate(() => ({ ...globalThis.cue4parseProbe, userAgent: navigator.userAgent,
    wasmResources: performance.getEntriesByType('resource').filter(r => r.name.endsWith('.wasm')).map(r => ({url:r.name, bytes:r.encodedBodySize})) }));
  assert.equal(result.state, 'ready', JSON.stringify(result));
  assert.ok(logs.some(line => line.includes('CUE4PARSE_BROWSER_WASM_OK')), 'Managed browser entrypoint did not execute');
  assert.ok(result.wasmResources.length > 0, 'No WASM fetched by browser');
  assert.equal(result.assetParsingProven, false, 'A runtime smoke test cannot prove real asset parsing');
  assert.equal(logs.filter(line => line.includes('CUE4PARSE_STAGE|real-toc-ctr-index|supported|')).length, 2, 'Both real encrypted indexes must pass');
  assert.equal(logs.filter(line => line.includes('CUE4PARSE_STAGE|real-ucas-block|supported|')).length, 2, 'Both real UCAS blocks must pass');
  result.realContainerBytesProven = true;
  if (process.env.REQUIRE_TEXTURE === '1') {
    assert.ok(logs.some(line => line.startsWith('REAL_TEXTURE_BROWSER_OK|')), 'No real browser package/texture proof');
    const native = await page.evaluate(async () => await globalThis.nativeTexturePromise);
    assert.ok(native?.pixelsSha256 && native.width > 0 && native.height > 0);
    assert.ok(logs.some(line => line.endsWith(native.pixelsSha256)), 'Displayed bytes do not match native decode');
    await page.locator('#native-texture').screenshot({ path: 'native-texture.png' });
    result.textureFixture = native;
    console.log('REAL_TEXTURE_VISIBLE', JSON.stringify(native));
  }
  console.log('ACTUAL_BROWSER_RUNTIME_PROOF', JSON.stringify(result));
  await page.goto(`http://127.0.0.1:${server.address().port}/?test=reject-partial-block`);
  await page.waitForFunction(() => ['ready','failed'].includes(globalThis.cue4parseProbe?.state), null, { timeout: 120000 });
  const rejection = await page.evaluate(() => globalThis.cue4parseProbe);
  assert.equal(rejection.state, 'failed');
  assert.match(rejection.error || '', /Require complete ECB blocks/);
  assert.match(rejection.error || '', /count=15/);
  result.partialBlockRejectionProven = true;
  console.log('AES_PARTIAL_BLOCK_REJECTED_AT_BROWSER_BOUNDARY');
  await page.goto(`http://127.0.0.1:${server.address().port}/?test=cancel-ctr`);
  await page.waitForFunction(() => ['ready','failed'].includes(globalThis.cue4parseProbe?.state), null, { timeout: 120000 });
  const cancelled = await page.evaluate(() => globalThis.cue4parseProbe);
  assert.equal(cancelled.state, 'failed', 'Cancelled CTR must not succeed');
  assert.match(cancelled.error || '', /OperationCanceled/);
  result.ctrPreCancellationProven = true;
  console.log('AES_CTR_CANCELLATION_PROVEN_AT_BROWSER_BOUNDARY');
} finally {
  fs.writeFileSync('browser-runtime-proof.json', JSON.stringify({result, logs, wasmBytes}, null, 2));
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}
