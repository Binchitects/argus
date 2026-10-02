import { inflateRawSync } from 'node:zlib'
import { describe, expect, it } from 'vitest'
import { crc32, makeZip, uniqueNames } from './zip'

/** Reads a zip back through its central directory: name, bytes, and whether the CRC matches. */
async function unzip(blob: Blob) {
  const buf = new Uint8Array(await blob.arrayBuffer())
  const v = new DataView(buf.buffer)
  const end = buf.length - 22
  expect(v.getUint32(end, true)).toBe(0x06054b50)
  const count = v.getUint16(end + 10, true)
  let at = v.getUint32(end + 16, true)
  const out: { name: string; data: Uint8Array; crc: boolean }[] = []
  for (let i = 0; i < count; i++) {
    expect(v.getUint32(at, true)).toBe(0x02014b50)
    const method = v.getUint16(at + 10, true)
    const crc = v.getUint32(at + 16, true)
    const packed = v.getUint32(at + 20, true)
    const nameLength = v.getUint16(at + 28, true)
    const local = v.getUint32(at + 42, true)
    const name = new TextDecoder().decode(buf.subarray(at + 46, at + 46 + nameLength))
    const start = local + 30 + v.getUint16(local + 26, true)
    const body = buf.subarray(start, start + packed)
    const data = method === 8 ? new Uint8Array(inflateRawSync(body)) : body
    out.push({ name, data, crc: crc32(data) === crc })
    at += 46 + nameLength
  }
  return out
}

describe('zip', () => {
  it('keeps every file whole, by its UTF-8 name, small ones stored and big ones deflated', async () => {
    const text = new TextEncoder().encode('row,value\n'.repeat(500))
    const png = new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10])
    const files = await unzip(await makeZip([{ name: 'data/report.csv', data: text }, { name: 'گزارش.png', data: png }]))
    expect(files.map((f) => [f.name, f.data.length, f.crc])).toEqual([['data/report.csv', text.length, true], ['گزارش.png', 8, true]])
    expect(new TextDecoder().decode(files[0]!.data)).toBe('row,value\n'.repeat(500))
  })

  it('knows the CRC-32 of a known string', () => {
    expect(crc32(new TextEncoder().encode('The quick brown fox jumps over the lazy dog'))).toBe(0x414fa339)
  })

  it('makes names unique, keeping the extension', () => {
    expect(uniqueNames(['a.png', 'A.png', 'a.png', 'src/.env', 'src/.env', 'notes'])).toEqual(['a.png', 'A (2).png', 'a (3).png', 'src/.env', 'src/.env (2)', 'notes'])
  })
})
