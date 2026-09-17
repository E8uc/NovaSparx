import assert from 'node:assert/strict';
import {
  parseNovaMesh,
  recommendedRenderQuality,
} from './novasparx-format.js';

const encoder = new TextEncoder();
const header = {
  schema: 'novasparx.client-mesh.v1',
  arrays: {
    positions: { byteOffset: 0, byteLength: 36, count: 3, components: 3, type: 'f32' },
    normals: { byteOffset: 36, byteLength: 36, count: 3, components: 3, type: 'f32' },
    tangents: { byteOffset: 72, byteLength: 48, count: 3, components: 4, type: 'f32' },
    uv0: { byteOffset: 120, byteLength: 24, count: 3, components: 2, type: 'f32' },
    indices: { byteOffset: 144, byteLength: 12, count: 3, components: 1, type: 'u32' },
  },
  sections: [],
  materials: [],
  bounds: { min: [0, 0, 0], max: [1, 1, 0], center: [0.5, 0.5, 0], radius: 1 },
};

const headerBytes = encoder.encode(JSON.stringify(header));
const paddedHeaderLength = (headerBytes.length + 3) & ~3;
const payloadLength = 156;
const buffer = new ArrayBuffer(16 + paddedHeaderLength + payloadLength);
const bytes = new Uint8Array(buffer);
bytes.set([0x4e, 0x53, 0x4d, 0x45, 0x53, 0x48, 0x31, 0x00], 0);
const data = new DataView(buffer);
data.setUint32(8, headerBytes.length, true);
data.setUint32(12, paddedHeaderLength, true);
bytes.set(headerBytes, 16);

const payload = 16 + paddedHeaderLength;
new Float32Array(buffer, payload, 9).set([
  0, 0, 0,
  1, 0, 0,
  0, 1, 0,
]);
new Float32Array(buffer, payload + 36, 9).set([
  0, 0, 1,
  0, 0, 1,
  0, 0, 1,
]);
new Float32Array(buffer, payload + 72, 12).set([
  1, 0, 0, 1,
  1, 0, 0, 1,
  1, 0, 0, 1,
]);
new Float32Array(buffer, payload + 120, 6).set([
  0, 0,
  1, 0,
  0, 1,
]);
new Uint32Array(buffer, payload + 144, 3).set([0, 1, 2]);

const parsed = parseNovaMesh(buffer);
assert.equal(parsed.header.schema, 'novasparx.client-mesh.v1');
assert.equal(parsed.geometry.positions.length, 9);
assert.equal(parsed.geometry.indices.length, 3);
assert.deepEqual(Array.from(parsed.geometry.indices), [0, 1, 2]);
assert.equal(
  recommendedRenderQuality({
    navigator: { deviceMemory: 4, hardwareConcurrency: 4 },
    matchMedia: () => ({ matches: true }),
  }),
  'mobile',
);

const broken = buffer.slice(0);
new Uint8Array(broken)[0] = 0;
assert.throws(() => parseNovaMesh(broken), /invalid signature/i);

console.log('NovaSparx browser renderer self-test passed.');
