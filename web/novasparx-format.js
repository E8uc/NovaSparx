const MAGIC = [0x4e, 0x53, 0x4d, 0x45, 0x53, 0x48, 0x31, 0x00];

export function recommendedRenderQuality(environment = globalThis) {
  const nav = environment?.navigator ?? {};
  const memory = Number(nav.deviceMemory || 0);
  const cores = Number(nav.hardwareConcurrency || 0);
  const coarse = Boolean(environment?.matchMedia?.('(pointer: coarse)')?.matches);
  if ((memory > 0 && memory <= 4) || (cores > 0 && cores <= 4)) return 'mobile';
  if (coarse || (memory > 0 && memory <= 8) || (cores > 0 && cores <= 8)) return 'balanced';
  return 'high';
}

export function recommendedPixelRatio(environment = globalThis) {
  const quality = recommendedRenderQuality(environment);
  const dpr = Number(environment?.devicePixelRatio || 1);
  const cap = quality === 'mobile' ? 1.25 : quality === 'balanced' ? 1.6 : 2;
  return Math.max(1, Math.min(dpr, cap));
}

export function parseNovaMesh(input) {
  const source = input instanceof ArrayBuffer
    ? { buffer: input, byteOffset: 0, byteLength: input.byteLength }
    : ArrayBuffer.isView(input)
      ? { buffer: input.buffer, byteOffset: input.byteOffset, byteLength: input.byteLength }
      : null;
  if (!source || source.byteLength < 16) throw new Error('Invalid NovaSparx mesh package.');

  const bytes = new Uint8Array(source.buffer, source.byteOffset, source.byteLength);
  for (let i = 0; i < MAGIC.length; i += 1)
    if (bytes[i] !== MAGIC[i]) throw new Error('NovaSparx mesh package has an invalid signature.');

  const view = new DataView(source.buffer, source.byteOffset, source.byteLength);
  const headerLength = view.getUint32(8, true);
  const paddedHeaderLength = view.getUint32(12, true);
  if (!headerLength || paddedHeaderLength < headerLength || paddedHeaderLength % 4)
    throw new Error('NovaSparx mesh package has an invalid header.');

  const payloadStart = 16 + paddedHeaderLength;
  if (payloadStart > source.byteLength) throw new Error('NovaSparx mesh header exceeds the package.');

  const header = JSON.parse(new TextDecoder().decode(new Uint8Array(
    source.buffer, source.byteOffset + 16, headerLength,
  )));
  if (header?.schema !== 'novasparx.client-mesh.v1')
    throw new Error(`Unsupported NovaSparx mesh schema: ${header?.schema || 'missing'}`);

  const base = source.byteOffset + payloadStart;
  const payloadLength = source.byteLength - payloadStart;
  const array = (name, Type) => {
    const item = header.arrays?.[name];
    if (!item) return null;
    const offset = Number(item.byteOffset);
    const length = Number(item.byteLength);
    if (!Number.isInteger(offset) || !Number.isInteger(length) || offset < 0 || length < 0 ||
        offset + length > payloadLength || length % Type.BYTES_PER_ELEMENT)
      throw new Error(`Invalid NovaSparx array: ${name}`);
    const absolute = base + offset;
    if (absolute % Type.BYTES_PER_ELEMENT) throw new Error(`Invalid NovaSparx alignment: ${name}`);
    return new Type(source.buffer, absolute, length / Type.BYTES_PER_ELEMENT);
  };

  const geometry = {
    positions: array('positions', Float32Array),
    normals: array('normals', Float32Array),
    tangents: array('tangents', Float32Array),
    uv0: array('uv0', Float32Array),
    colors: array('colors', Float32Array),
    indices: array('indices', Uint32Array),
  };
  if (!geometry.positions || !geometry.normals || !geometry.tangents || !geometry.uv0 || !geometry.indices)
    throw new Error('NovaSparx mesh package is missing geometry streams.');

  const vertices = geometry.positions.length / 3;
  if (!Number.isInteger(vertices) || vertices <= 0 ||
      geometry.normals.length !== vertices * 3 || geometry.tangents.length !== vertices * 4 ||
      geometry.uv0.length !== vertices * 2 || (geometry.colors && geometry.colors.length !== vertices * 4))
    throw new Error('NovaSparx mesh package has inconsistent geometry streams.');

  return { header, geometry, buffer: source.buffer };
}
