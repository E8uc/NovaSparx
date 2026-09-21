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
    const native = await page.evaluate(async () => await Promise.all(globalThis.nativeTexturePromises || []));
    assert.equal(native.length, 3, 'All three real Texture targets must render');
    assert.equal(new Set(native.map(item => item.path)).size, 3, 'Do not substitute duplicate assets');
    for (const [index, item] of native.entries()) {
      assert.ok(item.pixelsSha256 && item.width > 0 && item.height > 0);
      assert.ok(logs.some(line => line.startsWith(`REAL_TEXTURE_BROWSER_OK|${item.path}|`) && line.endsWith(item.pixelsSha256)), 'Displayed bytes do not match native decode');
      await page.locator(`#native-texture-${index}`).screenshot({ path: `native-texture-${index}.png` });
    }
    result.textureFixtures = native;
    console.log('REAL_TEXTURE_VISIBLE', JSON.stringify(native));

    const workerNative = await page.evaluate(async () => {
      return await new Promise((resolve, reject) => {
        const worker = new Worker('/worker.js', { type: 'module' });
        const pending = [];
        const rows = [];
        const timeout = setTimeout(() => {
          worker.terminate();
          reject(new Error('Texture worker proof timed out'));
        }, 120000);

        const finishError = (error) => {
          clearTimeout(timeout);
          worker.terminate();
          reject(error instanceof Error ? error : new Error(String(error)));
        };

        worker.onerror = event => {
          finishError(new Error(event.message || 'Texture worker failed'));
        };

        worker.onmessage = async event => {
          const message = event.data || {};
          if (message.type === 'error') {
            finishError(new Error(message.error || 'Texture worker failed'));
            return;
          }

          if (message.type === 'pixels') {
            const task = (async () => {
              const { path, width, height, pixels } = message;
              if (!(pixels instanceof ArrayBuffer)) throw new Error('Worker did not transfer an ArrayBuffer');
              if (width <= 0 || height <= 0 || pixels.byteLength !== width * height * 4) {
                throw new Error('Worker RGBA payload is invalid');
              }

              const hash = await crypto.subtle.digest('SHA-256', pixels);
              const pixelsSha256 = Array.from(new Uint8Array(hash), n => n.toString(16).padStart(2, '0')).join('').toUpperCase();

              const canvas = document.createElement('canvas');
              canvas.id = `worker-texture-${rows.length}`;
              canvas.width = width;
              canvas.height = height;
              canvas.getContext('2d').putImageData(
                new ImageData(new Uint8ClampedArray(pixels), width, height),
                0,
                0
              );
              document.body.append(canvas);

              rows.push({ path, width, height, pixelsSha256 });
            })();
            pending.push(task);
            return;
          }

          if (message.type === 'done') {
            try {
              await Promise.all(pending);
              if (message.exitCode !== 0) throw new Error(`Texture worker exited with ${message.exitCode}`);
              clearTimeout(timeout);
              worker.terminate();
              resolve(rows);
            } catch (error) {
              finishError(error);
            }
          }
        };
      });
    });

    assert.equal(workerNative.length, native.length, 'Worker must render the same three real Texture targets');
    assert.equal(new Set(workerNative.map(item => item.path)).size, 3, 'Worker must not substitute duplicate assets');
    const expectedByPath = new Map(native.map(item => [item.path, item]));
    for (const [index, item] of workerNative.entries()) {
      const expected = expectedByPath.get(item.path);
      assert.ok(expected, `Unexpected worker Texture path: ${item.path}`);
      assert.equal(item.width, expected.width);
      assert.equal(item.height, expected.height);
      assert.equal(item.pixelsSha256, expected.pixelsSha256, 'Worker pixels must match the main-thread browser proof and desktop reference');
      await page.locator(`#worker-texture-${index}`).screenshot({ path: `worker-texture-${index}.png` });
    }

    const messagesAfterImmediateTermination = await page.evaluate(async () => {
      const worker = new Worker('/worker.js', { type: 'module' });
      let messages = 0;
      worker.onmessage = () => { messages += 1; };
      worker.terminate();
      await new Promise(resolve => setTimeout(resolve, 300));
      return messages;
    });
    assert.equal(messagesAfterImmediateTermination, 0, 'A terminated parser worker must not publish stale output');

    result.workerTextureFixtures = workerNative;
    result.workerTextureParsingProven = true;
    result.workerTerminationProven = true;
    console.log('REAL_TEXTURE_WORKER_VISIBLE', JSON.stringify(workerNative));
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
