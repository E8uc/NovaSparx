import assert from 'node:assert/strict';
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { chromium } from 'playwright';

const LIVE_MANIFEST_ENDPOINT = 'https://export-service-new.dillyapis.com/v1/manifests';
const LIVE_CHUNK_BASE = new URL('https://egdownload.fastly-edge.com/Builds/Fortnite/CloudDir/');
const LIVE_MAX_MANIFEST_BYTES = 64 * 1024 * 1024;
const LIVE_MAX_CHUNK_BYTES = 8 * 1024 * 1024;

let liveManifestPromise = null;
let liveManifestRequests = 0;
let liveChunkRequests = 0;
let liveChunkBytes = 0;
const liveChunkPaths = [];
let relayRequests = 0;
let relayBytes = 0;
const RELAY_MAX_RANGE_BYTES = 4 * 1024 * 1024;
const RELAY_ALLOWED_HOSTS = new Set([
  'egdownload.fastly-edge.com',
  'download.epicgames.com',
  'fortnite-direct.dillycdn.com',
  'stormforge.dillycdn.com'
]);

async function fetchBounded(url, maxBytes, label) {
  const response = await fetch(url, {
    redirect: 'follow',
    headers: {
      accept: 'application/json,application/octet-stream,*/*;q=0.8'
    }
  });
  if (!response.ok) throw new Error(`${label} returned HTTP ${response.status}`);
  const declared = Number(response.headers.get('content-length') || 0);
  if (declared > maxBytes) throw new Error(`${label} exceeded the declared byte budget`);
  const bytes = new Uint8Array(await response.arrayBuffer());
  if (bytes.byteLength > maxBytes) throw new Error(`${label} exceeded the byte budget`);
  return bytes;
}

function looksLikeRawManifest(bytes) {
  if (bytes.byteLength < 16) return false;
  for (const value of bytes) {
    if (value === 9 || value === 10 || value === 13 || value === 32) continue;
    return value !== 0x7b && value !== 0x5b;
  }
  return false;
}

function collectManifestCandidates(root) {
  const urls = new Map();
  const ids = new Map();

  const addUrl = (value, score) => {
    try {
      const url = new URL(String(value || ''));
      if (url.protocol !== 'https:') return;
      const key = url.toString();
      urls.set(key, Math.max(urls.get(key) ?? -Infinity, score));
    } catch {}
  };

  const addId = (value, score) => {
    const id = String(value ?? '').trim();
    if (!id || id.length > 240) return;
    ids.set(id, Math.max(ids.get(id) ?? -Infinity, score));
  };

  const walk = node => {
    if (Array.isArray(node)) {
      for (const item of node) walk(item);
      return;
    }
    if (!node || typeof node !== 'object') return;

    const objectText = JSON.stringify(node).toLowerCase();
    let baseScore = 0;
    if (objectText.includes('windows')) baseScore += 40;
    if (objectText.includes('fortnite')) baseScore += 25;
    if (objectText.includes('live') || objectText.includes('latest')) baseScore += 10;
    if (objectText.includes('android') || objectText.includes('ios') || objectText.includes('mac')) baseScore -= 40;
    if (objectText.includes('studio') || objectText.includes('uefn')) baseScore -= 15;

    for (const [rawKey, value] of Object.entries(node)) {
      const key = rawKey.toLowerCase();

      if (typeof value === 'string') {
        const lower = value.toLowerCase();
        if (
          lower.includes('.manifest') ||
          key.includes('manifest') ||
          key.includes('download')
        ) {
          addUrl(value, baseScore + (lower.includes('.manifest') ? 80 : 0));
        }

        if (key === 'manifestid' || key === 'manifest_id' || key === 'id') {
          addId(value, baseScore);
        }
      } else if (
        typeof value === 'number' &&
        (key === 'manifestid' || key === 'manifest_id' || key === 'id')
      ) {
        addId(value, baseScore);
      }

      walk(value);
    }
  };

  walk(root);

  return {
    urls: [...urls.entries()]
      .map(([url, score]) => ({ url, score }))
      .sort((a, b) => b.score - a.score),
    ids: [...ids.entries()]
      .map(([id, score]) => ({ id, score }))
      .sort((a, b) => b.score - a.score)
  };
}

