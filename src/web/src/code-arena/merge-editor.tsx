import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowDown, ArrowUp, Check, RotateCcw } from 'lucide-react'
import type * as Monaco from 'monaco-editor'
import { useEffect, useLayoutEffect, useRef, useState, type RefObject } from 'react'
import { Button } from '@/components/ui/button'
import { useConfirm } from '@/components/ui/confirm'
import { Spinner } from '@/components/ui/spinner'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { loadMonaco, useEditor, type MonacoModule } from './editor-state'
import { editorOptionsOf } from './editor-options'
import { nameOf } from './ide-api'
import { completeMerge, conflictParts, findConflicts, linesOf, mergeInfoQuery, sideText, takenText, type LineRange, type MergeSide, type Taken } from './merge-conflicts'
import { installMergeConflicts, mergeEditorModels } from './merge-inline'
import { useSettings } from './settings'
import { useMonacoTheme } from './theme'

// VS Code's 3-way merge editor: the incoming side and the current side above, the result below (the file itself, as
// the editor holds it: saving saves it). Each conflict's part of a side has its Accept Incoming / Accept Current (both
// may be taken, the current first), the result can be edited by hand too, and Complete Merge saves the file and stages
// it, which tells git the conflict is resolved.

/** A side pane's lenses: the provider, given once, finds each pane's own by its model. */
interface SideLenses {
  side: 'current' | 'incoming'
  ranges: LineRange[]
  taken: () => Taken[]
  toggle: (index: number, side: 'current' | 'incoming' | 'both') => void
}
const sides = new Map<string, SideLenses>()
let lensesFor: MonacoModule | null = null
let lensesChanged: Monaco.Emitter<Monaco.languages.CodeLensProvider> | null = null

function installSideLenses(m: MonacoModule) {
  if (lensesFor === m) return
  lensesFor = m
  const { monaco } = m
  monaco.editor.registerCommand('codeArena.mergeEditor.take', (_accessor, uri: string, index: number, side: 'current' | 'incoming' | 'both') => sides.get(uri)?.toggle(index, side))
  lensesChanged = new monaco.Emitter<Monaco.languages.CodeLensProvider>()
  monaco.languages.registerCodeLensProvider('*', {
    onDidChange: lensesChanged.event,
    provideCodeLenses: (model) => {
      const own = sides.get(model.uri.toString())
      if (!own) return { lenses: [], dispose: () => undefined }
      const taken = own.taken()
      const lenses = own.ranges.flatMap((r, i) => {
        const range = { startLineNumber: Math.max(1, r.start), startColumn: 1, endLineNumber: Math.max(1, r.start), endColumn: 1 }
        const on = taken[i]?.[own.side] ?? false
        const both = !!taken[i]?.current && !!taken[i]?.incoming
        const name = own.side === 'current' ? 'Current' : 'Incoming'
        return [
          { range, command: { id: 'codeArena.mergeEditor.take', title: `${on ? '☑' : '☐'} Accept ${name}`, arguments: [model.uri.toString(), i, own.side] } },
          { range, command: { id: 'codeArena.mergeEditor.take', title: both ? '☑ Accept Combination' : 'Accept Combination', arguments: [model.uri.toString(), i, 'both'] } },
        ]
      })
      return { lenses, dispose: () => undefined }
    },
  })
}

const sideClass = { current: 'arena-merge-current', incoming: 'arena-merge-incoming' }

/** A side's name in its pane's header: "feature" and its commit, else the marker's label. */
function sideTitle(side: MergeSide | null | undefined, fallback: string) {
  if (!side) return fallback || '—'
  return `${side.name} · ${side.short}${side.subject ? ` ${side.subject}` : ''}`
}

