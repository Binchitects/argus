/**
 * A .glb read for the chat's 3D viewer: its meshes as flat arrays in world space (positions, normals, UVs, indices),
 * each with its material (colour, texture, metal, roughness, glow, see-through), its pictures, and its lights. Enough of
 * glTF 2.0 for what the 3D tool exports, and for most models from elsewhere: triangles, a node tree, KHR_texture_transform,
 * KHR_materials_emissive_strength, KHR_materials_transmission and KHR_lights_punctual.
 */

export interface GltfMaterial {
  color: [number, number, number, number]
  /** Index into images; the UV set it reads, and its transform (scale, offset). */
  texture: { image: number; uv: number; scale: [number, number]; offset: [number, number] } | null
  metallic: number
  roughness: number
  emissive: [number, number, number]
  /** 0 opaque … 1 clear glass. */
  transmission: number
  blend: boolean
  doubleSided: boolean
}

export interface GltfMesh {
  positions: Float32Array
  normals: Float32Array
  uvs: Float32Array[]
  indices: Uint32Array
  material: GltfMaterial
}

export interface GltfLight {
  kind: 'point' | 'spot' | 'directional'
  color: [number, number, number]
  intensity: number
  position: [number, number, number]
  direction: [number, number, number]
}

export interface GltfModel {
  meshes: GltfMesh[]
  images: Blob[]
  lights: GltfLight[]
  min: [number, number, number]
  max: [number, number, number]
}

type Json = Record<string, unknown> & {
  accessors?: Accessor[]
  bufferViews?: BufferView[]
  meshes?: { primitives: Primitive[] }[]
  nodes?: GNode[]
  scenes?: { nodes?: number[] }[]
  scene?: number
  materials?: Record<string, unknown>[]
  textures?: { source?: number }[]
  images?: { bufferView?: number; mimeType?: string; uri?: string }[]
  extensions?: { KHR_lights_punctual?: { lights: { type: string; color?: number[]; intensity?: number }[] } }
}
interface Accessor {
  bufferView?: number
  byteOffset?: number
  componentType: number
  count: number
  type: string
  normalized?: boolean
}
interface BufferView {
  byteOffset?: number
  byteLength: number
  byteStride?: number
}
interface Primitive {
  attributes: Record<string, number>
  indices?: number
  material?: number
  mode?: number
}
interface GNode {
  mesh?: number
  children?: number[]
  matrix?: number[]
  translation?: number[]
  rotation?: number[]
  scale?: number[]
  extensions?: { KHR_lights_punctual?: { light: number } }
}

const sizes: Record<string, number> = { SCALAR: 1, VEC2: 2, VEC3: 3, VEC4: 4, MAT4: 16 }

export class GltfError extends Error {}

/** Splits a .glb into its JSON and its binary chunk. */
export function readGlb(buffer: ArrayBuffer): { json: Json; bin: DataView | null } {
  const view = new DataView(buffer)
  if (buffer.byteLength < 20 || view.getUint32(0, true) !== 0x46546c67) throw new GltfError('Not a .glb file.')
  if (view.getUint32(4, true) !== 2) throw new GltfError('Only glTF 2.0 is shown.')
  let at = 12
  let json: Json | null = null
  let bin: DataView | null = null
  while (at + 8 <= buffer.byteLength) {
    const length = view.getUint32(at, true)
    const type = view.getUint32(at + 4, true)
    if (type === 0x4e4f534a) json = JSON.parse(new TextDecoder().decode(new Uint8Array(buffer, at + 8, length))) as Json
    else if (type === 0x004e4942) bin = new DataView(buffer, at + 8, length)
    at += 8 + length
  }
  if (!json) throw new GltfError('The file has no glTF description.')
  return { json, bin }
}

function read(json: Json, bin: DataView | null, index: number): Float32Array | Uint32Array {
  const a = json.accessors?.[index]
  if (!a) throw new GltfError(`No accessor ${index}.`)
  const n = sizes[a.type] ?? 1
  const out = a.componentType === 5126 ? new Float32Array(a.count * n) : new Uint32Array(a.count * n)
  if (a.bufferView === undefined || !bin) return out
  const bv = json.bufferViews![a.bufferView]
  const bytes = { 5120: 1, 5121: 1, 5122: 2, 5123: 2, 5125: 4, 5126: 4 }[a.componentType] ?? 4
  const stride = bv.byteStride || bytes * n
  const base = (bv.byteOffset ?? 0) + (a.byteOffset ?? 0)
  for (let i = 0; i < a.count; i++) {
    for (let k = 0; k < n; k++) {
      const p = base + i * stride + k * bytes
      let v: number
      switch (a.componentType) {
        case 5126: v = bin.getFloat32(p, true); break
        case 5125: v = bin.getUint32(p, true); break
        case 5123: v = bin.getUint16(p, true); break
        case 5122: v = bin.getInt16(p, true); break
        case 5121: v = bin.getUint8(p); break
        default: v = bin.getInt8(p)
      }
      if (a.normalized && a.componentType !== 5126) v /= { 5121: 255, 5123: 65535, 5120: 127, 5122: 32767 }[a.componentType] ?? 1
      out[i * n + k] = v
    }
  }
  return out
}

