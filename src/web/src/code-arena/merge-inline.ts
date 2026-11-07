import type * as Monaco from 'monaco-editor'
import { runCommand } from './commands'
import type { MonacoModule } from './editor-state'
import { findConflicts, linesOf, resolveAll, resolveText, type Choice, type Conflict } from './merge-conflicts'

// VS Code's merge conflicts in the text editor: the current change, the incoming one (and the common ancestor, with
// diff3) coloured, each conflict's CodeLens above it (Accept Current Change | Accept Incoming Change | Accept Both
// Changes | Resolve in Merge Editor), and the commands to accept, accept all and go to the next or previous conflict.

/** What the lenses and colours read: "merge-conflict.codeLens.enabled" and "merge-conflict.decorators.enabled". */
export interface InlineOptions {
  codeLens: boolean
  decorators: boolean
}

let options: InlineOptions = { codeLens: true, decorators: true }
let installed: MonacoModule | null = null
/** The models the merge editor shows as its result: its own controls resolve them, not the lenses. */
export const mergeEditorModels = new Set<Monaco.editor.ITextModel>()
const redraws = new Set<() => void>()
let lensesChanged: Monaco.Emitter<Monaco.languages.CodeLensProvider> | null = null

const css = `
.arena-merge-current-header { background: rgba(64, 200, 174, 0.35); }
.arena-merge-current { background: rgba(64, 200, 174, 0.05); }
.arena-merge-incoming-header { background: rgba(64, 166, 255, 0.35); }
.arena-merge-incoming { background: rgba(64, 166, 255, 0.05); }
.arena-merge-base-header { background: rgba(128, 128, 128, 0.3); }
.arena-merge-base { background: rgba(128, 128, 128, 0.05); }
.arena-merge-current-bar, .arena-merge-incoming-bar, .arena-merge-base-bar { border-left: 4px solid; margin-left: 3px; }
.arena-merge-current-bar { border-color: rgb(40, 160, 138); }
.arena-merge-incoming-bar { border-color: rgb(40, 130, 220); }
.arena-merge-base-bar { border-color: rgb(128, 128, 128); }
.arena-merge-label { opacity: 0.8; font-style: italic; }
.monaco-editor .codelens-decoration > a, .monaco-editor .codelens-decoration > span { color: var(--muted-foreground); }
`

/** The settings changed: the files show conflicts as they now say. */
export function setInlineOptions(next: InlineOptions) {
  if (next.codeLens === options.codeLens && next.decorators === options.decorators) return
  options = next
  for (const r of redraws) r()
  lensesChanged?.fire(undefined as never)
}

/** A conflict's decorations: its markers' lines and each part's, in VS Code's colours. */
export function conflictDecorations(c: Conflict): { range: Monaco.IRange; options: Monaco.editor.IModelDecorationOptions }[] {
  const whole = (start: number, end: number, className: string, extra: Partial<Monaco.editor.IModelDecorationOptions> = {}) =>
    end < start ? [] : [{ range: { startLineNumber: start, startColumn: 1, endLineNumber: end, endColumn: 1 }, options: { isWholeLine: true, className, ...extra } }]
  const ruler = (color: string) => ({ overviewRuler: { color, position: 7 as Monaco.editor.OverviewRulerLane } })
  return [
    ...whole(c.start, c.start, 'arena-merge-current-header', { after: { content: ' (Current Change)', inlineClassName: 'arena-merge-label' } }),
    ...whole(c.current.start, c.current.end - 1, 'arena-merge-current', { linesDecorationsClassName: 'arena-merge-current-bar', ...ruler('rgba(64, 200, 174, 0.8)') }),
    ...(c.baseMarker !== null && c.base ? [...whole(c.baseMarker, c.baseMarker, 'arena-merge-base-header', { after: { content: ' (Common Ancestor)', inlineClassName: 'arena-merge-label' } }), ...whole(c.base.start, c.base.end - 1, 'arena-merge-base', { linesDecorationsClassName: 'arena-merge-base-bar' })] : []),
    ...whole(c.incoming.start, c.incoming.end - 1, 'arena-merge-incoming', { linesDecorationsClassName: 'arena-merge-incoming-bar', ...ruler('rgba(64, 166, 255, 0.8)') }),
    ...whole(c.end, c.end, 'arena-merge-incoming-header', { after: { content: ' (Incoming Change)', inlineClassName: 'arena-merge-label' } }),
  ]
}

/** One conflict resolved in a model as one edit (Undo takes it back): the lines from its <<<<<<< to its >>>>>>>. */
export function resolveInModel(model: Monaco.editor.ITextModel, c: Conflict, choice: Choice) {
  const text = model.getValue()
  const next = resolveText(text, c, choice)
  const lines = linesOf(text)
  const before = lines.slice(0, c.start - 1).join('').length
  const after = lines.slice(c.end).join('').length
  const from = model.getPositionAt(before)
  const to = model.getPositionAt(text.length - after)
  model.pushStackElement()
  model.pushEditOperations([], [{ range: { startLineNumber: from.lineNumber, startColumn: from.column, endLineNumber: to.lineNumber, endColumn: to.column }, text: next.slice(before, next.length - after) }], () => null)
  model.pushStackElement()
}