export function MergeEditor({ id, path }: { id: string; path: string }) {
  const ed = useEditor()
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const settings = useSettings()
  const theme = useMonacoTheme()
  const info = useQuery(mergeInfoQuery(path))
  const incomingHost = useRef<HTMLDivElement>(null)
  const currentHost = useRef<HTMLDivElement>(null)
  const resultHost = useRef<HTMLDivElement>(null)
  const [ready, setReady] = useState<'loading' | 'ready' | 'none'>('loading')
  const [labels, setLabels] = useState({ current: '', incoming: '' })
  const [remaining, setRemaining] = useState(0)
  const [count, setCount] = useState(0)
  // What the panes' buttons and the toolbar work with, made once the panes are.
  const control = useRef<{ go: (delta: 1 | -1) => void; reset: () => void; model: Monaco.editor.ITextModel } | null>(null)
  const settingsRef = useRef(settings)
  const themeRef = useRef(theme)
  // The editor's api changes as its tabs do (this one's dot among them): the panes are made once, with the latest.
  const edRef = useRef(ed)
  useLayoutEffect(() => {
    settingsRef.current = settings
    themeRef.current = theme
    edRef.current = ed
  })

  useEffect(() => {
    let gone = false
    const own: { dispose(): void }[] = []
    void (async () => {
      const m = await loadMonaco()
      const model = await edRef.current.modelFor(path)
      if (gone) return
      if (!model || !incomingHost.current || !currentHost.current || !resultHost.current) {
        setReady('none')
        return
      }
      installMergeConflicts(m)
      installSideLenses(m)
      const { monaco } = m
      const original = model.getValue()
      const conflicts = findConflicts(original)
      const parts = conflictParts(original, conflicts)
      const taken: Taken[] = conflicts.map(() => ({ current: false, incoming: false }))
      setCount(conflicts.length)
      setLabels({ current: conflicts[0]?.currentLabel ?? '', incoming: conflicts[0]?.incomingLabel ?? '' })
      const language = model.getLanguageId()
      const options = { theme: themeRef.current, automaticLayout: true, ...editorOptionsOf(settingsRef.current.all(language)), fixedOverflowWidgets: true, minimap: { enabled: false } }
      const made = (side: 'current' | 'incoming', host: HTMLDivElement) => {
        const s = sideText(original, side)
        const uri = monaco.Uri.from({ scheme: `merge-${side}`, path: `/${path}` })
        monaco.editor.getModel(uri)?.dispose()
        const sideModel = monaco.editor.createModel(s.text, language, uri)
        const editor = monaco.editor.create(host, { ...options, model: sideModel, readOnly: true, domReadOnly: true, ariaLabel: `${side === 'current' ? 'Current' : 'Incoming'} side of ${nameOf(path)}` })
        sideModel.deltaDecorations(
          [],
          s.ranges.filter((r) => r.end > r.start).map((r) => ({ range: { startLineNumber: r.start, startColumn: 1, endLineNumber: r.end - 1, endColumn: 1 }, options: { isWholeLine: true, className: sideClass[side], linesDecorationsClassName: `${sideClass[side]}-bar` } })),
        )
        sides.set(uri.toString(), { side, ranges: s.ranges, taken: () => taken, toggle: (i, which) => take(i, which) })
        own.push(editor, sideModel, { dispose: () => sides.delete(uri.toString()) })
        return { editor, ranges: s.ranges }
      }
      const incoming = made('incoming', incomingHost.current)
      const current = made('current', currentHost.current)
      const result = monaco.editor.create(resultHost.current, { ...options, model, ariaLabel: `Result of merging ${nameOf(path)}` })
      own.push(result)
      mergeEditorModels.add(model)
      own.push({ dispose: () => mergeEditorModels.delete(model) })

      // Where each conflict is in the result as it is edited: tracked, never grown by typing at its edges.
      const offsets = (() => {
        const lines = linesOf(original)
        return conflicts.map((c) => ({ start: lines.slice(0, c.start - 1).join('').length, end: lines.slice(0, c.end).join('').length }))
      })()
      const rangeAt = (start: number, end: number) => {
        const a = model.getPositionAt(start)
        const b = model.getPositionAt(end)
        return { startLineNumber: a.lineNumber, startColumn: a.column, endLineNumber: b.lineNumber, endColumn: b.column }
      }
      let tracked = model.deltaDecorations([], offsets.map((o) => ({ range: rangeAt(o.start, o.end), options: { stickiness: 1 } })))
      let shown: string[] = []
      const draw = () => {
        if (model.isDisposed()) return
        // Each conflict's lines coloured by what was taken; one not handled yet is left to the inline colours.
        shown = model.deltaDecorations(
          shown,
          tracked.flatMap((d, i) => {
            const r = model.getDecorationRange(d)
            const t = taken[i]!
            if (!r || (!t.current && !t.incoming) || r.isEmpty()) return []
            const last = r.endColumn === 1 && r.endLineNumber > r.startLineNumber ? r.endLineNumber - 1 : r.endLineNumber
            return [{ range: { startLineNumber: r.startLineNumber, startColumn: 1, endLineNumber: last, endColumn: 1 }, options: { isWholeLine: true, linesDecorationsClassName: `${t.current && t.incoming ? 'arena-merge-base' : t.current ? sideClass.current : sideClass.incoming}-bar`, className: t.current && t.incoming ? 'arena-merge-base' : t.current ? sideClass.current : sideClass.incoming } }]
          }),
        )
        setRemaining(findConflicts(model.getValue()).length)
      }
      const take = (i: number, which: 'current' | 'incoming' | 'both') => {
        const t = taken[i]
        const d = tracked[i]
        const r = d ? model.getDecorationRange(d) : null
        if (!t || !d || !r) return
        if (which === 'both') {
          const on = !(t.current && t.incoming)
          t.current = on
          t.incoming = on
        } else t[which] = !t[which]
        const text = takenText(parts[i]!, t)
        const start = model.getOffsetAt(r.getStartPosition())
        model.pushStackElement()
        model.pushEditOperations([], [{ range: r, text }], () => null)
        model.pushStackElement()
        const [moved] = model.deltaDecorations([d], [{ range: rangeAt(start, start + text.length), options: { stickiness: 1 } }])
        tracked = tracked.map((x) => (x === d ? moved! : x))
        lensesChanged?.fire(undefined as never)
        draw()
      }
      let at = -1
      const go = (delta: 1 | -1) => {
        if (tracked.length === 0) return
        at = (at + delta + tracked.length) % tracked.length
        const r = model.getDecorationRange(tracked[at]!)
        if (r) {
          result.revealLineInCenter(r.startLineNumber)
          result.setPosition({ lineNumber: r.startLineNumber, column: 1 })
        }
        for (const [pane, ranges] of [
          [incoming.editor, incoming.ranges],
          [current.editor, current.ranges],
        ] as const) {
          const line = ranges[at]?.start
          if (line) pane.revealLineInCenter(line)
        }
        result.focus()
      }
      const reset = () => {
        for (const t of taken) t.current = t.incoming = false
        model.pushStackElement()
        model.pushEditOperations([], [{ range: model.getFullModelRange(), text: original }], () => null)
        model.pushStackElement()
        tracked = model.deltaDecorations(tracked, offsets.map((o) => ({ range: rangeAt(o.start, o.end), options: { stickiness: 1 } })))
        lensesChanged?.fire(undefined as never)
        draw()
      }
      const listen = model.onDidChangeContent(() => setRemaining(findConflicts(model.getValue()).length))
      own.push(listen, { dispose: () => !model.isDisposed() && model.deltaDecorations([...tracked, ...shown], []) })
      control.current = { go, reset, model }
      draw()
      setReady('ready')
    })().catch((e: unknown) => {
      if (!gone) {
        toast.error(errorMessage(e))
        setReady('none')
      }
    })
    return () => {
      gone = true
      control.current = null
      for (const d of own.reverse()) d.dispose()
    }
    // The panes are made once for the file (the editor's theme is Monaco's, for every editor).
  }, [path])

  const complete = async () => {
    const c = control.current
    if (!c) return
    const left = findConflicts(c.model.getValue()).length
    if (left > 0 && !(await confirm({ title: `${left} conflict${left === 1 ? '' : 's'} not resolved`, description: `The markers stay in ${nameOf(path)} as they are. Complete the merge anyway?`, confirm: 'Complete Merge', destructive: true }))) return
    if (!(await ed.save(id))) return
    try {
      await completeMerge(path, left > 0)
      await queryClient.invalidateQueries({ queryKey: ['code', 'git'] })
      toast.success(`${nameOf(path)} is merged and staged.`)
      void ed.close(id)
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }

  const kind = info.data?.kind
  const pane = (title: string, detail: string, tone: string, host: RefObject<HTMLDivElement | null>, testId: string) => (
    <section className="flex min-h-0 min-w-0 flex-1 flex-col" aria-label={title}>
      <header className={`flex h-7 shrink-0 items-center gap-2 border-b px-2 text-xs ${tone}`}>
        <span className="font-semibold">{title}</span>
        <span className="min-w-0 truncate text-muted-foreground" title={detail}>
          {detail}
        </span>
      </header>
      <div ref={host} data-testid={testId} className="monaco-component relative min-h-0 flex-1" />
    </section>
  )
  return (
    <div className="absolute inset-0 flex flex-col bg-background" data-testid="merge-editor">
      <div role="toolbar" aria-label="Merge" className="flex shrink-0 flex-wrap items-center gap-1 border-b px-2 py-1 text-xs">
        <span className="mr-2 min-w-0 truncate font-medium">
          Merging {nameOf(path)}
          {kind && kind !== 'merge' ? ` (${kind})` : ''}
        </span>
        <output className="mr-auto text-muted-foreground">
          {ready !== 'ready' ? '' : remaining === 0 ? 'No conflicts remaining' : `${remaining} of ${count} conflict${count === 1 ? '' : 's'} remaining`}
        </output>
        <Button size="sm" variant="ghost" onClick={() => control.current?.go(-1)} aria-label="Previous conflict" title="Previous conflict">
          <ArrowUp aria-hidden="true" />
        </Button>
        <Button size="sm" variant="ghost" onClick={() => control.current?.go(1)} aria-label="Next conflict" title="Next conflict">
          <ArrowDown aria-hidden="true" />
        </Button>
        <Button size="sm" variant="ghost" onClick={() => control.current?.reset()} title="Reset the result to the file as it was when the merge editor opened">
          <RotateCcw aria-hidden="true" /> Reset
        </Button>
        <Button size="sm" onClick={() => void complete()} disabled={ready !== 'ready'}>
          <Check aria-hidden="true" /> Complete Merge
        </Button>
      </div>
      {info.data && !info.data.unmerged && ready === 'ready' && (
        <p className="shrink-0 border-b bg-muted/50 px-3 py-1 text-xs text-muted-foreground">git has this file merged already (it was staged): Complete Merge stages it again.</p>
      )}
      <div className="flex min-h-0 flex-[2] flex-col border-b sm:flex-row">
        {pane('Incoming', sideTitle(info.data?.incoming, labels.incoming), 'bg-[rgba(64,166,255,0.12)]', incomingHost, 'merge-incoming')}
        <div className="h-px w-full shrink-0 bg-border sm:h-full sm:w-px" aria-hidden="true" />
        {pane('Current', sideTitle(info.data?.current, labels.current), 'bg-[rgba(64,200,174,0.12)]', currentHost, 'merge-current')}
      </div>
      {pane('Result', path, 'bg-muted/40', resultHost, 'merge-result')}
      {ready === 'loading' && (
        <div className="absolute inset-0 grid place-items-center bg-background">
          <Spinner label={`Opening ${nameOf(path)} in the merge editor`} />
        </div>
      )}
      {ready === 'none' && (
        <div className="absolute inset-0 grid place-items-center bg-background p-6 text-center text-sm text-muted-foreground">
          <p>{nameOf(path)} is not text: it cannot be merged here.</p>
        </div>
      )}
    </div>
  )
}
