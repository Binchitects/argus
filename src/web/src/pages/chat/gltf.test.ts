import { describe, expect, it } from 'vitest'
import { GltfError, parseGlb, readGlb } from './gltf'

/** A .glb of one triangle, turned by its node, with a red material, a point light and an image. */
function triangle(): ArrayBuffer {
  const positions = new Float32Array([0, 0, 0, 1, 0, 0, 0, 1, 0])
  const indices = new Uint16Array([0, 1, 2, 0])
  const png = new Uint8Array([137, 80, 78, 71])
  const bin = new Uint8Array(36 + 8 + 4)
  bin.set(new Uint8Array(positions.buffer), 0)
  bin.set(new Uint8Array(indices.buffer), 36)
  bin.set(png, 44)
  const json = {
    asset: { version: '2.0' },
    scene: 0,
    scenes: [{ nodes: [0, 1] }],
    nodes: [
      { mesh: 0, translation: [10, 0, 0] },
      { translation: [0, 3, 0], extensions: { KHR_lights_punctual: { light: 0 } } },
    ],
    meshes: [{ primitives: [{ attributes: { POSITION: 0 }, indices: 1, material: 0 }] }],
    materials: [{ pbrMetallicRoughness: { baseColorFactor: [1, 0, 0, 1], baseColorTexture: { index: 0 } }, extensions: { KHR_materials_emissive_strength: { emissiveStrength: 2 } }, emissiveFactor: [0.5, 0, 0] }],
    textures: [{ source: 0 }],
    images: [{ bufferView: 2, mimeType: 'image/png' }],
    accessors: [
      { bufferView: 0, componentType: 5126, count: 3, type: 'VEC3' },
      { bufferView: 1, componentType: 5123, count: 3, type: 'SCALAR' },
    ],
    bufferViews: [{ byteOffset: 0, byteLength: 36 }, { byteOffset: 36, byteLength: 6 }, { byteOffset: 44, byteLength: 4 }],
    buffers: [{ byteLength: bin.length }],
    extensions: { KHR_lights_punctual: { lights: [{ type: 'point', color: [1, 0.8, 0.6], intensity: 500 }] } },
  }
  let text = JSON.stringify(json)
  while (text.length % 4) text += ' '
  const jsonBytes = new TextEncoder().encode(text)
  const total = 12 + 8 + jsonBytes.length + 8 + bin.length
  const out = new ArrayBuffer(total)
  const view = new DataView(out)
  view.setUint32(0, 0x46546c67, true)
  view.setUint32(4, 2, true)
  view.setUint32(8, total, true)
  view.setUint32(12, jsonBytes.length, true)
  view.setUint32(16, 0x4e4f534a, true)
  new Uint8Array(out, 20, jsonBytes.length).set(jsonBytes)
  view.setUint32(20 + jsonBytes.length, bin.length, true)
  view.setUint32(24 + jsonBytes.length, 0x004e4942, true)
  new Uint8Array(out, 28 + jsonBytes.length, bin.length).set(bin)
  return out
}

describe('the 3D viewer reads a .glb', () => {
  it('puts its meshes in world space with their material, pictures and lights', () => {
    const model = parseGlb(triangle())
    expect(model.meshes).toHaveLength(1)
    const [mesh] = model.meshes
    expect([...mesh.positions]).toEqual([10, 0, 0, 11, 0, 0, 10, 1, 0])
    expect([...mesh.indices]).toEqual([0, 1, 2])
    // No normals in the file: worked out from the faces.
    expect([...mesh.normals.slice(0, 3)]).toEqual([0, 0, 1])
    expect(mesh.material.color).toEqual([1, 0, 0, 1])
    expect(mesh.material.texture).toEqual({ image: 0, uv: 0, scale: [1, 1], offset: [0, 0] })
    expect(mesh.material.emissive).toEqual([1, 0, 0])
    expect(model.images).toHaveLength(1)
    expect(model.lights).toEqual([{ kind: 'point', color: [1, 0.8, 0.6], intensity: 500, position: [0, 3, 0], direction: [0, 0, -1] }])
    expect(model.min).toEqual([10, 0, 0])
    expect(model.max).toEqual([11, 1, 0])
  })

  it('refuses what is not a .glb', () => {
    expect(() => readGlb(new ArrayBuffer(40))).toThrow(GltfError)
  })
})
