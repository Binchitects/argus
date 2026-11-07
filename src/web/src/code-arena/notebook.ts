// A Jupyter notebook (.ipynb, nbformat 4) as the notebook editor holds it, and back as the file: its cells (code,
// Markdown, raw), each with its source, its outputs and its execution count, the notebook's metadata, and whatever else
// it has, kept as it was. Saved as Jupyter and VS Code write one: the keys sorted, multi-line text as a list of lines,
// the file's own indentation (one space when it has none), a final line break.

/** An output as nbformat has it: stream (stdout, stderr), display_data, execute_result, error. */
export type Output = { output_type: string } & Record<string, unknown>

export type CellType = 'code' | 'markdown' | 'raw'

export interface Cell {
  /** nbformat 4.5's cell id; one made here for an older notebook is not written. */
  id: string
  type: CellType
  source: string
  outputs: Output[]
  count: number | null
  metadata: Record<string, unknown>
  /** What else the cell has (attachments…), written back as it was. */
  rest: Record<string, unknown>
}

export interface Notebook {
  cells: Cell[]
  metadata: Record<string, unknown>
  nbformat: number
  minor: number
  /** The file's indentation, kept when it is saved. */
  indent: number
  rest: Record<string, unknown>
}

/** A notebook's text that is not one: why. */
export interface NotNotebook {
  error: string
}

const isObject = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v)

/** A multi-line string as nbformat writes it (a list of lines, or one string), as one string. */
export const textOf = (v: unknown): string => (Array.isArray(v) ? v.filter((x) => typeof x === 'string').join('') : typeof v === 'string' ? v : '')

/** A string as nbformat writes multi-line text: its lines, each with its line break but the last. */
export function linesOf(text: string): string[] {
  return text === '' ? [] : text.split(/(?<=\n)/)
}

let made = 0
/** A new cell id: 8 letters and digits, as Jupyter makes them. */
export function newId(): string {
  const random = typeof crypto !== 'undefined' && crypto.getRandomValues ? Array.from(crypto.getRandomValues(new Uint8Array(4)), (b) => b.toString(16).padStart(2, '0')).join('') : (++made).toString(16).padStart(8, '0')
  return random
}

export function newCell(type: CellType, source = ''): Cell {
  return { id: newId(), type, source, outputs: [], count: null, metadata: {}, rest: {} }
}

/** An empty notebook, as a new .ipynb opens: one empty code cell, Python. */
export function emptyNotebook(): Notebook {
  return {
    cells: [newCell('code')],
    metadata: { kernelspec: { display_name: 'Python 3', language: 'python', name: 'python3' }, language_info: { name: 'python' } },
    nbformat: 4,
    minor: 5,
    indent: 1,
    rest: {},
  }
}

/** An output's multi-line fields (text, and a display's text formats) as one string each, the rest as it is. */
function readOutput(o: Record<string, unknown>): Output {
  const out: Record<string, unknown> = { ...o }
  if ('text' in out) out.text = textOf(out.text)
  if (isObject(out.data)) {
    out.data = Object.fromEntries(Object.entries(out.data).map(([mime, v]) => [mime, mime === 'application/json' || mime.endsWith('+json') ? v : textOf(v)]))
  }
  if (Array.isArray(out.traceback)) out.traceback = out.traceback.filter((l) => typeof l === 'string')
  return out as Output
}

