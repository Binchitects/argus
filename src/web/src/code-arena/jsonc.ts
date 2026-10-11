// VS Code's JSON with comments (settings.json, keybindings.json, launch.json): read with its comments and trailing
// commas, and changed in place, a property or an element at a time, so what the person wrote around it (comments, order,
// layout) stays as it was.

export type JsonPath = (string | number)[]

export interface JsonNode {
  type: 'object' | 'array' | 'string' | 'number' | 'boolean' | 'null'
  /** Where the value starts and how long it is, in the text. */
  offset: number
  length: number
  value?: unknown
  /** An object's properties, in order. */
  properties?: JsonProperty[]
  /** An array's elements, in order. */
  items?: JsonNode[]
}

export interface JsonProperty {
  key: string
  /** Where the property starts (its key) and ends (its value). */
  offset: number
  end: number
  value: JsonNode
}

export interface JsonError {
  offset: number
  length: number
  message: string
}

/** The text parsed into nodes with their places: comments and trailing commas allowed; the first mistake ends it. */
export function parseTree(text: string): { root: JsonNode | undefined; errors: JsonError[] } {
  let i = 0
  const errors: JsonError[] = []

  const skip = () => {
    for (;;) {
      while (i < text.length && /\s/.test(text[i]!)) i++
      if (text.startsWith('//', i)) {
        const nl = text.indexOf('\n', i)
        i = nl < 0 ? text.length : nl + 1
      } else if (text.startsWith('/*', i)) {
        const close = text.indexOf('*/', i + 2)
        if (close < 0) throw fail(i, 2, 'A comment that is never closed.')
        i = close + 2
      } else return
    }
  }
  const fail = (offset: number, length: number, message: string) => {
    errors.push({ offset, length, message })
    return new Error(message)
  }

  const string = (): JsonNode => {
    const start = i
    i++
    let out = ''
    for (;;) {
      if (i >= text.length || text[i] === '\n') throw fail(start, i - start, 'A string that is never closed.')
      const ch = text[i]!
      if (ch === '"') {
        i++
        return { type: 'string', offset: start, length: i - start, value: out }
      }
      if (ch === '\\') {
        const next = text[i + 1]
        const simple: Record<string, string> = { '"': '"', '\\': '\\', '/': '/', b: '\b', f: '\f', n: '\n', r: '\r', t: '\t' }
        if (next !== undefined && next in simple) {
          out += simple[next]
          i += 2
        } else if (next === 'u' && /^[0-9a-fA-F]{4}$/.test(text.slice(i + 2, i + 6))) {
          out += String.fromCharCode(parseInt(text.slice(i + 2, i + 6), 16))
          i += 6
        } else throw fail(i, 2, 'A wrong escape in a string.')
      } else {
        out += ch
        i++
      }
    }
  }

  const value = (): JsonNode => {
    skip()
    const start = i
    const ch = text[i]
    if (ch === '{') {
      i++
      const properties: JsonProperty[] = []
      skip()
      while (text[i] !== '}') {
        if (text[i] !== '"') throw fail(i, 1, i >= text.length ? 'The object is never closed.' : 'A property name in double quotes was expected.')
        const keyStart = i
        const key = string().value as string
        skip()
        if (text[i] !== ':') throw fail(i, 1, 'A colon was expected after the property name.')
        i++
        const v = value()
        properties.push({ key, offset: keyStart, end: v.offset + v.length, value: v })
        skip()
        if (text[i] === ',') {
          i++
          skip()
        } else if (text[i] !== '}') throw fail(i, 1, i >= text.length ? 'The object is never closed.' : 'A comma or a closing brace was expected.')
      }
      i++
      return { type: 'object', offset: start, length: i - start, properties }
    }
    if (ch === '[') {
      i++
      const items: JsonNode[] = []
      skip()
      while (text[i] !== ']') {
        if (i >= text.length) throw fail(start, 1, 'The array is never closed.')
        items.push(value())
        skip()
        if (text[i] === ',') {
          i++
          skip()
        } else if (text[i] !== ']') throw fail(i, 1, i >= text.length ? 'The array is never closed.' : 'A comma or a closing bracket was expected.')
      }
      i++
      return { type: 'array', offset: start, length: i - start, items }
    }
    if (ch === '"') return string()
    const word = /^(?:true|false|null|-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?)/.exec(text.slice(i, i + 64))?.[0]
    if (!word) throw fail(i, 1, i >= text.length ? 'A value was expected.' : `Not JSON here: ${JSON.stringify(text.slice(i, i + 12))}.`)
    i += word.length
    if (word === 'true' || word === 'false') return { type: 'boolean', offset: start, length: word.length, value: word === 'true' }
    if (word === 'null') return { type: 'null', offset: start, length: 4, value: null }
    return { type: 'number', offset: start, length: word.length, value: Number(word) }
  }

  try {
    skip()
    if (i >= text.length) return { root: undefined, errors }
    const root = value()
    skip()
    if (i < text.length) throw fail(i, text.length - i, 'Only one value may be in the file.')
    return { root, errors }
  } catch {
    return { root: undefined, errors }
  }
}