type Mat4 = Float64Array

const identity = (): Mat4 => new Float64Array([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1])

export function multiply(a: Mat4, b: Mat4): Mat4 {
  const o = new Float64Array(16)
  for (let c = 0; c < 4; c++) for (let r = 0; r < 4; r++) o[c * 4 + r] = a[r] * b[c * 4] + a[4 + r] * b[c * 4 + 1] + a[8 + r] * b[c * 4 + 2] + a[12 + r] * b[c * 4 + 3]
  return o
}

function local(node: GNode): Mat4 {
  if (node.matrix) return new Float64Array(node.matrix)
  const [x, y, z, w] = node.rotation ?? [0, 0, 0, 1]
  const [sx, sy, sz] = node.scale ?? [1, 1, 1]
  const [tx, ty, tz] = node.translation ?? [0, 0, 0]
  return new Float64Array([
    (1 - 2 * (y * y + z * z)) * sx, 2 * (x * y + z * w) * sx, 2 * (x * z - y * w) * sx, 0,
    2 * (x * y - z * w) * sy, (1 - 2 * (x * x + z * z)) * sy, 2 * (y * z + x * w) * sy, 0,
    2 * (x * z + y * w) * sz, 2 * (y * z - x * w) * sz, (1 - 2 * (x * x + y * y)) * sz, 0,
    tx, ty, tz, 1,
  ])
}

function material(json: Json, index: number | undefined): GltfMaterial {
  const m = (index === undefined ? undefined : json.materials?.[index]) as
    | {
        pbrMetallicRoughness?: { baseColorFactor?: number[]; baseColorTexture?: { index: number; texCoord?: number; extensions?: { KHR_texture_transform?: { scale?: number[]; offset?: number[]; texCoord?: number } } }; metallicFactor?: number; roughnessFactor?: number }
        emissiveFactor?: number[]
        alphaMode?: string
        doubleSided?: boolean
        extensions?: { KHR_materials_emissive_strength?: { emissiveStrength?: number }; KHR_materials_transmission?: { transmissionFactor?: number } }
      }
    | undefined
  const pbr = m?.pbrMetallicRoughness
  const t = pbr?.baseColorTexture
  const image = t ? json.textures?.[t.index]?.source : undefined
  const transform = t?.extensions?.KHR_texture_transform
  const strength = m?.extensions?.KHR_materials_emissive_strength?.emissiveStrength ?? 1
  const e = m?.emissiveFactor ?? [0, 0, 0]
  const transmission = m?.extensions?.KHR_materials_transmission?.transmissionFactor ?? 0
  const color = (pbr?.baseColorFactor ?? [1, 1, 1, 1]) as [number, number, number, number]
  return {
    color,
    texture: image === undefined ? null : { image, uv: transform?.texCoord ?? t?.texCoord ?? 0, scale: (transform?.scale ?? [1, 1]) as [number, number], offset: (transform?.offset ?? [0, 0]) as [number, number] },
    metallic: pbr?.metallicFactor ?? 1,
    roughness: pbr?.roughnessFactor ?? 1,
    emissive: [e[0] * strength, e[1] * strength, e[2] * strength],
    transmission,
    blend: m?.alphaMode === 'BLEND' || transmission > 0 || color[3] < 1,
    doubleSided: m?.doubleSided ?? false,
  }
}

/** Flat normals for a mesh that has none. */
function flatNormals(p: Float32Array, idx: Uint32Array): Float32Array {
  const n = new Float32Array(p.length)
  for (let i = 0; i + 2 < idx.length; i += 3) {
    const [a, b, c] = [idx[i] * 3, idx[i + 1] * 3, idx[i + 2] * 3]
    const u = [p[b] - p[a], p[b + 1] - p[a + 1], p[b + 2] - p[a + 2]]
    const v = [p[c] - p[a], p[c + 1] - p[a + 1], p[c + 2] - p[a + 2]]
    const x = u[1] * v[2] - u[2] * v[1]
    const y = u[2] * v[0] - u[0] * v[2]
    const z = u[0] * v[1] - u[1] * v[0]
    for (const k of [a, b, c]) {
      n[k] += x
      n[k + 1] += y
      n[k + 2] += z
    }
  }
  for (let i = 0; i < n.length; i += 3) {
    const l = Math.hypot(n[i], n[i + 1], n[i + 2]) || 1
    n[i] /= l
    n[i + 1] /= l
    n[i + 2] /= l
  }
  return n
}