async function resolveRawManifest(url, visited = new Set(), depth = 0) {
  if (depth > 5) throw new Error('Live manifest recursion limit reached');
  const normalized = new URL(url).toString();
  if (visited.has(normalized)) throw new Error('Live manifest source loop detected');
  visited.add(normalized);

  const bytes = await fetchBounded(normalized, LIVE_MAX_MANIFEST_BYTES, 'Live manifest source');
  if (looksLikeRawManifest(bytes)) return { bytes, source: normalized };

  let data;
  try {
    data = JSON.parse(new TextDecoder().decode(bytes));
  } catch {
    throw new Error('Live manifest metadata could not be decoded');
  }

  const candidates = collectManifestCandidates(data);

  for (const candidate of candidates.urls) {
    if (candidate.url === normalized) continue;
    try {
      return await resolveRawManifest(candidate.url, visited, depth + 1);
    } catch {}
  }

  if (!normalized.toLowerCase().endsWith('.manifest')) {
    const base = normalized.replace(/\/+$/, '');
    for (const candidate of candidates.ids) {
      try {
        return await resolveRawManifest(
          base + '/' + encodeURIComponent(candidate.id),
          visited,
          depth + 1
        );
      } catch {}
    }
  }

  throw new Error('No raw current Fortnite manifest could be resolved');
}