/** A node's plain value. */
export function valueOf(node: JsonNode | undefined): unknown {
  if (!node) return undefined
  if (node.type === 'object') return Object.fromEntries(node.properties!.map((p) => [p.key, valueOf(p.value)]))
  if (node.type === 'array') return node.items!.map(valueOf)
  return node.value
}

/** The text's value, or undefined (with why) when it is not JSON; empty text is undefined with no error. */
export function parse(text: string): { value: unknown; errors: JsonError[] } {
  const { root, errors } = parseTree(text)
  return { value: valueOf(root), errors }
}

/** The node at a path, if it is there. */
export function find(root: JsonNode | undefined, path: JsonPath): JsonNode | undefined {
  let node = root
  for (const seg of path) {
    if (!node) return undefined
    if (typeof seg === 'number') node = node.type === 'array' ? node.items![seg] : undefined
    else node = node.type === 'object' ? node.properties!.findLast((p) => p.key === seg)?.value : undefined
  }
  return node
}

/** The line and column (from 1) of an offset. */
export function positionAt(text: string, offset: number): { line: number; column: number } {
  let line = 1
  let start = 0
  for (let k = 0; k < offset && k < text.length; k++) {
    if (text[k] === '\n') {
      line++
      start = k + 1
    }
  }
  return { line, column: offset - start + 1 }
}

/** The file's own indentation (its first indented line's), else four spaces, as VS Code writes settings. */
function unitOf(text: string): string {
  const m = /\n([ \t]+)\S/.exec(text)
  if (!m) return '    '
  return m[1]!.startsWith('\t') ? '\t' : m[1]!
}

const lineIndent = (text: string, offset: number) => {
  const start = text.lastIndexOf('\n', offset - 1) + 1
  return /^[ \t]*/.exec(text.slice(start))![0]
}

/** A value as JSON, its lines after the first indented to sit at this indentation. */
const write = (v: unknown, unit: string, indent: string) => JSON.stringify(v, null, unit).split('\n').join('\n' + indent)

/** Where a comma or a closing bracket after a value is (comments and spaces skipped), or -1. */
function after(text: string, from: number): number {
  let k = from
  for (;;) {
    while (k < text.length && /\s/.test(text[k]!)) k++
    if (text.startsWith('//', k)) {
      const nl = text.indexOf('\n', k)
      k = nl < 0 ? text.length : nl + 1
    } else if (text.startsWith('/*', k)) {
      const close = text.indexOf('*/', k + 2)
      k = close < 0 ? text.length : close + 2
    } else return k
  }
}

const splice = (text: string, start: number, end: number, insert: string) => text.slice(0, start) + insert + text.slice(end)

/**
 * The text with the value at a path set (undefined: the property taken out), everything else as it was. Objects on the
 * path that are missing are made; a text that is not JSON yet becomes an object.
 */
