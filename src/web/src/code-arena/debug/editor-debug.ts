import type * as Monaco from 'monaco-editor'
import { debug, onDebugChange, type Session } from './session'

/**
 * The debugger in the code editor, as VS Code's: breakpoints in the glyph margin (a click sets or clears one; hollow
 * while no session has bound it, a mark for a condition or a log message, grey when off), the line a session stopped at
 * (yellow, the frame chosen green), and a hover with an expression's value while stopped. Breakpoints follow their
 * lines as the file is edited.
 */

const css = `
.arena-bp, .arena-bp-off, .arena-bp-pending, .arena-bp-condition, .arena-bp-log, .arena-frame, .arena-frame-focused { display: flex !important; align-items: center; justify-content: center; }
.arena-bp::before, .arena-bp-condition::before, .arena-bp-pending::before, .arena-bp-off::before, .arena-bp-log::before { content: ''; width: 10px; height: 10px; border-radius: 50%; background: #e51400; }
.arena-bp-condition::before { box-shadow: inset 0 0 0 2px #e51400; background: radial-gradient(circle, #fff 30%, #e51400 32%); }
.arena-bp-log::before { border-radius: 2px; transform: rotate(45deg); width: 8px; height: 8px; }
.arena-bp-pending::before { background: transparent; box-shadow: inset 0 0 0 2px #848484; }
.arena-bp-off::before { background: #848484; }
.arena-frame::before, .arena-frame-focused::before { content: '➜'; color: #ffcc00; font-size: 12px; line-height: 1; }
.arena-frame-focused::before { color: #89d185; }
.arena-frame-line { background: rgba(255, 204, 0, 0.18); }
.arena-frame-focused-line { background: rgba(137, 209, 133, 0.18); }
`

let styled = false
function style() {
  if (styled || typeof document === 'undefined') return
  styled = true
  const el = document.createElement('style')
  el.textContent = css
  document.head.appendChild(el)
}

/** The file the editor shows, by its path in the folder; null for another model. */
type PathOf = (model: Monaco.editor.ITextModel) => string | null

export function installDebugEditor(monaco: typeof Monaco, ed: Monaco.editor.IStandaloneCodeEditor, pathOf: PathOf): () => void {
  style()
  let decorations: Monaco.editor.IEditorDecorationsCollection | null = ed.createDecorationsCollection?.() ?? null
  // The decoration of each breakpoint, by its line when drawn: where it is after edits says where the breakpoint went.
  let drawn: { id: string; line: number }[] = []

  const draw = () => {
    const model = ed.getModel()
    const path = model ? pathOf(model) : null
    if (!decorations || !model || !path) {
      decorations?.clear()
      drawn = []
      return
    }
    const s = debug.active
    const results = s?.results[path]
    const enabled = (debug.breakpoints[path] ?? []).filter((b) => b.enabled)
    const list: Monaco.editor.IModelDeltaDecoration[] = []
    const lines = model.getLineCount()
    for (const b of debug.breakpoints[path] ?? []) {
      if (b.line < 1 || b.line > lines) continue
      const at = enabled.indexOf(b)
      const result = at >= 0 ? results?.[at] : undefined
      const kind = !b.enabled ? 'arena-bp-off' : s && result && !result.verified ? 'arena-bp-pending' : b.logMessage ? 'arena-bp-log' : b.condition || b.hitCondition ? 'arena-bp-condition' : 'arena-bp'
      const hover = b.logMessage ? `Log: ${b.logMessage}` : b.condition ? `Breaks when ${b.condition}` : b.hitCondition ? `Breaks at hit ${b.hitCondition}` : result && !result.verified ? (result.message ?? 'Not bound to code yet') : 'Breakpoint'
      list.push({
        range: new monaco.Range(b.line, 1, b.line, 1),
        options: { glyphMarginClassName: kind, glyphMarginHoverMessage: { value: hover }, stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges },
      })
    }
    const marks = list.length
    if (s?.state === 'stopped') {
      const top = s.frames[0]
      const chosen = s.frames.find((f) => f.id === s.frameId)
      for (const f of [top, chosen !== top ? chosen : undefined]) {
        if (!f || f.path !== path || f.line < 1 || f.line > lines) continue
        const focused = f !== top
        list.push({
          range: new monaco.Range(f.line, 1, f.line, model.getLineMaxColumn(f.line)),
          options: { isWholeLine: true, className: focused ? 'arena-frame-focused-line' : 'arena-frame-line', glyphMarginClassName: focused ? 'arena-frame-focused' : 'arena-frame', stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges },
        })
      }
    }
    decorations.set(list)
    drawn = (debug.breakpoints[path] ?? [])
      .filter((b) => b.line >= 1 && b.line <= lines)
      .map((b, i) => ({ id: String(i), line: b.line }))
      .slice(0, marks)
  }

  const offStore = onDebugChange(draw)
  const offModel = ed.onDidChangeModel(draw)
  // Edited: each breakpoint goes where its line went.
  const offContent = ed.onDidChangeModelContent(() => {
    const model = ed.getModel()
    const path = model ? pathOf(model) : null
    if (!model || !path || !decorations || drawn.length === 0) return
    const ranges = decorations.getRanges?.() ?? []
    const moved = new Map<number, number>()
    drawn.forEach((d, i) => {
      const r = ranges[i]
      if (r && r.startLineNumber !== d.line) moved.set(d.line, r.startLineNumber)
    })
    if (moved.size > 0) debug.moveBreakpoints(path, moved)
  })
  // A click in the glyph margin sets or clears a breakpoint.
  const offMouse = ed.onMouseDown((e) => {
    if (e.target.type !== monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN || !e.target.position) return
    const model = ed.getModel()
    const path = model ? pathOf(model) : null
    if (path) debug.toggle(path, e.target.position.lineNumber)
  })
  // While stopped: an expression's value under the pointer.
  const hover = monaco.languages.registerHoverProvider('*', {
    provideHover: async (model, position) => {
      const s = debug.active
      if (!s || s.state !== 'stopped' || model !== ed.getModel()) return null
      const word = model.getWordAtPosition(position)
      if (!word) return null
      const line = model.getLineContent(position.lineNumber)
      // a.b.c: the expression up to the word, as VS Code takes it.
      let start = word.startColumn
      while (start > 2 && /[\w$.]/.test(line[start - 2]!)) start--
      const expression = line.slice(start - 1, word.endColumn - 1).replace(/^\.+/, '')
      if (!expression) return null
      try {
        const r = await s.evaluate(expression, s.capabilities.supportsEvaluateForHovers ? 'hover' : 'watch')
        return { range: new monaco.Range(position.lineNumber, start, position.lineNumber, word.endColumn), contents: [{ value: `\`${expression}\`${r.type ? ` *${r.type}*` : ''}` }, { value: `\`\`\`\n${r.result}\n\`\`\`` }] }
      } catch {
        return null
      }
    },
  })
  draw()
  return () => {
    offStore()
    offModel.dispose()
    offContent.dispose()
    offMouse.dispose()
    hover.dispose()
    decorations?.clear()
    decorations = null
  }
}