/** Every conflict of a model resolved one way, as one edit. */
export function resolveAllInModel(model: Monaco.editor.ITextModel, choice: Choice) {
  const text = model.getValue()
  const next = resolveAll(text, choice)
  if (next === text) return
  model.pushStackElement()
  model.pushEditOperations([], [{ range: model.getFullModelRange(), text: next }], () => null)
  model.pushStackElement()
}

/** The conflict at a line (in its markers or parts), or the next one after it; with `previous`, the one before. */
export function conflictNear(conflicts: Conflict[], line: number, where: 'at' | 'next' | 'previous'): Conflict | undefined {
  if (where === 'at') return conflicts.find((c) => c.start <= line && line <= c.end)
  if (where === 'next') return conflicts.find((c) => c.start > line) ?? conflicts[0]
  return [...conflicts].reverse().find((c) => c.end < line) ?? conflicts.at(-1)
}

/** Monaco, once loaded: conflicts coloured in every file that has them, and their lenses. */
export function installMergeConflicts(m: MonacoModule) {
  if (installed === m) return
  installed = m
  const { monaco } = m
  if (typeof document !== 'undefined') {
    const el = document.createElement('style')
    el.textContent = css
    document.head.appendChild(el)
  }
  const watch = (model: Monaco.editor.ITextModel) => {
    let ids: string[] = []
    let timer: ReturnType<typeof setTimeout> | undefined
    const draw = () => {
      if (model.isDisposed()) return
      const conflicts = options.decorators ? findConflicts(model.getValue()) : []
      ids = model.deltaDecorations(ids, conflicts.flatMap(conflictDecorations))
    }
    draw()
    const changes = model.onDidChangeContent(() => {
      clearTimeout(timer)
      timer = setTimeout(draw, 100)
    })
    redraws.add(draw)
    model.onWillDispose(() => {
      clearTimeout(timer)
      changes.dispose()
      redraws.delete(draw)
    })
  }
  for (const model of monaco.editor.getModels()) watch(model)
  monaco.editor.onDidCreateModel(watch)

  monaco.editor.registerCommand('codeArena.merge.accept', (_accessor, uri: string, start: number, choice: Choice) => {
    const model = monaco.editor.getModel(monaco.Uri.parse(uri))
    const c = model && findConflicts(model.getValue()).find((x) => x.start === start)
    if (model && c) resolveInModel(model, c, choice)
  })
  monaco.editor.registerCommand('codeArena.merge.openEditor', (_accessor, uri: string) => {
    const path = m.pathOf(monaco.Uri.parse(uri))
    if (path) runCommand('git.openMergeEditor', path)
  })
  lensesChanged = new monaco.Emitter<Monaco.languages.CodeLensProvider>()
  monaco.languages.registerCodeLensProvider('*', {
    onDidChange: lensesChanged.event,
    provideCodeLenses: (model) => {
      if (!options.codeLens || mergeEditorModels.has(model) || !['file', 'outside'].includes(model.uri.scheme)) return { lenses: [], dispose: () => undefined }
      const uri = model.uri.toString()
      const lenses = findConflicts(model.getValue()).flatMap((c) => {
        const range = { startLineNumber: c.start, startColumn: 1, endLineNumber: c.start, endColumn: 1 }
        const lens = (title: string, choice: Choice) => ({ range, command: { id: 'codeArena.merge.accept', title, arguments: [uri, c.start, choice] } })
        return [
          lens('Accept Current Change', 'current'),
          lens('Accept Incoming Change', 'incoming'),
          lens('Accept Both Changes', 'both'),
          { range, command: { id: 'codeArena.merge.openEditor', title: 'Resolve in Merge Editor', arguments: [uri] } },
        ]
      })
      return { lenses, dispose: () => undefined }
    },
  })
}

/** What a merge-conflict command does to the file shown, at its cursor. */
export type ConflictAction = Choice | 'all-current' | 'all-incoming' | 'all-both' | 'next' | 'previous'

/** A merge-conflict command (merge-conflict.accept.current and the others) on an editor, at its cursor; false when it has no conflict. */
export function conflictAction(editor: Monaco.editor.ICodeEditor, action: ConflictAction): boolean {
  const model = editor.getModel()
  if (!model) return false
  const conflicts = findConflicts(model.getValue())
  if (conflicts.length === 0) return false
  if (action.startsWith('all-')) {
    resolveAllInModel(model, action.slice(4) as Choice)
    return true
  }
  const line = editor.getPosition()?.lineNumber ?? 1
  if (action === 'next' || action === 'previous') {
    const c = conflictNear(conflicts, line, action)
    if (!c) return false
    editor.setPosition({ lineNumber: c.start, column: 1 })
    editor.revealLineInCenterIfOutsideViewport(c.start)
    return true
  }
  // At the cursor, else the next one: as VS Code's commands do.
  const c = conflictNear(conflicts, line, 'at') ?? conflictNear(conflicts, line, 'next')
  if (!c) return false
  resolveInModel(model, c, action as Choice)
  return true
}
