// The same module boots in a page and a dedicated Worker. Only the Worker owns
// the .NET heap; terminating a replaced request interrupts synchronous parsing.
if (typeof document === 'undefined') {
  self.onmessage = async ({ data: { id, test } }) => {
    self.onmessage = null;
    try {
      const { dotnet } = await import('./_framework/dotnet.js');
      const { runMain, getConfig, setModuleImports } = await dotnet.withDiagnosticTracing(false).create();
      setModuleImports('texture-view', { render(width, height, encoded, path) {
        if (!Number.isInteger(width) || !Number.isInteger(height) || width <= 0 || height <= 0 || width * height > 65536 || encoded.length > 349528)
          throw new Error('Texture pixel budget exceeded');
        const raw = Uint8Array.from(atob(encoded), c => c.charCodeAt(0));
        if (raw.length !== width * height * 4) throw new Error('RGBA byte count mismatch');
        self.postMessage({ id, kind: 'pixels', width, height, path, buffer: raw.buffer }, [raw.buffer]);
      }});
      self.postMessage({ id, kind: 'runtime-ready' });
      const args = test === 'reject-partial-block' ? ['--reject-partial-block'] : test === 'cancel-ctr' ? ['--cancel-ctr'] : [];
      const exitCode = await runMain(getConfig().mainAssemblyName, args);
      self.postMessage({ id, kind: 'complete', state: exitCode === 0 ? 'ready' : 'failed', exitCode,
        wasmResources: performance.getEntriesByType('resource').filter(r => r.name.endsWith('.wasm')).map(r => ({ url: r.name, bytes: r.encodedBodySize })) });
    } catch (error) {
      self.postMessage({ id, kind: 'complete', state: 'failed', error: String(error?.stack || error) });
    }
  };
} else {
  let generation = 0;
  let active = null;
  const view = document.createElement('section');
  document.body.append(view);
  globalThis.probeLifecycle = { started: 0, terminated: 0, runtimeReady: 0, staleMessages: 0 };
  globalThis.startCue4ParseProbe = (test = '', { signal } = {}) => {
    active?.cancel();
    const id = ++generation;
    view.replaceChildren();
    globalThis.nativeTexturePromises = [];
    globalThis.cue4parseProbe = { state: 'starting', requestId: id, assetParsingProven: false, worker: true };
    if (signal?.aborted) {
      globalThis.cue4parseProbe.state = 'cancelled';
      return Promise.reject(new DOMException('Request cancelled', 'AbortError'));
    }
    return new Promise((resolve, reject) => {
      const worker = new Worker(new URL(import.meta.url), { type: 'module' });
      globalThis.probeLifecycle.started++;
      let settled = false;
      const dispose = () => {
        worker.onmessage = null; worker.onerror = null;
        worker.terminate(); globalThis.probeLifecycle.terminated++;
        signal?.removeEventListener('abort', cancel);
        if (active?.id === id) active = null;
      };
      const cancel = () => {
        if (settled) return;
        settled = true; dispose();
        if (id === generation) globalThis.cue4parseProbe.state = 'cancelled';
        reject(new DOMException('Request replaced or cancelled', 'AbortError'));
      };
      const fail = error => {
        if (settled) return;
        settled = true; dispose();
        globalThis.cue4parseProbe = { state: 'failed', error: String(error), requestId: id, assetParsingProven: false, worker: true };
        resolve(globalThis.cue4parseProbe);
      };
      active = { id, cancel };
      signal?.addEventListener('abort', cancel, { once: true });
      worker.onerror = event => { event.preventDefault(); fail(event.message); };
      worker.onmessage = ({ data }) => {
        if (settled || id !== generation || data.id !== id) { globalThis.probeLifecycle.staleMessages++; return; }
        if (data.kind === 'runtime-ready') { globalThis.probeLifecycle.runtimeReady++; return; }
        if (data.kind === 'pixels') {
          const { width, height, buffer, path } = data;
          if (!Number.isInteger(width) || !Number.isInteger(height) || width <= 0 || height <= 0 || width * height > 65536 ||
              !(buffer instanceof ArrayBuffer) || buffer.byteLength !== width * height * 4 || globalThis.nativeTexturePromises.length >= 3) {
            fail('Invalid or excessive Worker pixel output'); return;
          }
          const canvas = document.createElement('canvas');
          canvas.id = `native-texture-${globalThis.nativeTexturePromises.length}`;
          canvas.dataset.requestId = String(id); canvas.width = width; canvas.height = height;
          canvas.getContext('2d').putImageData(new ImageData(new Uint8ClampedArray(buffer), width, height), 0, 0);
          const label = document.createElement('p'); label.textContent = path;
          view.append(label, canvas);
          globalThis.nativeTexturePromises.push(crypto.subtle.digest('SHA-256', buffer).then(hash => ({ path, width, height,
            pixelsSha256: Array.from(new Uint8Array(hash), n => n.toString(16).padStart(2, '0')).join('').toUpperCase() })));
        } else if (data.kind === 'complete') {
          settled = true; dispose();
          globalThis.cue4parseProbe = { ...data, requestId: id, assetParsingProven: false, worker: true };
          resolve(globalThis.cue4parseProbe);
        }
      };
      worker.postMessage({ id, test });
    });
  };
  globalThis.startCue4ParseProbe(new URLSearchParams(location.search).get('test') || '').catch(error => {
    if (error.name !== 'AbortError') console.error(error);
  });
}
