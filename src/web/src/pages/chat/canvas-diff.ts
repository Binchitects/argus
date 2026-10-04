/** One line of a diff: the same in both, added, or removed, with its numbers in the old and new text. */
export interface DiffLine {
  kind: 'same' | 'added' | 'removed'
  text: string
  old: number | null
  new: number | null
}

/** A row of a diff as shown: a line, or unchanged lines folded away. */
export type DiffRow = DiffLine | { kind: 'skip'; count: number }

/** Past this many differences the middle shows as removed, then added (a diff that long is a rewrite). */
const MaxEdits = 4000

/** Its lines; a last line break ends the last line, it does not start another (as the server counts them). */
export function linesOf(text: string): string[] {
  if (!text) return []
  return (text.endsWith('\n') ? text.slice(0, -1) : text).split('\n')
}

/**
 * The lines that differ between two texts, in order (Myers' algorithm, the one git uses):
 * the fewest lines removed and added that turn the old text into the new.
 */
export function diffLines(before: string, after: string): DiffLine[] {
  const a = linesOf(before)
  const b = linesOf(after)
  // What both start and end with needs no search.
  let start = 0
  while (start < a.length && start < b.length && a[start] === b[start]) start++
  let endA = a.length
  let endB = b.length
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) {
    endA--
    endB--
  }
  const out: DiffLine[] = []
  for (let i = 0; i < start; i++) out.push({ kind: 'same', text: a[i]!, old: i + 1, new: i + 1 })
  for (const [op, i, j] of middle(a.slice(start, endA), b.slice(start, endB))) {
    if (op === '=') out.push({ kind: 'same', text: a[start + i]!, old: start + i + 1, new: start + j + 1 })
    else if (op === '-') out.push({ kind: 'removed', text: a[start + i]!, old: start + i + 1, new: null })
    else out.push({ kind: 'added', text: b[start + j]!, old: null, new: start + j + 1 })
  }
  for (let i = endA, j = endB; i < a.length; i++, j++) out.push({ kind: 'same', text: a[i]!, old: i + 1, new: j + 1 })
  return out
}

type Op = ['=' | '-' | '+', number, number]

function middle(a: string[], b: string[]): Op[] {
  const n = a.length
  const m = b.length
  if (n === 0) return b.map((_, j): Op => ['+', 0, j])
  if (m === 0) return a.map((_, i): Op => ['-', i, 0])
  const max = Math.min(n + m, MaxEdits)
  const off = max + 1
  const v = new Int32Array(2 * max + 3)
  // Each step's furthest reaches, for the diagonals it could use: kept to walk back.
  const trace: Int32Array[] = []
  for (let d = 0; d <= max; d++) {
    trace.push(v.slice(off - d - 1, off + d + 2))
    for (let k = -d; k <= d; k += 2) {
      let x = k === -d || (k !== d && v[off + k - 1]! < v[off + k + 1]!) ? v[off + k + 1]! : v[off + k - 1]! + 1
      let y = x - k
      while (x < n && y < m && a[x] === b[y]) {
        x++
        y++
      }
      v[off + k] = x
      if (x >= n && y >= m) return back(trace, n, m)
    }
  }
  // Too different to search: all of the old, then all of the new.
  return [...a.map((_, i): Op => ['-', i, 0]), ...b.map((_, j): Op => ['+', 0, j])]
}

function back(trace: Int32Array[], n: number, m: number): Op[] {
  const ops: Op[] = []
  let x = n
  let y = m
  for (let d = trace.length - 1; d >= 0; d--) {
    const v = trace[d]!
    const at = (k: number) => v[k + d + 1]!
    const k = x - y
    const prevK = k === -d || (k !== d && at(k - 1) < at(k + 1)) ? k + 1 : k - 1
    const prevX = at(prevK)
    const prevY = prevX - prevK
    while (x > prevX && y > prevY) {
      ops.push(['=', x - 1, y - 1])
      x--
      y--
    }
    if (d > 0) {
      if (x === prevX) ops.push(['+', x, y - 1])
      else ops.push(['-', x - 1, y])
    }
    x = prevX
    y = prevY
  }
  return ops.reverse()
}

/** The changes with a few lines around each; the unchanged stretches between them fold away. */
export function diffRows(diff: DiffLine[], context = 3): DiffRow[] {
  const keep = diff.map(() => false)
  diff.forEach((l, i) => {
    if (l.kind === 'same') return
    for (let j = Math.max(0, i - context); j <= Math.min(diff.length - 1, i + context); j++) keep[j] = true
  })
  const rows: DiffRow[] = []
  let skipped = 0
  diff.forEach((l, i) => {
    if (keep[i]) {
      if (skipped) rows.push({ kind: 'skip', count: skipped })
      skipped = 0
      rows.push(l)
    } else skipped++
  })
  if (skipped) rows.push({ kind: 'skip', count: skipped })
  return rows
}

/** How many lines a change added and removed. */
export function diffStats(diff: DiffLine[]): { added: number; removed: number } {
  return { added: diff.filter((l) => l.kind === 'added').length, removed: diff.filter((l) => l.kind === 'removed').length }
}