/** The session the editor shows where it stopped: its top frame's file at its line. */
export function stoppedAt(s: Session) {
  return s.frames.find((f) => f.id === s.frameId) ?? s.frames[0] ?? null
}

/** The editor's right-click menu for the debugger, as VS Code's: a conditional breakpoint, a logpoint, Add to Watch. */
export function addDebugActions(monaco: typeof Monaco, ed: Monaco.editor.IStandaloneCodeEditor, pathOf: PathOf) {
  const here = () => {
    const model = ed.getModel()
    const path = model ? pathOf(model) : null
    const line = ed.getPosition()?.lineNumber
    return path && line ? { path, line, model: model! } : null
  }
  const ask = async (what: 'condition' | 'logMessage') => {
    const at = here()
    if (!at) return
    const now = debug.breakpoints[at.path]?.find((b) => b.line === at.line)
    const { inputBox } = await import('../quick-pick')
    const value = await inputBox({
      title: what === 'condition' ? `Breakpoint condition, line ${at.line}` : `Log message, line ${at.line}`,
      placeholder: what === 'condition' ? 'Breaks when the expression is true (empty: always)' : 'Logged when the line is hit; {expression} is replaced by its value',
      value: now?.[what],
    })
    if (value !== undefined) debug.edit(at.path, at.line, { [what]: value || undefined })
  }
  ed.addAction({ id: 'editor.debug.action.conditionalBreakpoint', label: 'Add Conditional Breakpoint…', contextMenuGroupId: 'debug', contextMenuOrder: 1, run: () => void ask('condition') })
  ed.addAction({ id: 'editor.debug.action.addLogPoint', label: 'Add Logpoint…', contextMenuGroupId: 'debug', contextMenuOrder: 2, run: () => void ask('logMessage') })
  ed.addAction({
    id: 'editor.debug.action.selectionToWatch',
    label: 'Add to Watch',
    contextMenuGroupId: 'debug',
    contextMenuOrder: 3,
    run: () => {
      const model = ed.getModel()
      const sel = ed.getSelection()
      if (!model || !sel) return
      const text = sel.isEmpty() ? (model.getWordAtPosition(sel.getStartPosition())?.word ?? '') : model.getValueInRange(sel)
      if (text.trim()) debug.setWatch([...debug.watch, text.trim()])
    },
  })
  void monaco
}
