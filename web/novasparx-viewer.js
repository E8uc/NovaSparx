import * as THREE from 'https://cdn.jsdelivr.net/npm/three@0.180.0/build/three.module.min.js';
import { parseNovaMesh, recommendedPixelRatio } from './novasparx-format.js';

export class NovaSparxViewer {
  constructor(canvas, options = {}) {
    if (!canvas?.getContext) throw new TypeError('NovaSparxViewer requires a canvas.');
    this.canvas = canvas;
    this.guard =
      globalThis.NovaSparxBrowserGuard ||
      null;

    this.options = {
      textureUrlForPath: options.textureUrlForPath || null,
      fetch: options.fetch || globalThis.fetch?.bind(globalThis),
      fetchOptions: options.fetchOptions || { credentials: 'same-origin' },
      pixelRatio: Number(options.pixelRatio || recommendedPixelRatio()),
      background: options.background ?? 0x0e0f13,
      onWarning: typeof options.onWarning === 'function' ? options.onWarning : () => {},
    };

    this.renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: false, preserveDrawingBuffer: true });

    const guardState =
      this.guard?.status?.() || {};

    const maxPixelRatio =
      guardState.isMobile
        ? 1.5
        : 2.5;

    this.renderer.setPixelRatio(
      Math.max(
        1,
        Math.min(
          this.options.pixelRatio,
          maxPixelRatio
        )
      )
    );
    this.renderer.setClearColor(this.options.background, 1);
    this.renderer.outputColorSpace = THREE.SRGBColorSpace;
    this.scene = new THREE.Scene();
    this.camera = new THREE.PerspectiveCamera(45, 1, 0.01, 100);
    this.camera.position.set(1.5, 0.6, 2.4);
    this.scene.add(new THREE.HemisphereLight(0xffffff, 0x303040, 1.6));
    const key = new THREE.DirectionalLight(0xffffff, 2.1);
    key.position.set(2, 3, 4);
    this.scene.add(key);
    this.root = new THREE.Group();
    this.scene.add(this.root);
    this.target = new THREE.Vector3();
    this.yaw = 0.55;
    this.pitch = 0.2;
    this.distance = 2.8;
    this.pointer = null;
    this.frame = 0;
    this.textureCache = new Map();

    this.contextLost =
      () => {
        this.guard?.setPressure?.(
          "high",
          "webgl-context-lost"
        );

        this.options.onWarning(
          "NovaSparx paused rendering because the browser lost its WebGL context."
        );
      };

    this.canvas.addEventListener(
      "webglcontextlost",
      this.contextLost
    );

    this.#events();
    this.resizeObserver = new ResizeObserver(() => this.requestRender());
    this.resizeObserver.observe(canvas);
    this.requestRender();
  }

  async loadFromUrl(url, fetchOptions) {
    if (!this.options.fetch) throw new Error('Fetch is unavailable.');
    const response = await this.options.fetch(url, fetchOptions || this.options.fetchOptions);

    this.guard
      ?.assertResponseBudget?.(
        response,
        "mesh"
      );

    if (!response.ok) {
      let message = `NovaSparx mesh request failed (${response.status}).`;
      try { message = (await response.json())?.error || message; } catch {}
      throw new Error(message);
    }
    return this.loadPackage(await response.arrayBuffer());
  }

  async loadPackage(buffer) {
    const parsed = parseNovaMesh(buffer);
    this.#clearModel();
    const { header, geometry } = parsed;

    const guardManifest = {
      geometry,
      metadata: {
        vertexCount:
          geometry.positions.length / 3
      }
    };

    this.guard
      ?.assertManifestBudget?.(
        guardManifest
      );

    const policy =
      this.guard
        ?.renderPolicy?.(
          guardManifest
        ) || {};

    this.textureModes =
      new Set(
        policy.textureModes ||
        [
          "base",
          "normal",
          "emissive",
          "opacity"
        ]
      );

    this.remainingTextureLoads =
      Number(
        policy.maxTextureLoads
      ) ||
      24;
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(geometry.positions, 3));
    g.setAttribute('normal', new THREE.BufferAttribute(geometry.normals, 3));
    g.setAttribute('uv', new THREE.BufferAttribute(geometry.uv0, 2));
    if (geometry.colors) g.setAttribute('color', new THREE.BufferAttribute(geometry.colors, 4));
    g.setIndex(new THREE.BufferAttribute(geometry.indices, 1));

    const materials = await Promise.all((header.materials || []).map(m => this.#material(m)));
    if (!materials.length) materials.push(new THREE.MeshStandardMaterial({ color: 0xffffff, roughness: 0.62 }));
    for (const section of header.sections || [])
      g.addGroup(section.firstIndex, section.indexCount, Math.max(0, section.materialIndex || 0));

    const mesh = new THREE.Mesh(g, materials);
    const center = header.bounds?.center || [0, 0, 0];
    const radius = Math.max(Number(header.bounds?.radius || 1), 0.0001);
    mesh.position.set(
      -center[0] / radius,
      -center[1] / radius,
      -center[2] / radius
    );

    mesh.scale.setScalar(1 / radius);
    this.root.add(mesh);
    this.model = mesh;
    this.resetView();
    return header;
  }

  async #material(source) {
    const color = source.baseColor || [1, 1, 1, 1];
    const material = new THREE.MeshStandardMaterial({
      color: new THREE.Color(color[0], color[1], color[2]),
      roughness: finite01(source.roughness, 0.62),
      metalness: finite01(source.metallic, 0),
      opacity: finite01(source.opacity, 1) * finite01(color[3], 1),
      transparent: String(source.opacityMode || '').toLowerCase() === 'translucent',
      alphaTest: String(source.opacityMode || '').toLowerCase() === 'masked'
        ? finite01(source.opacityCutoff, 0.333) : 0,
      side: source.twoSided ? THREE.DoubleSide : THREE.FrontSide,
      vertexColors: Boolean(source.useVertexColor),
    });

    const emissive = source.emissiveColor || [0, 0, 0, 1];
    material.emissive = new THREE.Color(emissive[0], emissive[1], emissive[2]);
    material.emissiveIntensity = 1;

    const slots = [
      ['base', 'map', source.baseColorTexture, THREE.SRGBColorSpace],
      ['normal', 'normalMap', source.normalTexture, THREE.NoColorSpace],
      ['emissive', 'emissiveMap', source.emissiveTexture, THREE.SRGBColorSpace],
      ['opacity', 'alphaMap', source.opacityTexture, THREE.NoColorSpace],
    ];

    await Promise.all(slots.map(async ([mode, property, path, colorSpace]) => {
      if (
        !path ||
        !this.textureModes?.has(mode) ||
        this.remainingTextureLoads <= 0
      ) return;

      this.remainingTextureLoads--;

      const texture = await this.#texture(path, colorSpace);
      if (texture) material[property] = texture;
    }));
    material.needsUpdate = true;
    return material;
  }

  async #texture(path, colorSpace) {
    const cached = this.textureCache.get(path);
    if (cached) return cached;
    if (!this.options.textureUrlForPath || !this.options.fetch) return null;

    try {
      const url = await this.options.textureUrlForPath(path);
      if (!url) return null;
      const response = await this.options.fetch(url, this.options.fetchOptions);
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const bitmap = await createImageBitmap(await response.blob());
      const texture = new THREE.CanvasTexture(bitmap);
      texture.colorSpace = colorSpace;
      texture.flipY = false;
      texture.wrapS = texture.wrapT = THREE.RepeatWrapping;
      texture.needsUpdate = true;
      this.textureCache.set(path, texture);
      return texture;
    } catch (error) {
      this.options.onWarning(`Texture could not be loaded: ${path}`, error);
      return null;
    }
  }

  resetView() {
    this.yaw = 0.55;
    this.pitch = 0.2;
    this.distance = 2.8;
    this.requestRender();
  }

  requestRender() {
    if (this.frame) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = 0;
      const width = Math.max(1, this.canvas.clientWidth || 1);
      const height = Math.max(1, this.canvas.clientHeight || 1);
      this.renderer.setSize(width, height, false);
      this.camera.aspect = width / height;
      this.camera.updateProjectionMatrix();
      const cp = Math.cos(this.pitch);
      this.camera.position.set(
        Math.sin(this.yaw) * cp * this.distance,
        Math.sin(this.pitch) * this.distance,
        Math.cos(this.yaw) * cp * this.distance,
      );
      this.camera.lookAt(this.target);
      this.renderer.render(this.scene, this.camera);
    });
  }

  async toPngBlob() {
    this.requestRender();
    await new Promise(resolve => requestAnimationFrame(resolve));
    return new Promise((resolve, reject) => this.canvas.toBlob(
      blob => blob ? resolve(blob) : reject(new Error('PNG capture failed.')), 'image/png',
    ));
  }

  #events() {
    this.canvas.style.touchAction = 'none';
    this.down = event => {
      if (event.button !== undefined && event.button !== 0) return;
      this.pointer = { id: event.pointerId, x: event.clientX, y: event.clientY };
      this.canvas.setPointerCapture?.(event.pointerId);
    };
    this.move = event => {
      if (!this.pointer || this.pointer.id !== event.pointerId) return;
      this.yaw += (event.clientX - this.pointer.x) * 0.008;
      this.pitch = Math.max(-1.45, Math.min(1.45, this.pitch + (event.clientY - this.pointer.y) * 0.008));
      this.pointer.x = event.clientX; this.pointer.y = event.clientY;
      this.requestRender();
    };
    this.up = event => { if (this.pointer?.id === event.pointerId) this.pointer = null; };
    this.wheel = event => {
      event.preventDefault();
      this.distance = Math.max(1.25, Math.min(8, this.distance * Math.exp(event.deltaY * 0.001)));
      this.requestRender();
    };
    this.canvas.addEventListener('pointerdown', this.down);
    this.canvas.addEventListener('pointermove', this.move);
    this.canvas.addEventListener('pointerup', this.up);
    this.canvas.addEventListener('pointercancel', this.up);
    this.canvas.addEventListener('wheel', this.wheel, { passive: false });
  }

  #clearModel() {
    if (!this.model) return;
    this.model.geometry.dispose();
    const materials = Array.isArray(this.model.material) ? this.model.material : [this.model.material];
    for (const material of materials) material.dispose();
    this.root.remove(this.model);
    this.model = null;
  }

  dispose() {
    this.resizeObserver?.disconnect();
    if (this.frame) cancelAnimationFrame(this.frame);
    this.#clearModel();
    for (const texture of this.textureCache.values()) texture.dispose();
    this.textureCache.clear();
    this.renderer.dispose();
    this.renderer.forceContextLoss?.();

    this.canvas.removeEventListener(
      "webglcontextlost",
      this.contextLost
    );

    this.canvas.removeEventListener('pointerdown', this.down);
    this.canvas.removeEventListener('pointermove', this.move);
    this.canvas.removeEventListener('pointerup', this.up);
    this.canvas.removeEventListener('pointercancel', this.up);
    this.canvas.removeEventListener('wheel', this.wheel);
  }
}

function finite01(value, fallback) {
  const number = Number(value);
  return Number.isFinite(number) ? Math.max(0, Math.min(1, number)) : fallback;
}