/** The model: every mesh of the scene in world space, its pictures, its lights and its bounds. */
export function parseGlb(buffer: ArrayBuffer): GltfModel {
  const { json, bin } = readGlb(buffer)
  const meshes: GltfMesh[] = []
  const lights: GltfLight[] = []
  const min: [number, number, number] = [Infinity, Infinity, Infinity]
  const max: [number, number, number] = [-Infinity, -Infinity, -Infinity]
  const lightDefs = json.extensions?.KHR_lights_punctual?.lights ?? []
  const visit = (index: number, parent: Mat4, depth: number) => {
    const node = json.nodes?.[index]
    if (!node || depth > 64) return
    const world = multiply(parent, local(node))
    const li = node.extensions?.KHR_lights_punctual?.light
    if (li !== undefined && lightDefs[li]) {
      const d = lightDefs[li]
      const dir = [-world[8], -world[9], -world[10]]
      const l = Math.hypot(dir[0], dir[1], dir[2]) || 1
      lights.push({
        kind: d.type === 'spot' ? 'spot' : d.type === 'directional' ? 'directional' : 'point',
        color: (d.color ?? [1, 1, 1]) as [number, number, number],
        intensity: d.intensity ?? 1,
        position: [world[12], world[13], world[14]],
        direction: [dir[0] / l + 0, dir[1] / l + 0, dir[2] / l + 0],
      })
    }
    if (node.mesh !== undefined) {
      for (const prim of json.meshes?.[node.mesh]?.primitives ?? []) {
        if ((prim.mode ?? 4) !== 4 || prim.attributes.POSITION === undefined) continue
        const raw = read(json, bin, prim.attributes.POSITION) as Float32Array
        const count = raw.length / 3
        const indices = prim.indices !== undefined ? Uint32Array.from(read(json, bin, prim.indices)) : Uint32Array.from({ length: count }, (_, i) => i)
        const positions = new Float32Array(raw.length)
        for (let i = 0; i < count; i++) {
          const [x, y, z] = [raw[i * 3], raw[i * 3 + 1], raw[i * 3 + 2]]
          for (let k = 0; k < 3; k++) {
            const v = world[k] * x + world[4 + k] * y + world[8 + k] * z + world[12 + k]
            positions[i * 3 + k] = v
            if (v < min[k]) min[k] = v
            if (v > max[k]) max[k] = v
          }
        }
        let normals: Float32Array
        if (prim.attributes.NORMAL !== undefined) {
          const n = read(json, bin, prim.attributes.NORMAL) as Float32Array
          normals = new Float32Array(n.length)
          for (let i = 0; i < count; i++) {
            const [x, y, z] = [n[i * 3], n[i * 3 + 1], n[i * 3 + 2]]
            const v = [0, 1, 2].map((k) => world[k] * x + world[4 + k] * y + world[8 + k] * z)
            const l = Math.hypot(v[0], v[1], v[2]) || 1
            normals.set([v[0] / l, v[1] / l, v[2] / l], i * 3)
          }
        } else {
          normals = flatNormals(positions, indices)
        }
        const uvs: Float32Array[] = []
        for (let k = 0; prim.attributes[`TEXCOORD_${k}`] !== undefined; k++) uvs.push(read(json, bin, prim.attributes[`TEXCOORD_${k}`]) as Float32Array)
        meshes.push({ positions, normals, uvs, indices, material: material(json, prim.material) })
      }
    }
    for (const c of node.children ?? []) visit(c, world, depth + 1)
  }
  const roots = json.scenes?.[json.scene ?? 0]?.nodes ?? json.nodes?.map((_, i) => i) ?? []
  for (const r of roots) visit(r, identity(), 0)
  if (meshes.length === 0) throw new GltfError('The model has no meshes to show.')
  const images = (json.images ?? []).map((im) => {
    if (im.bufferView !== undefined && bin) {
      const bv = json.bufferViews![im.bufferView]
      const start = bin.byteOffset + (bv.byteOffset ?? 0)
      return new Blob([(bin.buffer as ArrayBuffer).slice(start, start + bv.byteLength)], { type: im.mimeType ?? 'image/png' })
    }
    return new Blob([], { type: 'image/png' })
  })
  return { meshes, images, lights, min, max }
}
