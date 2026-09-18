# NovaSparx

NovaSparx is a web-first Fortnite asset preview backend. The user does **not** need Fortnite, FModel, UEFN, or local Fortnite files installed.

NovaSparx uses a layered browser-first runtime. The user's own device is preferred whenever it already has enough validated data; server parsing remains a compatibility/fallback layer while the browser parser is expanded. Mesh rendering always happens on the requesting user's device. NovaSparx never uses one visitor's device to process another visitor's request.

## Web rendering architecture

```text
Browser / phone / tablet / desktop
        |
        |  asset path
        v
Cloudflare / E8 edge
        |
        |  private NovaLink or authenticated origin request
        v
NovaSparx fallback backend
  - live Fortnite manifest
  - optional Fortnite_Studio / streamed texture TOCs
  - mappings / AES / IoStore OnDemand
  - CUE4Parse asset loading
  - texture decode
  - mesh extraction only
        |
        |  compact NSMESH1 geometry + material references
        v
User's browser
  - WebGL rendering
  - orbit / zoom
  - material texture loading
  - adaptive pixel ratio for mobile devices
  - PNG capture
```

This keeps GPU rendering on the device that requested the preview instead of on the small backend.

## Layered runtime

NovaSparx does not depend on one all-or-nothing path. The web runtime tries independent layers in order and falls through safely:

1. **Device memory** — reuse a validated mesh already open in the current page.
2. **Device cache** — reuse a validated NSMESH1 package stored by that browser. Repeat views can work without asking the parsing backend again.
3. **Browser parser / WebAssembly contract** — a local parser can register as `NovaSparxLocalParser` or `NovaSparxWasm`. This is the migration point for pure client-side Fortnite parsing. It is reported unavailable until a real parser engine is registered; the UI never pretends this layer is active.
4. **Backend binary** — CUE4Parse extracts a compact `NSMESH1` package; the requesting device parses/renders it.
5. **Backend JSON compatibility** — older `/v1/resolve` fallback.
6. **Universal references / texture preview** — verified referenced visual data when direct mesh output is not available.
7. **Evidence fallback** — truthful metadata instead of fabricated art.

`web/novasparx-browser-transport.js` provides bounded HTTP Range reads (maximum 4 MiB per request) for future browser/WASM parsers. `web/novasparx-local-parser.js` is the stable local-parser registration contract, and `web/novasparx-layers.js` owns layer selection, device cache limits, and fallback tracing.

This lets NovaSparx move work from hosted RAM to each requesting device incrementally instead of requiring a risky rewrite in one step.

## Browser memory guard

The browser runtime treats mobile memory as a hard safety boundary instead of
assuming the user's device can absorb server-sized workloads.

- iOS mesh packages are capped at 8 MiB before `arrayBuffer()`; other mobile
  devices use 12 MiB.
- Geometry is validated before WebGL allocation (100k vertices / 300k indices on
  iOS, 160k / 480k on other mobile devices).
- Mobile rendering lowers framebuffer size, material count, texture count and
  mipmap use.
- WebGL contexts are explicitly released after PNG capture and temporary
  canvases are shrunk immediately.
- device cache writes check storage headroom and mobile cache counts are small.
- page lifecycle changes cancel active requests and release in-memory mesh
  caches.
- when a browser exposes `measureUserAgentSpecificMemory()`, NovaSparx can use
  it as an extra signal; it is never required for safety.

No browser API can reliably predict every future tab/process kill, especially on
WebKit, so the guard uses conservative pre-allocation budgets rather than
claiming crash-proof execution.

## Type-safe visual associations

NovaSparx treats asset class as evidence. Similar filenames alone are never
allowed to change the asset family.

- Texture -> texture/image only.
- Material -> verified texture dependencies, never an arbitrary mesh.
- Blueprint -> mesh references found in the Blueprint export/dependency data.
- Mesh -> direct mesh, optionally annotated with a verified Blueprint referencer.
- Unknown -> metadata/reference fallback unless type evidence becomes available.

For reverse Mesh -> Blueprint relationships the preferred zero-runtime-RAM
layer is a sharded index generated from Unreal AssetRegistry referencers in
GitHub Actions. If the current Fortnite delivery does not expose
`AssetRegistry.bin`, the generator publishes an unavailable manifest and the
browser falls back to FNAA path shortlisting plus exact Blueprint export-JSON
reference verification. This keeps the hosted backend out of the reverse-index
work and avoids loading a global reference graph into a phone or small
container.

## Browser mesh endpoint

`GET /v1/client-mesh?path=/Game/...`

The route is protected by the same server-to-server bearer token as the other `/v1` routes. Do not expose that shared token in frontend JavaScript. A public E8/Cloudflare gateway should authenticate/rate-limit the visitor and proxy only allowed operations.

Content type:

```text
application/vnd.novasparx.mesh-v1
```

The binary payload starts with `NSMESH1\0`, followed by a JSON header and typed-array geometry. This avoids transferring hundreds of thousands of floating-point values as oversized JSON.

Textures continue to use:

`GET /v1/texture?path=/Game/...`

## Browser renderer

`web/novasparx-viewer.js` renders the package on the visitor's device with WebGL through a pinned Three.js build. `web/novasparx-format.js` contains the dependency-free binary parser.

The viewer supports:

- StaticMesh and SkeletalMesh preview geometry returned by NovaSparx
- sections and multiple materials
- base-color, normal, emissive, and opacity textures when available
- roughness, metallic, transparency, vertex colors, and two-sided materials
- mouse, touch, and pen orbit controls
- zoom and responsive canvas sizing
- adaptive device pixel ratio for weaker mobile devices
- PNG capture with `toPngBlob()`

Example integration behind a same-origin E8 gateway:

```js
import { NovaSparxViewer } from './novasparx-viewer.js';

const viewer = new NovaSparxViewer(document.querySelector('canvas'), {
  textureUrlForPath(path) {
    return `/api/nova/texture?path=${encodeURIComponent(path)}`;
  },
});

await viewer.loadFromUrl(
  `/api/nova/client-mesh?path=${encodeURIComponent(assetPath)}`,
);
```

The gateway paths above are placeholders. The private backend token belongs at the edge/backend boundary, never in the page.

## API

NovaSparx keeps the previous operations for compatibility and adds the compact client mesh route:

- `/health`
- `/v1/health`
- `/v1/warmup`
- `/v1/refresh`
- `/v1/resolve?path=...`
- `/v1/preview?path=...`
- `/v1/client-mesh?path=...`
- `/v1/inspect?path=...`
- `/v1/references?path=...`
- `/v1/texture?path=...`

`/v1/resolve` remains JSON for older consumers. New browser previews should prefer `/v1/client-mesh` because it is substantially more compact for large meshes.

## Limits

The backend keeps hard mesh, texture, timeout, and response-size limits so one asset cannot exhaust the service. The current low-memory deployment profile caps client packages at 24 MiB, disables expensive optional provider features by default, and keeps the accepted configurable range at 8–60 MiB. Device caching independently refuses packages above 12 MiB on phones/tablets and 24 MiB on desktop.

If an asset cannot be represented honestly, NovaSparx returns metadata/references rather than inventing a fake preview.
