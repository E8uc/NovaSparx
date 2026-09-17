# NovaSparx

NovaSparx is a web-first Fortnite asset preview backend. The user does **not** need Fortnite, FModel, UEFN, or local Fortnite files installed.

The backend discovers and converts live Fortnite assets with CUE4Parse. Textures are decoded to PNG by the backend. Mesh geometry is transferred in a compact binary package and the **user's own browser/device renders the 3D preview**. NovaSparx never uses one visitor's device to process another visitor's request.

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
NovaSparx backend
  - live Fortnite manifest + Fortnite_Studio
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

The backend keeps hard mesh, texture, timeout, and response-size limits so one asset cannot exhaust the service. `NOVASPARX_CLIENT_PACKAGE_MAX_BYTES` can tune the browser package ceiling; the default is 48 MiB and the accepted range is 8–60 MiB.

If an asset cannot be represented honestly, NovaSparx returns metadata/references rather than inventing a fake preview.