export function setProperty(text: string, path: JsonPath, value: unknown): string {
  const { root, errors } = parseTree(text)
  const unit = unitOf(text)
  if (!root) {
    if (errors.length > 0) throw new Error(`The file is not JSON: ${errors[0]!.message}`)
    if (value === undefined) return text
    const nested = path.reduceRight<unknown>((v, seg) => (typeof seg === 'number' ? [v] : { [seg]: v }), value)
    return JSON.stringify(nested, null, unit) + '\n'
  }
  let node: JsonNode = root
  for (let d = 0; d < path.length; d++) {
    const seg = path[d]!
    const last = d === path.length - 1
    if (typeof seg === 'number') {
      if (node.type !== 'array') throw new Error(`Not an array at ${path.slice(0, d).join('.')}`)
      const item = node.items![seg]
      if (!item) throw new Error(`No element ${seg}`)
      if (last) {
        if (value === undefined) return removeElement(text, path.slice(0, d), seg)
        return splice(text, item.offset, item.offset + item.length, write(value, unit, lineIndent(text, item.offset)))
      }
      node = item
      continue
    }
    if (node.type !== 'object') throw new Error(`Not an object at ${path.slice(0, d).join('.') || 'the top'}`)
    const props = node.properties!
    const at = props.findLastIndex((p) => p.key === seg)
    if (at >= 0) {
      const prop = props[at]!
      if (!last) {
        node = prop.value
        continue
      }
      if (value !== undefined) return splice(text, prop.value.offset, prop.value.offset + prop.value.length, write(value, unit, lineIndent(text, prop.offset)))
      // Out with its comma: the one after it, else (the last) the one before it.
      const comma = after(text, prop.end)
      if (text[comma] === ',') return splice(text, lineStart(text, prop.offset), lineEndAfter(text, comma + 1), '')
      const prev = props[at - 1]
      if (prev) {
        const prevComma = after(text, prev.end)
        return splice(text, prevComma, lineEndAfter(text, prop.end), '\n')
      }
      return splice(text, lineStart(text, prop.offset), lineEndAfter(text, prop.end), '')
    }
    if (value === undefined) return text
    // Missing: the rest of the path made here, as the object's last property.
    const rest = path.slice(d + 1).reduceRight<unknown>((v, s) => (typeof s === 'number' ? [v] : { [s]: v }), value)
    const parentIndent = lineIndent(text, node.offset)
    const close = node.offset + node.length - 1
    if (props.length === 0) {
      const indent = parentIndent + unit
      return splice(text, node.offset + 1, close, `\n${indent}${JSON.stringify(seg)}: ${write(rest, unit, indent)}\n${parentIndent}`)
    }
    const lastProp = props[props.length - 1]!
    const indent = lineIndent(text, lastProp.offset)
    const comma = after(text, lastProp.end)
    const entry = `${JSON.stringify(seg)}: ${write(rest, unit, indent)}`
    if (text[comma] === ',') return splice(text, comma + 1, comma + 1, `\n${indent}${entry},`)
    return splice(text, lastProp.end, lastProp.end, `,\n${indent}${entry}`)
  }
  return text
}

const lineStart = (text: string, offset: number) => {
  const start = text.lastIndexOf('\n', offset - 1) + 1
  return /^[ \t]*$/.test(text.slice(start, offset)) ? start : offset
}
const lineEndAfter = (text: string, offset: number) => {
  const m = /^[ \t]*(?:\/\/[^\n]*)?\n?/.exec(text.slice(offset))
  return offset + (m?.[0].length ?? 0)
}

/** The text with a value added at the end of the array at a path (the whole text an array when it is not JSON yet). */
export function appendElement(text: string, path: JsonPath, value: unknown): string {
  const { root, errors } = parseTree(text)
  const unit = unitOf(text)
  if (!root && path.length === 0) {
    if (errors.length > 0) throw new Error(`The file is not JSON: ${errors[0]!.message}`)
    return `[\n${unit}${write(value, unit, unit)}\n]\n`
  }
  const node = find(root, path)
  if (!node) return setProperty(text, path, [value])
  if (node.type !== 'array') throw new Error('Not an array there.')
  const parentIndent = lineIndent(text, node.offset)
  const items = node.items!
  if (items.length === 0) {
    const indent = parentIndent + unit
    return splice(text, node.offset + 1, node.offset + node.length - 1, `\n${indent}${write(value, unit, indent)}\n${parentIndent}`)
  }
  const last = items[items.length - 1]!
  const indent = lineIndent(text, last.offset)
  const comma = after(text, last.offset + last.length)
  if (text[comma] === ',') return splice(text, comma + 1, comma + 1, `\n${indent}${write(value, unit, indent)},`)
  return splice(text, last.offset + last.length, last.offset + last.length, `,\n${indent}${write(value, unit, indent)}`)
}

/** The text without the element at an index of the array at a path. */
export function removeElement(text: string, path: JsonPath, index: number): string {
  const { root } = parseTree(text)
  const node = find(root, path)
  if (node?.type !== 'array') return text
  const items = node.items!
  const item = items[index]
  if (!item) return text
  const end = item.offset + item.length
  const comma = after(text, end)
  if (text[comma] === ',') return splice(text, lineStart(text, item.offset), lineEndAfter(text, comma + 1), '')
  const prev = items[index - 1]
  if (prev) return splice(text, after(text, prev.offset + prev.length), lineEndAfter(text, end), '\n')
  return splice(text, lineStart(text, item.offset), lineEndAfter(text, end), '')
}