/** A notebook's text read (an empty file is a new notebook); what is not nbformat 4 says why. */
export function parseNotebook(text: string): Notebook | NotNotebook {
  if (!text.trim()) return emptyNotebook()
  let raw: unknown
  try {
    raw = JSON.parse(text)
  } catch (e) {
    return { error: `The notebook is not JSON: ${e instanceof Error ? e.message : String(e)}` }
  }
  if (!isObject(raw)) return { error: 'The notebook is not a JSON object.' }
  const { cells, metadata, nbformat, nbformat_minor, ...rest } = raw
  if (nbformat !== 4) return { error: `Only nbformat 4 notebooks open here (this one is ${typeof nbformat === 'number' ? nbformat : 'of no version'}): open it as text.` }
  if (!Array.isArray(cells)) return { error: 'The notebook has no list of cells.' }
  const indent = /^\{\r?\n( +)"/.exec(text)?.[1]?.length ?? 1
  const minor = typeof nbformat_minor === 'number' ? nbformat_minor : 0
  return {
    nbformat: 4,
    minor,
    indent,
    metadata: isObject(metadata) ? metadata : {},
    rest,
    cells: cells.filter(isObject).map((c) => {
      const { cell_type, id, source, outputs, execution_count, metadata: own, ...others } = c
      const type: CellType = cell_type === 'markdown' || cell_type === 'raw' ? cell_type : 'code'
      return {
        id: typeof id === 'string' && id ? id : newId(),
        type,
        source: textOf(source),
        outputs: type === 'code' && Array.isArray(outputs) ? outputs.filter(isObject).map(readOutput) : [],
        count: type === 'code' && typeof execution_count === 'number' ? execution_count : null,
        metadata: isObject(own) ? own : {},
        rest: others,
      }
    }),
  }
}

/** Objects with their keys sorted, all the way down (Jupyter writes with sort_keys). */
function sorted(v: unknown): unknown {
  if (Array.isArray(v)) return v.map(sorted)
  if (!isObject(v)) return v
  return Object.fromEntries(
    Object.keys(v)
      .sort()
      .map((k) => [k, sorted(v[k])]),
  )
}

function writeOutput(o: Output): Record<string, unknown> {
  const out: Record<string, unknown> = { ...o }
  if (typeof out.text === 'string') out.text = linesOf(out.text)
  if (isObject(out.data)) {
    out.data = Object.fromEntries(
      Object.entries(out.data).map(([mime, v]) => [mime, typeof v === 'string' && (mime.startsWith('text/') || mime === 'image/svg+xml') ? linesOf(v) : v]),
    )
  }
  return out
}

/** A notebook as its file: what Jupyter and VS Code would write. */
export function serializeNotebook(nb: Notebook): string {
  const withIds = nb.nbformat > 4 || nb.minor >= 5
  const cells = nb.cells.map((c) => {
    const base: Record<string, unknown> = { ...c.rest, cell_type: c.type, metadata: c.metadata, source: linesOf(c.source) }
    if (withIds) base.id = c.id
    if (c.type === 'code') {
      base.execution_count = c.count
      base.outputs = c.outputs.map(writeOutput)
    }
    return base
  })
  return JSON.stringify(sorted({ ...nb.rest, cells, metadata: nb.metadata, nbformat: nb.nbformat, nbformat_minor: nb.minor }), null, nb.indent) + '\n'
}

/** A terminal's escape codes: colours and moves (CSI), and titles (OSC). */
const [esc, bell] = [String.fromCharCode(27), String.fromCharCode(7)]
const ansi = new RegExp(`${esc}\\[[0-9;?]*[A-Za-z]|${esc}\\][^${bell}]*${bell}`, 'g')

/** The colours and moves of a terminal's escape codes taken out of a text (tracebacks, coloured logs). */
export const stripAnsi = (text: string) => text.replace(ansi, '')

/** The forms the notebook shows, the richest first: as VS Code's renderers choose. */
const shownForms = ['image/png', 'image/jpeg', 'image/gif', 'image/svg+xml', 'text/html', 'text/markdown', 'text/latex', 'application/json', 'text/plain'] as const
export type ShownForm = (typeof shownForms)[number]

/** A display's or a result's richest form the editor shows, and its value. */
export function richest(data: unknown): { mime: ShownForm; value: unknown } | null {
  if (!isObject(data)) return null
  for (const mime of shownForms) if (mime in data && data[mime] !== undefined && data[mime] !== null) return { mime, value: data[mime] }
  return null
}

/** The language a notebook's code is in, as its metadata says (Python when it says none). */
export function notebookLanguage(nb: Notebook): string {
  const info = nb.metadata.language_info
  const spec = nb.metadata.kernelspec
  const name = (isObject(info) && typeof info.name === 'string' && info.name) || (isObject(spec) && typeof spec.language === 'string' && spec.language) || 'python'
  return name.toLowerCase()
}
