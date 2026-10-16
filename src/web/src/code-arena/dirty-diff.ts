import type * as Monaco from 'monaco-editor'
import { gitShow } from './ide-api'

/**
 * VS Code's quick diff: bars beside the line numbers for the lines changed since what git's index has (added green,
 * changed blue, a deletion a red mark between lines), drawn as the file is typed in. Alt+F3 and Shift+Alt+F3 go to the
 * next and the previous change.
 */

export interface LineChange {
  kind: 'added' | 'modified' | 'deleted'
  /** The lines of the file now (a deletion: the line after which lines were taken away, 0 for the top). */
  start: number
  end: number
}

/**
 * The work (diagonals tried and lines compared) a diff may take before it gives up: about 1,400 lines changed apart, a
 * few ms and a trace of a few MB. Time and the trace it keeps grow as the square of the lines changed, so a file
 * rewritten or reformatted over thousands of lines would take hundreds of MB and a pause at each key.
 */
const diffBudget = 1_000_000

/**
 * The lines changed from a text to another (Myers' diff on lines), as runs: lines only in the second are added, a run
 * replaced is modified, lines only in the first are a deletion after the line before them. Lines are compared without
 * their ends, as VS Code does: a file checked out with CRLF is the same as git's index with LF. Past the budget, the
 * lines from the first to the last change are one change.
 */
export function lineChanges(before: string, after: string): LineChange[] {
  const a = before.split(/\r\n|\r|\n/)
  const b = after.split(/\r\n|\r|\n/)
  // Common start and end left out: most edits are small.
  let head = 0
  while (head < a.length && head < b.length && a[head] === b[head]) head++
  let tail = 0
  while (tail < a.length - head && tail < b.length - head && a[a.length - 1 - tail] === b[b.length - 1 - tail]) tail++
  const x = a.slice(head, a.length - tail)
  const y = b.slice(head, b.length - tail)
  if (x.length === 0 && y.length === 0) return []
  // Lines only added, or only taken away (a paste, a cut), need no diff however many.
  if (x.length === 0) return [{ kind: 'added', start: head + 1, end: head + y.length }]
  if (y.length === 0) return [{ kind: 'deleted', start: head, end: head }]
  // Myers: the shortest edit script, as a trace of the furthest reach on each diagonal.
  const n = x.length
  const m = y.length
  const max = n + m
  const v = new Int32Array(2 * max + 2)
  // Step d reads only the diagonals step d - 1 reached, -(d - 1) to d - 1: those are kept, diagonal k at k + d - 1.
  const trace: Int32Array[] = []
  let work = 0
  outer: for (let d = 0; d <= max; d++) {
    trace.push(v.slice(max - d + 1, max + d))
    for (let k = -d; k <= d; k += 2) {
      let i = k === -d || (k !== d && v[max + k - 1]! < v[max + k + 1]!) ? v[max + k + 1]! : v[max + k - 1]! + 1
      let j = i - k
      const from = i
      while (i < n && j < m && x[i] === y[j]) {
        i++
        j++
      }
      v[max + k] = i
      if (i >= n && j >= m) break outer
      work += i - from + 1
      if (work > diffBudget) return [{ kind: 'modified', start: head + 1, end: head + m }]
    }
  }
  // Back through the trace: each step one line taken away (from x) or added (from y).
  const ops: ('=' | '-' | '+')[] = []
  let i = n
  let j = m
  for (let d = trace.length - 1; d > 0; d--) {
    const vd = trace[d]!
    const at = d - 1
    const k = i - j
    const down = k === -d || (k !== d && vd[at + k - 1]! < vd[at + k + 1]!)
    const pk = down ? k + 1 : k - 1
    const pi = vd[at + pk]!
    const pj = pi - pk
    while (i > pi && j > pj) {
      ops.push('=')
      i--
      j--
    }
    ops.push(down ? '+' : '-')
    if (down) j--
    else i--
  }
  while (i > 0 && j > 0) {
    ops.push('=')
    i--
    j--
  }
  ops.reverse()
  // Runs of removals and additions: both make a change, additions alone are added, removals alone a deletion.
  const out: LineChange[] = []
  let line = head
  for (let p = 0; p < ops.length; ) {
    if (ops[p] === '=') {
      line++
      p++
      continue
    }
    let removed = 0
    let added = 0
    while (p < ops.length && ops[p] !== '=') {
      if (ops[p] === '-') removed++
      else added++
      p++
    }
    if (added === 0) out.push({ kind: 'deleted', start: line, end: line })
    else {
      out.push({ kind: removed > 0 ? 'modified' : 'added', start: line + 1, end: line + added })
      line += added
    }
  }
  return out
}