const bundle = path.resolve(process.argv[2] || '');
assert.ok(fs.existsSync(path.join(bundle, '_framework/dotnet.js')), 'Pass the published AppBundle directory');
const logs = [];
let wasmBytes = 0;
const server = http.createServer(async (req, res) => {
  const pathname = new URL(req.url, 'http://localhost').pathname;

  try {
    if (pathname === '/live/manifest') {
      liveManifestRequests += 1;
      const live = await (liveManifestPromise ||= resolveRawManifest(LIVE_MANIFEST_ENDPOINT));
      res.statusCode = 200;
      res.setHeader('Content-Type', 'application/octet-stream');
      res.setHeader('Cache-Control', 'no-store');
      res.setHeader('Content-Length', String(live.bytes.byteLength));
      res.setHeader('X-NovaSparx-Live-Source', live.source);
      res.end(Buffer.from(live.bytes));
      return;
    }

    if (pathname.startsWith('/live/chunk/')) {
      const relative = decodeURIComponent(pathname.slice('/live/chunk/'.length));
      if (!liveChunkPaths.includes(relative)) liveChunkPaths.push(relative);
      if (!relative || relative.startsWith('/') || relative.includes('..')) {
        res.writeHead(400).end();
        return;
      }

      const upstream = new URL(relative, LIVE_CHUNK_BASE);
      if (!upstream.toString().startsWith(LIVE_CHUNK_BASE.toString())) {
        res.writeHead(403).end();
        return;
      }

      liveChunkRequests += 1;
      const bytes = await fetchBounded(
        upstream,
        LIVE_MAX_CHUNK_BYTES,
        'Live BuildPatch chunk'
      );
      liveChunkBytes += bytes.byteLength;

      res.statusCode = 200;
      res.setHeader('Content-Type', 'application/octet-stream');
      res.setHeader('Cache-Control', 'no-store');
      res.setHeader('Content-Length', String(bytes.byteLength));
      res.end(Buffer.from(bytes));
      return;
    }

    if (pathname === '/edge/range') {
      const requestUrl = new URL(req.url, 'http://localhost');
      let target;
      try {
        target = new URL(requestUrl.searchParams.get('url') || '');
      } catch {
        res.writeHead(400).end('Invalid relay URL');
        return;
      }

      const start = Number(requestUrl.searchParams.get('start'));
      const end = Number(requestUrl.searchParams.get('end'));
      if (
        target.protocol !== 'https:' ||
        !RELAY_ALLOWED_HOSTS.has(target.hostname.toLowerCase()) ||
        !Number.isSafeInteger(start) ||
        !Number.isSafeInteger(end) ||
        start < 0 ||
        end < start ||
        end - start + 1 > RELAY_MAX_RANGE_BYTES
      ) {
        res.writeHead(400).end('Invalid relay range');
        return;
      }

      const upstream = await fetch(target, {
        method: 'GET',
        redirect: 'error',
        headers: {
          range: `bytes=${start}-${end}`,
          accept: 'application/octet-stream,*/*;q=0.8'
        }
      });

      if (![200, 206].includes(upstream.status)) {
        res.writeHead(502).end(`Relay upstream HTTP ${upstream.status}`);
        return;
      }

      const bytes = new Uint8Array(await upstream.arrayBuffer());
      const expected = end - start + 1;
      if (bytes.byteLength < 1 || bytes.byteLength > expected) {
        res.writeHead(502).end('Relay source exceeded requested byte window');
        return;
      }

      relayRequests += 1;
      relayBytes += bytes.byteLength;

      res.statusCode = upstream.status === 206 ? 206 : 200;
      res.setHeader(
        'Content-Type',
        upstream.headers.get('content-type') || 'application/octet-stream'
      );
      res.setHeader('Cache-Control', 'no-store');
      res.setHeader('Accept-Ranges', 'bytes');
      res.setHeader('Content-Length', String(bytes.byteLength));

      const contentRange = upstream.headers.get('content-range');
      if (contentRange) res.setHeader('Content-Range', contentRange);

      res.end(Buffer.from(bytes));
      return;
    }
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
  } catch (error) {
    res.statusCode = 502;
    res.setHeader('Content-Type', 'text/plain; charset=utf-8');
    res.end(String(error?.stack || error));
  }
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
  if (process.env.REQUIRE_LIVE_BUILD_PATCH === '1') {
    const liveWorker = await page.evaluate(async () => {
      return await new Promise((resolve, reject) => {
        const worker = new Worker('/worker.js?test=live-buildpatch', { type: 'module' });
        const timeout = setTimeout(() => {
          worker.terminate();
          reject(new Error('Live BuildPatch worker proof timed out'));
        }, 180000);

        const fail = error => {
          clearTimeout(timeout);
          worker.terminate();
          reject(error instanceof Error ? error : new Error(String(error)));
        };

        worker.onerror = event => {
          fail(new Error(event.message || 'Live BuildPatch worker failed'));
        };

        worker.onmessage = event => {
          const message = event.data || {};
          if (message.type === 'error') {
            fail(new Error(message.error || 'Live BuildPatch worker failed'));
            return;
          }
          if (message.type === 'done') {
            clearTimeout(timeout);
            worker.terminate();
            resolve({ exitCode: message.exitCode });
          }
        };
      });
    });

    assert.equal(liveWorker.exitCode, 0, 'Live BuildPatch worker must exit successfully');
    assert.ok(liveManifestRequests >= 1, 'Worker did not fetch a live Fortnite manifest');
    assert.ok(liveChunkRequests >= 1, 'Worker did not fetch any live BuildPatch chunks');
    assert.ok(liveChunkBytes > 0, 'Worker fetched no live BuildPatch bytes');

    result.liveBuildPatchWorkerProven = true;
    result.liveBuildPatchNetwork = {
      manifestRequests: liveManifestRequests,
      chunkRequests: liveChunkRequests,
      chunkBytes: liveChunkBytes
    };
    console.log('LIVE_BUILDPATCH_WORKER_PROVEN', JSON.stringify(result.liveBuildPatchNetwork));
  }

  if (process.env.REQUIRE_LIVE_TEXTURE === '1') {
    assert.ok(
      Array.isArray(result.textureFixtures) && result.textureFixtures.length === 3,
      'Live Texture proof requires the established three desktop/browser references'
    );

    const networkBeforeLiveTexture = {
      manifestRequests: liveManifestRequests,
      chunkRequests: liveChunkRequests,
      chunkBytes: liveChunkBytes
    };

    const liveTextures = await page.evaluate(async () => {
      return await new Promise((resolve, reject) => {
        const worker = new Worker('/worker.js?test=live-texture', { type: 'module' });
        const pending = [];
        const rows = [];
        const timeout = setTimeout(() => {
          worker.terminate();
          reject(new Error('Live Texture worker proof timed out'));
        }, 300000);

        const fail = error => {
          clearTimeout(timeout);
          worker.terminate();
          reject(error instanceof Error ? error : new Error(String(error)));
        };

        worker.onerror = event => {
          fail(new Error(event.message || 'Live Texture worker failed'));
        };

        worker.onmessage = event => {
          const message = event.data || {};

          if (message.type === 'error') {
            fail(new Error(message.error || 'Live Texture worker failed'));
            return;
          }

          if (message.type === 'pixels') {
            const task = (async () => {
              const { path, width, height, pixels } = message;

              if (!(pixels instanceof ArrayBuffer)) {
                throw new Error('Live Texture worker did not transfer an ArrayBuffer');
              }

              if (
                width <= 0 ||
                height <= 0 ||
                width * height > 65536 ||
                pixels.byteLength !== width * height * 4
              ) {
                throw new Error('Live Texture worker returned an invalid RGBA payload');
              }

              const hash = await crypto.subtle.digest('SHA-256', pixels);
              const pixelsSha256 = Array.from(
                new Uint8Array(hash),
                value => value.toString(16).padStart(2, '0')
              ).join('').toUpperCase();

              const canvas = document.createElement('canvas');
              canvas.id = `live-texture-${rows.length}`;
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
            (async () => {
              try {
                await Promise.all(pending);
                if (message.exitCode !== 0) {
                  throw new Error(`Live Texture worker exited with ${message.exitCode}`);
                }
                clearTimeout(timeout);
                worker.terminate();
                resolve(rows);
              } catch (error) {
                fail(error);
              }
            })();
          }
        };
      });
    });

    assert.equal(liveTextures.length, 3, 'Live Worker must render all three exact Texture targets');
    assert.equal(new Set(liveTextures.map(item => item.path)).size, 3, 'Live Worker must not substitute duplicate Texture targets');

    const expectedLiveByPath = new Map(
      result.textureFixtures.map(item => [item.path, item])
    );

    for (const [index, item] of liveTextures.entries()) {
      const expected = expectedLiveByPath.get(item.path);
      assert.ok(expected, `Unexpected live Texture path: ${item.path}`);
      assert.equal(item.width, expected.width);
      assert.equal(item.height, expected.height);
      assert.equal(
        item.pixelsSha256,
        expected.pixelsSha256,
        'Live BuildPatch pixels must match the desktop and captured browser reference'
      );
      await page.locator(`#live-texture-${index}`).screenshot({
        path: `live-texture-${index}.png`
      });
    }

    assert.ok(
      liveManifestRequests > networkBeforeLiveTexture.manifestRequests,
      'Live Texture Worker did not fetch a current Fortnite manifest'
    );
    assert.ok(
      liveChunkRequests > networkBeforeLiveTexture.chunkRequests,
      'Live Texture Worker did not fetch current BuildPatch chunks'
    );
    assert.ok(
      liveChunkBytes > networkBeforeLiveTexture.chunkBytes,
      'Live Texture Worker fetched no additional BuildPatch bytes'
    );

    result.liveTextureFixtures = liveTextures;
    result.liveTextureParsingProven = true;
    result.liveTextureNetwork = {
      manifestRequests:
        liveManifestRequests - networkBeforeLiveTexture.manifestRequests,
      chunkRequests:
        liveChunkRequests - networkBeforeLiveTexture.chunkRequests,
      chunkBytes:
        liveChunkBytes - networkBeforeLiveTexture.chunkBytes
    };

    console.log(
      'LIVE_TEXTURE_WORKER_PROVEN',
      JSON.stringify({
        fixtures: liveTextures,
        network: result.liveTextureNetwork
      })
    );
  }

  if (process.env.REQUIRE_RELAY_TEXTURE === '1') {
    assert.ok(
      Array.isArray(result.textureFixtures) && result.textureFixtures.length === 3,
      'Relay Texture proof requires the established three references'
    );

    const live =
      await (liveManifestPromise ||= resolveRawManifest(LIVE_MANIFEST_ENDPOINT));

    const relayBefore = {
      requests: relayRequests,
      bytes: relayBytes
    };

    const relayTextures = await page.evaluate(
      async ({ manifestUrl, chunkBase }) => {
        return await new Promise((resolve, reject) => {
          const params = new URLSearchParams({
            test: 'live-texture-relay',
            manifest: manifestUrl,
            chunkBase,
            relay: '/edge/range'
          });

          const worker = new Worker(
            '/worker.js?' + params.toString(),
            { type: 'module' }
          );

          const pending = [];
          const rows = [];
          const timeout = setTimeout(() => {
            worker.terminate();
            reject(new Error('Relayed live Texture worker timed out'));
          }, 300000);

          const fail = error => {
            clearTimeout(timeout);
            worker.terminate();
            reject(error instanceof Error ? error : new Error(String(error)));
          };

          worker.onerror = event => {
            fail(new Error(event.message || 'Relayed live Texture worker failed'));
          };

          worker.onmessage = event => {
            const message = event.data || {};

            if (message.type === 'error') {
              fail(new Error(message.error || 'Relayed live Texture worker failed'));
              return;
            }

            if (message.type === 'pixels') {
              const task = (async () => {
                const { path, width, height, pixels } = message;

                if (!(pixels instanceof ArrayBuffer)) {
                  throw new Error('Relayed Texture worker did not transfer an ArrayBuffer');
                }

                if (
                  width <= 0 ||
                  height <= 0 ||
                  width * height > 65536 ||
                  pixels.byteLength !== width * height * 4
                ) {
                  throw new Error('Relayed Texture worker returned invalid RGBA pixels');
                }

                const hash = await crypto.subtle.digest('SHA-256', pixels);
                const pixelsSha256 = Array.from(
                  new Uint8Array(hash),
                  value => value.toString(16).padStart(2, '0')
                ).join('').toUpperCase();

                const canvas = document.createElement('canvas');
                canvas.id = `relay-texture-${rows.length}`;
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
              (async () => {
                try {
                  await Promise.all(pending);
                  if (message.exitCode !== 0) {
                    throw new Error(
                      `Relayed live Texture worker exited with ${message.exitCode}`
                    );
                  }
                  clearTimeout(timeout);
                  worker.terminate();
                  resolve(rows);
                } catch (error) {
                  fail(error);
                }
              })();
            }
          };
        });
      },
      {
        manifestUrl: live.source,
        chunkBase: LIVE_CHUNK_BASE.toString()
      }
    );

    assert.equal(relayTextures.length, 3);
    assert.equal(new Set(relayTextures.map(item => item.path)).size, 3);

    const expectedByPath =
      new Map(
        result.textureFixtures.map(item => [item.path, item])
      );

    for (const [index, item] of relayTextures.entries()) {
      const expected = expectedByPath.get(item.path);
      assert.ok(expected, `Unexpected relayed Texture path: ${item.path}`);
      assert.equal(item.width, expected.width);
      assert.equal(item.height, expected.height);
      assert.equal(
        item.pixelsSha256,
        expected.pixelsSha256,
        'Relayed public Fortnite bytes must decode to the established pixels'
      );

      await page.locator(`#relay-texture-${index}`).screenshot({
        path: `relay-texture-${index}.png`
      });
    }

    const relayNetwork = {
      requests: relayRequests - relayBefore.requests,
      bytes: relayBytes - relayBefore.bytes
    };

    assert.ok(relayNetwork.requests > 0, 'Relayed Texture proof never used the range relay');
    assert.ok(relayNetwork.bytes > 0, 'Relayed Texture proof transferred no relay bytes');

    result.relayTextureParsingProven = true;
    result.relayTextureFixtures = relayTextures;
    result.relayTextureNetwork = relayNetwork;

    console.log(
      'RELAY_TEXTURE_WORKER_PROVEN',
      JSON.stringify({
        fixtures: relayTextures,
        network: relayNetwork
      })
    );
  }

  if (process.env.REQUIRE_GENERIC_TEXTURE === '1') {
    const fixturePath =
      path.resolve(
        'tools/CUE4Parse.BrowserProbe/texture-fixture.json'
      );

    assert.ok(
      fs.existsSync(fixturePath),
      'Generic Texture proof requires the generated desktop fixture'
    );

    const fixtureDocument =
      JSON.parse(
        fs.readFileSync(
          fixturePath,
          'utf8'
        )
      );

    const target =
      fixtureDocument?.selected?.[0];

    assert.ok(
      target?.path &&
      target?.containerToc &&
      target?.pixelsSha256 &&
      target?.width > 0 &&
      target?.height > 0,
      'Generic Texture reference is missing path/container/pixel evidence'
    );

    const live =
      await (
        liveManifestPromise ||=
          resolveRawManifest(
            LIVE_MANIFEST_ENDPOINT
          )
      );

    const relayBeforeGeneric = {
      requests:
        relayRequests,
      bytes:
        relayBytes
    };

    const generic =
      await page.evaluate(
        async ({
          assetPath,
          containerToc,
          expectedWidth,
          expectedHeight,
          maxSize,
          manifestUrl,
          chunkBase
        }) => {
          return await new Promise(
            (resolve, reject) => {
              const params =
                new URLSearchParams({
                  test:
                    'resolve-texture-relay',
                  path:
                    assetPath,
                  toc:
                    containerToc,
                  manifest:
                    manifestUrl,
                  chunkBase,
                  mappingsApi:
                    'https://api.fortniteapi.com/v1/mappings',
                  aesApi:
                    'https://export-service-new.dillyapis.com/v1/aes',
                  maxSize:
                    String(maxSize),
                  relay:
                    '/edge/range'
                });

              const worker =
                new Worker(
                  '/worker.js?' +
                    params.toString(),
                  {
                    type:
                      'module'
                  }
                );

              const timeout =
                setTimeout(
                  () => {
                    worker.terminate();
                    reject(
                      new Error(
                        'Generic Texture worker timed out'
                      )
                    );
                  },
                  300000
                );

              const fail =
                error => {
                  clearTimeout(
                    timeout
                  );
                  worker.terminate();
                  reject(
                    error instanceof
                      Error
                      ? error
                      : new Error(
                          String(
                            error
                          )
                        )
                  );
                };

              let pixelsResult =
                null;

              worker.onerror =
                event => {
                  fail(
                    new Error(
                      event.message ||
                      'Generic Texture worker failed'
                    )
                  );
                };

              worker.onmessage =
                event => {
                  const message =
                    event.data ||
                    {};

                  if (
                    message.type ===
                    'error'
                  ) {
                    fail(
                      new Error(
                        message.error ||
                        'Generic Texture worker failed'
                      )
                    );
                    return;
                  }

                  if (
                    message.type ===
                    'pixels'
                  ) {
                    try {
                      const {
                        path:
                          returnedPath,
                        width,
                        height,
                        pixels
                      } =
                        message;

                      if (
                        !(
                          pixels instanceof
                          ArrayBuffer
                        )
                      ) {
                        throw new Error(
                          'Generic Texture worker did not transfer an ArrayBuffer'
                        );
                      }

                      if (
                        width <= 0 ||
                        height <= 0 ||
                        width >
                          2048 ||
                        height >
                          2048 ||
                        pixels.byteLength !==
                          width *
                            height *
                            4
                      ) {
                        throw new Error(
                          'Generic Texture worker returned invalid RGBA pixels'
                        );
                      }

                      if (
                        width !==
                          expectedWidth ||
                        height !==
                          expectedHeight
                      ) {
                        throw new Error(
                          'Generic Texture runtime selected a different preview mip'
                        );
                      }

                      pixelsResult = {
                        path:
                          returnedPath,
                        width,
                        height,
                        pixels
                      };
                    } catch (
                      error
                    ) {
                      fail(error);
                    }

                    return;
                  }

                  if (
                    message.type ===
                    'done'
                  ) {
                    (async () => {
                      try {
                        if (
                          message.exitCode !==
                          0
                        ) {
                          throw new Error(
                            `Generic Texture worker exited with ${message.exitCode}`
                          );
                        }

                        if (
                          !pixelsResult
                        ) {
                          throw new Error(
                            'Generic Texture worker completed without pixels'
                          );
                        }

                        const hash =
                          await crypto.subtle
                            .digest(
                              'SHA-256',
                              pixelsResult
                                .pixels
                            );

                        const pixelsSha256 =
                          Array.from(
                            new Uint8Array(
                              hash
                            ),
                            value =>
                              value
                                .toString(
                                  16
                                )
                                .padStart(
                                  2,
                                  '0'
                                )
                          )
                            .join('')
                            .toUpperCase();

                        const canvas =
                          document
                            .createElement(
                              'canvas'
                            );

                        canvas.id =
                          'generic-texture-0';

                        canvas.width =
                          pixelsResult
                            .width;

                        canvas.height =
                          pixelsResult
                            .height;

                        canvas
                          .getContext(
                            '2d'
                          )
                          .putImageData(
                            new ImageData(
                              new Uint8ClampedArray(
                                pixelsResult
                                  .pixels
                              ),
                              pixelsResult
                                .width,
                              pixelsResult
                                .height
                            ),
                            0,
                            0
                          );

                        document.body
                          .append(
                            canvas
                          );

                        clearTimeout(
                          timeout
                        );

                        worker
                          .terminate();

                        resolve({
                          path:
                            pixelsResult
                              .path,
                          width:
                            pixelsResult
                              .width,
                          height:
                            pixelsResult
                              .height,
                          pixelsSha256
                        });
                      } catch (
                        error
                      ) {
                        fail(error);
                      }
                    })();
                  }
                };
            }
          );
        },
        {
          assetPath:
            target.path,
          containerToc:
            target.containerToc,
          expectedWidth:
            target.width,
          expectedHeight:
            target.height,
          maxSize:
            Math.max(
              target.width,
              target.height
            ),
          manifestUrl:
            live.source,
          chunkBase:
            LIVE_CHUNK_BASE
              .toString()
        }
      );

    assert.equal(
      generic.path
        .toLowerCase(),
      target.path
        .toLowerCase(),
      'Generic Texture runtime returned a different asset path'
    );

    assert.equal(
      generic.width,
      target.width
    );

    assert.equal(
      generic.height,
      target.height
    );

    assert.equal(
      generic.pixelsSha256,
      target.pixelsSha256,
      'Generic live Texture runtime pixels differ from desktop CUE4Parse'
    );

    const genericRelayNetwork = {
      requests:
        relayRequests -
        relayBeforeGeneric
          .requests,
      bytes:
        relayBytes -
        relayBeforeGeneric
          .bytes
    };

    assert.ok(
      genericRelayNetwork
        .requests >
        0,
      'Generic Texture runtime never used the bounded range relay'
    );

    assert.ok(
      genericRelayNetwork
        .bytes >
        0,
      'Generic Texture runtime transferred no relay bytes'
    );

    await page
      .locator(
        '#generic-texture-0'
      )
      .screenshot({
        path:
          'generic-texture-0.png'
      });

    result.genericTextureParsingProven =
      true;

    result.genericTextureFixture =
      generic;

    result.genericTextureNetwork =
      genericRelayNetwork;

    console.log(
      'GENERIC_TEXTURE_WORKER_PROVEN',
      JSON.stringify({
        fixture:
          generic,
        network:
          genericRelayNetwork
      })
    );
  }

  if (process.env.REQUIRE_PUBLIC_SOURCE_CORS === '1') {
    const live = await (liveManifestPromise ||= resolveRawManifest(LIVE_MANIFEST_ENDPOINT));
    assert.ok(liveChunkPaths.length > 0, 'No live BuildPatch chunk path was observed for CORS proof');

    const nodeRangeProbe = async (url, label) => {
      const response = await fetch(url, {
        method: 'GET',
        redirect: 'error',
        headers: {
          range: 'bytes=0-255',
          accept: 'application/octet-stream,*/*;q=0.8'
        }
      });
      const bytes = new Uint8Array(await response.arrayBuffer());
      return {
        label,
        status: response.status,
        bytes: bytes.byteLength,
        contentRange: response.headers.get('content-range'),
        contentLength: response.headers.get('content-length')
      };
    };

    const mappingsMetadataForRange = await fetch(
      'https://api.fortniteapi.com/v1/mappings',
      { headers: { accept: 'application/json' } }
    ).then(response => {
      if (!response.ok) throw new Error(`Mappings metadata range preflight returned HTTP ${response.status}`);
      return response.json();
    });

    const findMappingUrlForRange = root => {
      let found = '';
      const walk = value => {
        if (found) return;
        if (typeof value === 'string') {
          if (/^https:\/\//i.test(value) && /usmap/i.test(value)) found = value;
          return;
        }
        if (Array.isArray(value)) {
          for (const item of value) walk(item);
          return;
        }
        if (value && typeof value === 'object') {
          for (const item of Object.values(value)) walk(item);
        }
      };
      walk(root);
      return found;
    };

    const mappingUrlForRange = findMappingUrlForRange(mappingsMetadataForRange);
    assert.ok(mappingUrlForRange, 'Mappings metadata exposed no HTTPS usmap URL for origin range proof');

    const originRanges = {
      rawManifest: await nodeRangeProbe(live.source, 'Raw Fortnite manifest'),
      mappingsFile: await nodeRangeProbe(mappingUrlForRange, 'Fortnite mappings file'),
      buildPatchChunk: await nodeRangeProbe(
        new URL(liveChunkPaths[0], LIVE_CHUNK_BASE).toString(),
        'Epic BuildPatch chunk'
      )
    };

    result.publicSourceOriginRanges = originRanges;
    console.log('PUBLIC_SOURCE_ORIGIN_RANGE_MATRIX', JSON.stringify(originRanges));

    for (const item of Object.values(originRanges)) {
      assert.equal(item.status, 206, `${item.label} must support bounded HTTP range reads`);
      assert.ok(item.bytes > 0 && item.bytes <= 256, `${item.label} range body exceeded probe budget`);
      assert.match(
        item.contentRange || '',
        /^bytes\s+0-\d+\/\d+$/i,
        `${item.label} omitted a usable Content-Range`
      );
    }

    const cors = await page.evaluate(async ({ rawManifestUrl, chunkUrl }) => {
      const settle = async (label, task) => {
        try {
          return { ok: true, label, ...(await task()) };
        } catch (error) {
          return {
            ok: false,
            label,
            error: error?.message || String(error)
          };
        }
      };

      const fetchJson = async (url, label) => {
        const response = await fetch(url, {
          mode: 'cors',
          credentials: 'omit',
          cache: 'no-store',
          headers: { accept: 'application/json,*/*;q=0.8' }
        });
        if (!response.ok) throw new Error(`${label} returned HTTP ${response.status}`);
        const text = await response.text();
        if (text.length > 4 * 1024 * 1024) throw new Error(`${label} exceeded JSON budget`);
        return { status: response.status, json: JSON.parse(text) };
      };

      const probeRange = async (url, label) => {
        const response = await fetch(url, {
          mode: 'cors',
          credentials: 'omit',
          cache: 'no-store',
          headers: {
            range: 'bytes=0-255',
            accept: 'application/octet-stream,*/*;q=0.8'
          }
        });
        if (!response.ok) throw new Error(`${label} returned HTTP ${response.status}`);
        const reader = response.body?.getReader?.();
        if (!reader) {
          const bytes = new Uint8Array(await response.arrayBuffer());
          if (!bytes.byteLength) throw new Error(`${label} returned no bytes`);
          return { status: response.status, bytes: bytes.byteLength };
        }
        const first = await reader.read();
        await reader.cancel('cors-probe-complete').catch(() => {});
        if (first.done || !first.value?.byteLength) throw new Error(`${label} returned no bytes`);
        return { status: response.status, bytes: first.value.byteLength };
      };

      const findUsmap = root => {
        let found = '';
        const walk = value => {
          if (found) return;
          if (typeof value === 'string') {
            if (/^https:\/\//i.test(value) && /usmap/i.test(value)) found = value;
            return;
          }
          if (Array.isArray(value)) {
            for (const item of value) walk(item);
            return;
          }
          if (value && typeof value === 'object') {
            for (const item of Object.values(value)) walk(item);
          }
        };
        walk(root);
        return found;
      };

      const results = {};

      results.manifestMetadata = await settle(
        'Dilly manifest metadata',
        async () => {
          const value = await fetchJson(
            'https://export-service-new.dillyapis.com/v1/manifests',
            'Dilly manifest metadata'
          );
          return { status: value.status };
        }
      );

      results.aesMetadata = await settle(
        'Dilly AES metadata',
        async () => {
          const value = await fetchJson(
            'https://export-service-new.dillyapis.com/v1/aes',
            'Dilly AES metadata'
          );
          return { status: value.status };
        }
      );

      let mappingUrl = '';
      results.mappingsMetadata = await settle(
        'FortniteAPI mappings metadata',
        async () => {
          const value = await fetchJson(
            'https://api.fortniteapi.com/v1/mappings',
            'FortniteAPI mappings metadata'
          );
          mappingUrl = findUsmap(value.json);
          if (!mappingUrl) throw new Error('Mappings metadata exposed no HTTPS usmap URL');
          return { status: value.status, mappingUrl };
        }
      );

      results.rawManifest = await settle(
        'Raw Fortnite manifest',
        () => probeRange(rawManifestUrl, 'Raw Fortnite manifest')
      );

      results.mappingsFile = mappingUrl
        ? await settle(
            'Fortnite mappings file',
            () => probeRange(mappingUrl, 'Fortnite mappings file')
          )
        : {
            ok: false,
            label: 'Fortnite mappings file',
            error: 'Mappings metadata did not provide a usable URL'
          };

      results.buildPatchChunk = await settle(
        'Epic BuildPatch chunk',
        () => probeRange(chunkUrl, 'Epic BuildPatch chunk')
      );

      return results;
    }, {
      rawManifestUrl: live.source,
      chunkUrl: new URL(liveChunkPaths[0], LIVE_CHUNK_BASE).toString()
    });

    result.publicSourceCors = cors;
    console.log('PUBLIC_SOURCE_CORS_MATRIX', JSON.stringify(cors));

    const failedCors = Object.values(cors).filter(item => !item?.ok);
    result.publicSourceCorsProven = true;
    result.publicSourceDirectReady = failedCors.length === 0;
    result.publicSourceRelayRequired = failedCors.map(item => item.label);

    console.log(
      result.publicSourceDirectReady
        ? 'PUBLIC_SOURCE_CORS_DIRECT_READY'
        : 'PUBLIC_SOURCE_CORS_RELAY_REQUIRED',
      JSON.stringify({
        directReady: result.publicSourceDirectReady,
        relayRequired: result.publicSourceRelayRequired
      })
    );
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
  assert.match(
    [cancelled.error || '', ...logs.slice(-40)].join('\n'),
    /(?:OperationCanceled|TaskCanceled|AggregateException[^\n]*TaskCanceled)/,
    'Cancelled CTR must surface only as a cancellation failure'
  );
  result.ctrPreCancellationProven = true;
  console.log('AES_CTR_CANCELLATION_PROVEN_AT_BROWSER_BOUNDARY');
} finally {
  fs.writeFileSync('browser-runtime-proof.json', JSON.stringify({result, logs, wasmBytes}, null, 2));
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}
