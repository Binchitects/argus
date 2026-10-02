/** A file to put in a zip: its path inside (folders by "/") and its bytes. */
export interface ZipEntry {
  name: string
  data: Uint8Array
}

const crcTable = (() => {
  const t = new Uint32Array(256)
  for (let n = 0; n < 256; n++) {
    let c = n
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
    t[n] = c >>> 0
  }
  return t
})()

export function crc32(data: Uint8Array): number {
  let c = 0xffffffff
  for (const b of data) c = crcTable[(c ^ b) & 0xff]! ^ (c >>> 8)
  return (c ^ 0xffffffff) >>> 0
}

/** Deflated with the browser's own compression; null where it has none (the file is stored as it is). */
async function deflate(data: Uint8Array): Promise<Uint8Array | null> {
  if (typeof CompressionStream === 'undefined') return null
  try {
    const stream = new Blob([data as BlobPart]).stream().pipeThrough(new CompressionStream('deflate-raw'))
    return new Uint8Array(await new Response(stream).arrayBuffer())
  } catch {
    return null
  }
}

/** MS-DOS time and date, as zip keeps them. */
function dosTime(d: Date): [number, number] {
  return [(d.getHours() << 11) | (d.getMinutes() << 5) | (d.getSeconds() >> 1), ((d.getFullYear() - 1980) << 9) | ((d.getMonth() + 1) << 5) | d.getDate()]
}

/**
 * A zip of these files (names in UTF-8), each deflated when that makes it smaller.
 * Names are taken as given: make them unique first.
 */
export async function makeZip(entries: ZipEntry[], now = new Date()): Promise<Blob> {
  const encoder = new TextEncoder()
  const [time, date] = dosTime(now)
  const parts: Uint8Array[] = []
  const central: Uint8Array[] = []
  let offset = 0
  for (const e of entries) {
    const name = encoder.encode(e.name)
    const crc = crc32(e.data)
    const packed = await deflate(e.data)
    const deflated = packed !== null && packed.length < e.data.length
    const body = deflated ? packed : e.data
    const local = new DataView(new ArrayBuffer(30))
    local.setUint32(0, 0x04034b50, true)
    local.setUint16(4, 20, true)
    local.setUint16(6, 0x0800, true) // UTF-8 names
    local.setUint16(8, deflated ? 8 : 0, true)
    local.setUint16(10, time, true)
    local.setUint16(12, date, true)
    local.setUint32(14, crc, true)
    local.setUint32(18, body.length, true)
    local.setUint32(22, e.data.length, true)
    local.setUint16(26, name.length, true)
    parts.push(new Uint8Array(local.buffer), name, body)
    const entry = new DataView(new ArrayBuffer(46))
    entry.setUint32(0, 0x02014b50, true)
    entry.setUint16(4, 20, true)
    entry.setUint16(6, 20, true)
    entry.setUint16(8, 0x0800, true)
    entry.setUint16(10, deflated ? 8 : 0, true)
    entry.setUint16(12, time, true)
    entry.setUint16(14, date, true)
    entry.setUint32(16, crc, true)
    entry.setUint32(20, body.length, true)
    entry.setUint32(24, e.data.length, true)
    entry.setUint16(28, name.length, true)
    entry.setUint32(42, offset, true)
    central.push(new Uint8Array(entry.buffer), name)
    offset += 30 + name.length + body.length
  }
  const size = central.reduce((n, c) => n + c.length, 0)
  const end = new DataView(new ArrayBuffer(22))
  end.setUint32(0, 0x06054b50, true)
  end.setUint16(8, entries.length, true)
  end.setUint16(10, entries.length, true)
  end.setUint32(12, size, true)
  end.setUint32(16, offset, true)
  return new Blob([...parts, ...central, new Uint8Array(end.buffer)] as BlobPart[], { type: 'application/zip' })
}

/** Unique names: a second "a.png" becomes "a (2).png". */
export function uniqueNames(names: string[]): string[] {
  const used = new Set<string>()
  return names.map((n) => {
    let name = n
    const dot = n.lastIndexOf('.')
    const [stem, ext] = dot > n.lastIndexOf('/') + 1 ? [n.slice(0, dot), n.slice(dot)] : [n, '']
    for (let i = 2; used.has(name.toLowerCase()); i++) name = `${stem} (${i})${ext}`
    used.add(name.toLowerCase())
    return name
  })
}

/** Saves a blob as a file, as a download link would. */
export function saveBlob(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = fileName
  document.body.append(a)
  a.click()
  a.remove()
  setTimeout(() => URL.revokeObjectURL(url), 10_000)
}