const css = `
.arena-dirty-added, .arena-dirty-modified { border-left: 3px solid; margin-left: 3px; }
.arena-dirty-added { border-color: #2ea043; }
.arena-dirty-modified { border-color: #1b81a8; }
.arena-dirty-deleted { border-left: 4px solid transparent; margin-left: 2px; }
.arena-dirty-deleted::after { content: ''; position: absolute; bottom: -4px; left: 2px; border: 4px solid transparent; border-left-color: #e51400; }
`

let styled = false

/**
 * Quick diff in the code editor: the file shown against git's index, read when the file is shown and again when told
 * (it was saved, staged, committed), drawn as it is typed in. The function stops it.
 */
export function installDirtyDiff(monaco: typeof Monaco, ed: Monaco.editor.IStandaloneCodeEditor, pathOf: (m: Monaco.editor.ITextModel) => string | null) {
  if (!styled && typeof document !== 'undefined') {
    styled = true
    const el = document.createElement('style')
    el.textContent = css
    document.head.appendChild(el)
  }
  const bars = ed.createDecorationsCollection?.()
  const index = new Map<string, string | null>()
  let changes: LineChange[] = []
  let timer: ReturnType<typeof setTimeout> | undefined
  const draw = () => {
    const model = ed.getModel()
    const path = model ? pathOf(model) : null
    const base = path ? index.get(path) : undefined
    if (!bars || !model || !path || base === undefined || base === null) {
      bars?.clear()
      changes = []
      return
    }
    changes = lineChanges(base, model.getValue())
    bars.set(
      changes.map((c) => ({
        range: new monaco.Range(Math.max(1, c.start), 1, Math.max(1, c.end), 1),
        options: { isWholeLine: true, linesDecorationsClassName: `arena-dirty-${c.kind}`, overviewRuler: { color: c.kind === 'added' ? '#2ea04399' : c.kind === 'modified' ? '#1b81a899' : '#e5140099', position: monaco.editor.OverviewRulerLane.Left } },
      })),
    )
  }
  const read = async () => {
    const model = ed.getModel()
    const path = model ? pathOf(model) : null
    if (!path) return draw()
    try {
      const shown = await gitShow(path, ':')
      // Not in git (new, ignored), or not text: no bars.
      index.set(path, shown.exists && !shown.binary && !shown.tooLarge ? (shown.text ?? '') : null)
    } catch {
      index.set(path, null)
    }
    draw()
  }
  const later = () => {
    clearTimeout(timer)
    timer = setTimeout(draw, 200)
  }
  const go = (delta: 1 | -1) => {
    if (changes.length === 0) return
    const at = ed.getPosition()?.lineNumber ?? 1
    const next = delta > 0 ? (changes.find((c) => c.start > at) ?? changes[0]!) : (changes.findLast((c) => c.start < at) ?? changes.at(-1)!)
    const line = Math.max(1, next.start)
    ed.setPosition({ lineNumber: line, column: 1 })
    ed.revealLineInCenter(line)
  }
  const a = ed.onDidChangeModel(() => void read())
  const b = ed.onDidChangeModelContent(later)
  ed.addAction({ id: 'editor.action.dirtydiff.next', label: 'Go to Next Change', keybindings: [monaco.KeyMod.Alt | monaco.KeyCode.F3], run: () => go(1) })
  ed.addAction({ id: 'editor.action.dirtydiff.previous', label: 'Go to Previous Change', keybindings: [monaco.KeyMod.Shift | monaco.KeyMod.Alt | monaco.KeyCode.F3], run: () => go(-1) })
  void read()
  return {
    /** git's index may have changed (a save, a stage, a commit): read again. */
    refresh: () => {
      index.clear()
      void read()
    },
    dispose: () => {
      clearTimeout(timer)
      a.dispose()
      b.dispose()
      bars?.clear()
    },
  }
}
