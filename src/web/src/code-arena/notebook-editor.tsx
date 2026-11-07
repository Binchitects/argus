import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowDown, ArrowUp, Check, Code2, Eraser, FileCode2, Play, PlayCircle, Plus, RotateCcw, Square, Trash2, Type, X } from 'lucide-react'
import type * as Monaco from 'monaco-editor'
import { memo, useCallback, useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { useConfirm } from '@/components/ui/confirm'
import { Spinner } from '@/components/ui/spinner'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { Markdown } from '@/pages/chat/markdown'
import { runCommand } from './commands'
import { loadMonaco, useEditor, type MonacoModule } from './editor-state'
import { editorOptionsOf } from './editor-options'
import { nameOf } from './ide-api'
import { interruptKernel, kernelQuery, restartKernel, runCell } from './notebook-api'
import { newCell, notebookLanguage, parseNotebook, richest, serializeNotebook, stripAnsi, textOf, type Cell, type CellType, type Notebook, type Output } from './notebook'
import { addSaveParticipant } from './save-participants'
import { useSettings } from './settings'
import { useMonacoTheme } from './theme'

// VS Code's notebook editor for Jupyter notebooks: the cells one under another, code in an editor of its own with its
// outputs below (what it printed, its pictures, HTML, Markdown, its error), Markdown shown as it reads (double-click or
// Enter edits it). Cells run in the notebook's kernel, a Python code-arena keeps for it (the project's .venv when there is
// one). The notebook is the file itself, as the editor holds it: its tab's dot, Ctrl+S, Undo of the file, and Reopen as
// text work as for any file.

/** Each notebook's pending write, flushed before a save. */
const pendingWrites = new Map<string, () => void>()
let flushOnSave: (() => void) | null = null

function useFlushBeforeSave() {
  useEffect(() => {
    flushOnSave ??= addSaveParticipant(({ path }) => pendingWrites.get(path)?.())
  }, [])
}

type RunState = { state: 'queued' | 'running' | 'ok' | 'failed'; took?: number }

const withCell = (nb: Notebook, id: string, f: (c: Cell) => Cell): Notebook => ({ ...nb, cells: nb.cells.map((c) => (c.id === id ? f(c) : c)) })

export function NotebookEditor({ path }: { path: string }) {
  const ed = useEditor()
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const settings = useSettings()
  const theme = useMonacoTheme()
  const [nb, setNb] = useState<Notebook | { error: string } | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [editing, setEditing] = useState<string | null>(null)
  const [runs, setRuns] = useState<Record<string, RunState>>({})
  const [monaco, setMonaco] = useState<MonacoModule | null>(null)
  // How many times the file was read (opened, or changed by anything else): a cell's editor takes its text then only,
  // never an echo of its own typing (a render behind the keys would put back what was there a key ago).
  const [revision, setRevision] = useState(0)
  const busy = Object.values(runs).some((r) => r.state === 'running' || r.state === 'queued')
  const kernel = useQuery({ ...kernelQuery(path), refetchInterval: busy ? 2000 : false })
  const edRef = useRef(ed)
  const nbRef = useRef<Notebook | null>(null)
  const modelRef = useRef<Monaco.editor.ITextModel | null>(null)
  const written = useRef<string | null>(null)
  const timer = useRef<number | undefined>(undefined)
  const cellBoxes = useRef(new Map<string, HTMLElement>())
  const cellEditors = useRef(new Map<string, Monaco.editor.IStandaloneCodeEditor>())
  const stopAll = useRef(false)
  const lastKey = useRef<{ key: string; at: number } | null>(null)
  useLayoutEffect(() => {
    edRef.current = ed
  })
  useFlushBeforeSave()

  /** The notebook into the file's model, as one edit (Undo of the file takes it back). */
  const flush = useCallback(() => {
    window.clearTimeout(timer.current)
    timer.current = undefined
    const model = modelRef.current
    const now = nbRef.current
    if (!model || model.isDisposed() || !now) return
    const text = serializeNotebook(now)
    if (text === model.getValue()) return
    written.current = text
    model.pushStackElement()
    model.pushEditOperations([], [{ range: model.getFullModelRange(), text }], () => null)
    model.pushStackElement()
  }, [])

  /** A change to the notebook: shown at once, written to the file at once or a moment after typing (`later`). */
  const change = useCallback(
    (f: (nb: Notebook) => Notebook, later = false) => {
      const now = nbRef.current
      if (!now) return
      const next = f(now)
      nbRef.current = next
      setNb(next)
      window.clearTimeout(timer.current)
      if (later) timer.current = window.setTimeout(flush, 250)
      else flush()
    },
    [flush],
  )

  useEffect(() => {
    let gone = false
    let listen: { dispose(): void } | null = null
    void (async () => {
      const [m, model] = await Promise.all([loadMonaco(), edRef.current.modelFor(path)])
      if (gone) return
      setMonaco(m)
      if (!model) {
        setNb({ error: `${nameOf(path)} is not text: it does not open as a notebook.` })
        return
      }
      modelRef.current = model
      const read = () => {
        const text = model.getValue()
        const parsed = parseNotebook(text)
        written.current = text
        if ('error' in parsed) {
          nbRef.current = null
          setNb(parsed)
          return
        }
        // The cells' ids stay where the cells are the same (a reload, an undo of the file): the selection stays on them.
        const before = nbRef.current
        if (before) parsed.cells = parsed.cells.map((c, i) => (before.cells[i] && before.cells[i]!.type === c.type ? { ...c, id: before.cells[i]!.id } : c))
        nbRef.current = parsed
        setNb(parsed)
        setRevision((r) => r + 1)
        setSelected((s) => s ?? parsed.cells[0]?.id ?? null)
      }
      read()
      // The file changed by anything else (its text editor, Undo, the disk): read again.
      listen = model.onDidChangeContent(() => {
        if (model.getValue() !== written.current) read()
      })
      pendingWrites.set(path, flush)
    })().catch((e: unknown) => !gone && setNb({ error: errorMessage(e) }))
    return () => {
      gone = true
      listen?.dispose()
      if (timer.current !== undefined) flush()
      pendingWrites.delete(path)
    }
  }, [path, flush])

  const notebook = nb && !('error' in nb) ? nb : null
  const language = notebook ? notebookLanguage(notebook) : 'python'

  const focusCell = (cellId: string | undefined, edit = false) => {
    if (!cellId) return
    setSelected(cellId)
    window.requestAnimationFrame(() => {
      const editor = cellEditors.current.get(cellId)
      if (edit && editor) editor.focus()
      else cellBoxes.current.get(cellId)?.focus()
      cellBoxes.current.get(cellId)?.scrollIntoView({ block: 'nearest' })
    })
  }

  const insert = (at: number, type: CellType, edit = true) => {
    const cell = newCell(type)
    change((n) => ({ ...n, cells: [...n.cells.slice(0, at), cell, ...n.cells.slice(at)] }))
    if (type === 'markdown') setEditing(cell.id)
    focusCell(cell.id, edit)
    return cell.id
  }
  const indexOf = (cellId: string) => nbRef.current?.cells.findIndex((c) => c.id === cellId) ?? -1
  const remove = (cellId: string) => {
    const at = indexOf(cellId)
    const cells = nbRef.current?.cells ?? []
    change((n) => ({ ...n, cells: n.cells.filter((c) => c.id !== cellId) }))
    focusCell((cells[at + 1] ?? cells[at - 1])?.id)
  }
  const move = (cellId: string, delta: number) => {
    const at = indexOf(cellId)
    const to = at + delta
    if (at < 0 || to < 0 || to >= (nbRef.current?.cells.length ?? 0)) return
    change((n) => {
      const cells = [...n.cells]
      const [cell] = cells.splice(at, 1)
      cells.splice(to, 0, cell!)
      return { ...n, cells }
    })
    focusCell(cellId)
  }
  const setType = (cellId: string, type: CellType) =>
    change((n) => withCell(n, cellId, (c) => (c.type === type ? c : { ...c, type, outputs: [], count: null })))

  /** A cell run in the kernel: its outputs as they come; false when it failed (Run All stops there). */
  const run = async (cellId: string): Promise<boolean> => {
    const cell = nbRef.current?.cells.find((c) => c.id === cellId)
    if (!cell) return true
    if (cell.type === 'markdown') {
      setEditing((e) => (e === cellId ? null : e))
      return true
    }
    if (cell.type !== 'code') return true
    setRuns((r) => ({ ...r, [cellId]: { state: 'running' } }))
    change((n) => withCell(n, cellId, (c) => ({ ...c, outputs: [] })), true)
    try {
      const done = await runCell(path, cell.source, (outputs, count) => change((n) => withCell(n, cellId, (c) => ({ ...c, outputs, count: count ?? c.count })), true))
      change((n) => withCell(n, cellId, (c) => ({ ...c, count: done.count ?? c.count })))
      setRuns((r) => ({ ...r, [cellId]: { state: done.ok ? 'ok' : 'failed', took: done.took } }))
      return done.ok
    } catch (e) {
      setRuns((r) => ({ ...r, [cellId]: { state: 'failed' } }))
      toast.error(errorMessage(e))
      return false
    } finally {
      void queryClient.invalidateQueries({ queryKey: kernelQuery(path).queryKey })
    }
  }

  /** Run All (or those from a cell down): one after another, stopping at the first that fails or at Interrupt. */
  const runMany = async (from = 0) => {
    const code = (nbRef.current?.cells ?? []).slice(from).filter((c) => c.type === 'code')
    stopAll.current = false
    setRuns((r) => ({ ...r, ...Object.fromEntries(code.map((c) => [c.id, { state: 'queued' } as RunState])) }))
    for (const [i, c] of code.entries()) {
      if (stopAll.current || !(await run(c.id))) {
        const left = code.slice(i + 1).map((x) => x.id)
        setRuns((r) => Object.fromEntries(Object.entries(r).filter(([k]) => !left.includes(k))))
        return
      }
    }
  }

  const interrupt = async () => {
    stopAll.current = true
    try {
      await interruptKernel(path)
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }
  const restart = async () => {
    if (!(await confirm({ title: 'Restart the kernel?', description: 'Its variables and imports are lost; the outputs stay in the notebook.', confirm: 'Restart' }))) return
    stopAll.current = true
    try {
      await restartKernel(path)
      setRuns({})
      await queryClient.invalidateQueries({ queryKey: kernelQuery(path).queryKey })
      toast.success('The kernel restarts with the next cell.')
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }

  /** A cell's keys in its editor (edit mode): run it, run and go on, run and add one below, leave the editor. */
  const editorKeys = (cellId: string) => (e: KeyboardEvent) => {
    const cell = nbRef.current?.cells.find((c) => c.id === cellId)
    if (!cell) return
    const suggesting = !!(e.currentTarget as HTMLElement).querySelector('.suggest-widget.visible')
    if (e.key === 'Escape' && !suggesting) {
      e.preventDefault()
      e.stopPropagation()
      if (cell.type === 'markdown') setEditing(null)
      focusCell(cellId)
      return
    }
    if (e.key !== 'Enter' || !(e.shiftKey || e.ctrlKey || e.metaKey || e.altKey) || suggesting) return
    e.preventDefault()
    e.stopPropagation()
    const at = indexOf(cellId)
    void run(cellId)
    if (e.altKey) insert(at + 1, 'code')
    else if (e.shiftKey) {
      const next = nbRef.current?.cells[at + 1]
      if (next) focusCell(next.id, next.type === 'code')
      else insert(at + 1, 'code')
    } else focusCell(cellId)
  }

  /** The notebook's keys on a cell (command mode), as VS Code's and Jupyter's. */
  const commandKeys = (cellId: string) => (e: KeyboardEvent<HTMLElement>) => {
    if (e.target !== e.currentTarget) return
    const cells = nbRef.current?.cells ?? []
    const at = indexOf(cellId)
    const cell = cells[at]
    if (!cell) return
    const key = e.key
    const plain = !e.ctrlKey && !e.metaKey && !e.altKey
    const twice = lastKey.current?.key === key && Date.now() - lastKey.current.at < 600
    lastKey.current = { key, at: Date.now() }
    let handled = true
    if (key === 'Enter' && (e.shiftKey || e.ctrlKey || e.metaKey || e.altKey)) {
      void run(cellId)
      if (e.altKey) insert(at + 1, 'code')
      else if (e.shiftKey) {
        if (cells[at + 1]) focusCell(cells[at + 1]!.id)
        else insert(at + 1, 'code')
      }
    } else if (key === 'Enter') {
      if (cell.type === 'markdown') setEditing(cellId)
      focusCell(cellId, true)
    } else if (plain && (key === 'ArrowUp' || key === 'k')) focusCell(cells[at - 1]?.id)
    else if (plain && (key === 'ArrowDown' || key === 'j')) focusCell(cells[at + 1]?.id)
    else if (plain && key === 'a') insert(at, 'code', false)
    else if (plain && key === 'b') insert(at + 1, 'code', false)
    else if (plain && key === 'd' && twice) remove(cellId)
    else if (plain && key === 'm') setType(cellId, 'markdown')
    else if (plain && key === 'y') setType(cellId, 'code')
    else handled = false
    if (handled) e.preventDefault()
  }

  const kernelLabel = kernel.data ? `${kernel.data.python.split(/[\\/]/).slice(-3).join('/')}${kernel.data.busy || busy ? ' · busy' : kernel.data.running ? ' · idle' : ''}` : ''
  if (nb === null || !monaco) return <Spinner label={`Opening ${nameOf(path)}`} />
  if ('error' in nb) {
    return (
      <div className="absolute inset-0 grid place-items-center bg-background p-6 text-center text-sm text-muted-foreground">
        <div className="max-w-md space-y-3">
          <p>{nb.error}</p>
          <Button size="sm" variant="outline" onClick={() => void ed.open(path, undefined, { as: 'text' })}>
            <FileCode2 aria-hidden="true" /> Open as text
          </Button>
        </div>
      </div>
    )
  }
  const cells = notebook!.cells
  return (
    <div className="absolute inset-0 flex flex-col bg-background" data-testid="notebook-editor">
      <div role="toolbar" aria-label="Notebook" className="flex shrink-0 flex-wrap items-center gap-1 border-b px-2 py-1 text-xs">
        <Button size="sm" variant="ghost" onClick={() => insert(cells.length, 'code')}>
          <Plus aria-hidden="true" /> Code
        </Button>
        <Button size="sm" variant="ghost" onClick={() => insert(cells.length, 'markdown')}>
          <Plus aria-hidden="true" /> Markdown
        </Button>
        <span className="mx-1 h-4 w-px bg-border" aria-hidden="true" />
        <Button size="sm" variant="ghost" onClick={() => void runMany()} disabled={busy}>
          <PlayCircle aria-hidden="true" /> Run All
        </Button>
        <Button size="sm" variant="ghost" onClick={() => change((n) => ({ ...n, cells: n.cells.map((c) => ({ ...c, outputs: [], count: null })) }))}>
          <Eraser aria-hidden="true" /> Clear All Outputs
        </Button>
        <Button size="sm" variant="ghost" onClick={() => void interrupt()} disabled={!busy}>
          <Square aria-hidden="true" /> Interrupt
        </Button>
        <Button size="sm" variant="ghost" onClick={() => void restart()}>
          <RotateCcw aria-hidden="true" /> Restart
        </Button>
        <span className="ml-auto flex min-w-0 items-center gap-2">
          <output className="min-w-0 truncate text-muted-foreground" title={kernel.data?.python}>
            {language === 'python' ? 'Python' : language} {kernelLabel && `· ${kernelLabel}`}
          </output>
          <Button size="sm" variant="ghost" onClick={() => void ed.open(path, undefined, { as: 'text' })} title="Reopen the notebook as its JSON text">
            <FileCode2 aria-hidden="true" /> Open as Text
          </Button>
        </span>
      </div>
      {kernel.data && !kernel.data.trusted && (
        <div className="flex shrink-0 flex-wrap items-center gap-2 border-b bg-muted/50 px-3 py-1.5 text-xs">
          <span>This folder is not trusted: its notebook&apos;s cells do not run until you trust it.</span>
          <Button size="sm" variant="outline" onClick={() => runCommand('workbench.trust.manage')}>
            Manage Workspace Trust
          </Button>
        </div>
      )}
      <ol aria-label={`Cells of ${nameOf(path)}`} className="min-h-0 flex-1 overflow-auto px-2 py-3 sm:px-6">
        {cells.map((cell, i) => (
          <li key={cell.id} className="group/cell">
            <CellView
              cell={cell}
              index={i}
              language={language}
              monaco={monaco}
              theme={theme}
              options={settings.all(language)}
              revision={revision}
              selected={selected === cell.id}
              editing={editing === cell.id}
              run={runs[cell.id]}
              bindBox={(el) => (el ? cellBoxes.current.set(cell.id, el) : cellBoxes.current.delete(cell.id))}
              bindEditor={(e) => (e ? cellEditors.current.set(cell.id, e) : cellEditors.current.delete(cell.id))}
              onSelect={() => setSelected(cell.id)}
              onSource={(source) => change((n) => withCell(n, cell.id, (c) => ({ ...c, source })), true)}
              onEditorKeys={editorKeys(cell.id)}
              onCommandKeys={commandKeys(cell.id)}
              onRun={() => void run(cell.id)}
              onEdit={() => {
                setEditing(cell.id)
                focusCell(cell.id, true)
              }}
              onClearOutputs={() => change((n) => withCell(n, cell.id, (c) => ({ ...c, outputs: [], count: null })))}
              onMove={(d) => move(cell.id, d)}
              onType={(t) => setType(cell.id, t)}
              onRemove={() => remove(cell.id)}
            />
            <div className="flex h-5 items-center justify-center gap-2 opacity-0 transition-opacity group-hover/cell:opacity-100 focus-within:opacity-100">
              <button type="button" onClick={() => insert(i + 1, 'code')} className="flex items-center gap-1 rounded border bg-background px-2 text-[0.6875rem] text-muted-foreground outline-none hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring">
                <Plus className="size-3" aria-hidden="true" /> Code
              </button>
              <button type="button" onClick={() => insert(i + 1, 'markdown')} className="flex items-center gap-1 rounded border bg-background px-2 text-[0.6875rem] text-muted-foreground outline-none hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring">
                <Plus className="size-3" aria-hidden="true" /> Markdown
              </button>
            </div>
          </li>
        ))}
        {cells.length === 0 && (
          <li className="py-8 text-center text-sm text-muted-foreground">
            The notebook has no cells: <button type="button" className="underline" onClick={() => insert(0, 'code')}>add a code cell</button>.
          </li>
        )}
      </ol>
    </div>
  )
}

interface CellProps {
  cell: Cell
  revision: number
  index: number
  language: string
  monaco: MonacoModule
  theme: string
  options: Record<string, unknown>
  selected: boolean
  editing: boolean
  run: RunState | undefined
  bindBox: (el: HTMLElement | null) => void
  bindEditor: (e: Monaco.editor.IStandaloneCodeEditor | null) => void
  onSelect: () => void
  onSource: (source: string) => void
  onEditorKeys: (e: KeyboardEvent) => void
  onCommandKeys: (e: KeyboardEvent<HTMLElement>) => void
  onRun: () => void
  onEdit: () => void
  onClearOutputs: () => void
  onMove: (delta: number) => void
  onType: (type: CellType) => void
  onRemove: () => void
}

function CellView({ revision, cell, index, language, monaco, theme, options, selected, editing, run, bindBox, bindEditor, onSelect, onSource, onEditorKeys, onCommandKeys, onRun, onEdit, onClearOutputs, onMove, onType, onRemove }: CellProps) {
  const code = cell.type === 'code'
  const label = `${code ? 'Code' : cell.type === 'markdown' ? 'Markdown' : 'Raw'} cell ${index + 1}`
  const tool = (name: string, icon: ReactNode, onClick: () => void, disabled = false) => (
    <Tooltip content={name}>
      <button type="button" aria-label={`${name} (${label})`} onClick={onClick} disabled={disabled} className="grid size-6 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-40 [&_svg]:size-3.5">
        {icon}
      </button>
    </Tooltip>
  )
  const showsEditor = code || cell.type === 'raw' || editing
  return (
    <section
      aria-label={label}
      data-testid="notebook-cell"
      onFocusCapture={onSelect}
      onPointerDown={onSelect}
      className={cn('relative rounded-md py-1 pr-1 pl-3', selected ? 'bg-accent/30' : 'hover:bg-accent/20')}
    >
      {/* The cell itself, for the notebook's keys (command mode): the bar along its left edge, as VS Code's focus indicator. */}
      <button
        ref={bindBox}
        type="button"
        tabIndex={selected ? 0 : -1}
        aria-label={`${label}${selected ? ', selected' : ''}`}
        aria-keyshortcuts="Enter Shift+Enter Control+Enter A B D M Y"
        onKeyDown={onCommandKeys}
        className={cn('absolute inset-y-0 left-0 w-1.5 rounded-l-md outline-none focus-visible:ring-2 focus-visible:ring-ring', selected ? 'bg-primary' : 'bg-transparent')}
      />
      <div className="flex items-start gap-2">
        <div className="flex w-12 shrink-0 flex-col items-center gap-0.5 pt-1.5 font-mono text-[0.6875rem] text-muted-foreground">
          {code && (
            <>
              <Tooltip content="Run cell (Ctrl+Enter)">
                <button type="button" aria-label={`Run ${label}`} onClick={onRun} disabled={run?.state === 'running' || run?.state === 'queued'} className="grid size-6 place-items-center rounded-sm outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring">
                  {run?.state === 'running' ? <Spinner className="size-3.5" label="Running" /> : <Play className="size-3.5" aria-hidden="true" />}
                </button>
              </Tooltip>
              <span aria-label={cell.count === null ? 'not run' : `run ${cell.count}`}>[{run?.state === 'running' ? '*' : run?.state === 'queued' ? '…' : (cell.count ?? ' ')}]</span>
            </>
          )}
        </div>
        <div className="min-w-0 flex-1">
          {showsEditor ? (
            <div onKeyDownCapture={onEditorKeys} className="overflow-hidden rounded border bg-background">
              <CellEditor
                source={cell.source}
                revision={revision}
                language={code ? language : cell.type === 'markdown' ? 'markdown' : 'plaintext'}
                monaco={monaco}
                theme={theme}
                options={options}
                label={label}
                bindEditor={bindEditor}
                onSource={onSource}
                autoFocus={editing}
              />
            </div>
          ) : (
            <div className="min-h-8 rounded px-2 py-1 text-sm" title="Double-click (or Enter) to edit" onDoubleClick={onEdit}>
              {cell.source.trim() ? <Markdown text={cell.source} /> : <span className="text-muted-foreground italic">Empty Markdown cell: double-click to edit.</span>}
            </div>
          )}
          {code && cell.outputs.length > 0 && <Outputs outputs={cell.outputs} label={label} />}
          {code && run && (run.state === 'ok' || run.state === 'failed') && (
            <p className={cn('mt-0.5 flex items-center gap-1 text-[0.6875rem]', run.state === 'ok' ? 'text-success-ink' : 'text-destructive-ink')}>
              {run.state === 'ok' ? <Check className="size-3" aria-hidden="true" /> : <X className="size-3" aria-hidden="true" />}
              {run.state === 'ok' ? 'ran' : 'failed'}
              {run.took !== undefined && ` in ${run.took < 1000 ? `${Math.round(run.took)} ms` : `${(run.took / 1000).toFixed(1)} s`}`}
            </p>
          )}
        </div>
        <div className={cn('flex shrink-0 flex-col gap-0.5 opacity-0 group-hover/cell:opacity-100 focus-within:opacity-100', selected && 'opacity-100')}>
          {code ? tool('Change to Markdown', <Type />, () => onType('markdown')) : tool('Change to Code', <Code2 />, () => onType('code'))}
          {code && tool('Clear Outputs', <Eraser />, onClearOutputs, cell.outputs.length === 0)}
          {tool('Move Cell Up', <ArrowUp />, () => onMove(-1))}
          {tool('Move Cell Down', <ArrowDown />, () => onMove(1))}
          {tool('Delete Cell', <Trash2 />, onRemove)}
        </div>
      </div>
    </section>
  )
}

/** A cell's editor: Monaco, as tall as its text, with the workbench's settings for its language. */
const CellEditor = memo(function CellEditor(p: {
  source: string
  /** The file's reads: its text is taken from `source` when this changes. */
  revision: number
  language: string
  monaco: MonacoModule
  theme: string
  options: Record<string, unknown>
  label: string
  bindEditor: (e: Monaco.editor.IStandaloneCodeEditor | null) => void
  onSource: (source: string) => void
  autoFocus: boolean
}) {
  const host = useRef<HTMLDivElement>(null)
  const editor = useRef<Monaco.editor.IStandaloneCodeEditor | null>(null)
  const latest = useRef(p)
  useLayoutEffect(() => {
    latest.current = p
  })
  useEffect(() => {
    const { monaco } = p.monaco
    const el = host.current
    if (!el) return
    const model = monaco.editor.createModel(latest.current.source, latest.current.language)
    const e = monaco.editor.create(el, {
      ...editorOptionsOf(latest.current.options),
      model,
      theme: latest.current.theme,
      automaticLayout: true,
      fixedOverflowWidgets: true,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      lineNumbers: 'off',
      folding: false,
      glyphMargin: false,
      lineDecorationsWidth: 6,
      overviewRulerLanes: 0,
      renderLineHighlight: 'none',
      scrollbar: { vertical: 'hidden', alwaysConsumeMouseWheel: false, handleMouseWheel: false },
      padding: { top: 6, bottom: 6 },
      ariaLabel: latest.current.label,
    })
    const size = () => {
      el.style.height = `${Math.max(32, Math.min(e.getContentHeight(), 4000))}px`
      e.layout()
    }
    size()
    const sized = e.onDidContentSizeChange(size)
    const changed = model.onDidChangeContent(() => latest.current.onSource(model.getValue()))
    editor.current = e
    latest.current.bindEditor(e)
    if (latest.current.autoFocus) e.focus()
    return () => {
      latest.current.bindEditor(null)
      sized.dispose()
      changed.dispose()
      e.dispose()
      model.dispose()
      editor.current = null
    }
  }, [p.monaco])
  // The file read again (changed outside): the editor takes the cell's text, when it is another.
  useEffect(() => {
    const model = editor.current?.getModel()
    if (model && model.getValue() !== latest.current.source) model.setValue(latest.current.source)
  }, [p.revision])
  useEffect(() => {
    const model = editor.current?.getModel()
    if (model && model.getLanguageId() !== p.language) p.monaco.monaco.editor.setModelLanguage(model, p.language)
  }, [p.language, p.monaco])
  return <div ref={host} className="monaco-component relative" style={{ height: 32 }} />
})

/** A cell's outputs, as Jupyter's renderers show them: each in its richest form the editor has. */
function Outputs({ outputs, label }: { outputs: Output[]; label: string }) {
  return (
    <div className="mt-1 space-y-1" data-testid="cell-outputs" title={`Outputs of ${label}`}>
      {outputs.map((o, i) => (
        <OutputView key={i} output={o} />
      ))}
    </div>
  )
}

const pre = 'max-h-96 overflow-auto rounded px-2 py-1 font-mono text-xs whitespace-pre-wrap break-words'

function OutputView({ output }: { output: Output }) {
  if (output.output_type === 'stream') {
    const err = output.name === 'stderr'
    return <pre className={cn(pre, err ? 'bg-destructive/10 text-foreground' : 'text-foreground')}>{stripAnsi(textOf(output.text))}</pre>
  }
  if (output.output_type === 'error') {
    const trace = Array.isArray(output.traceback) ? (output.traceback as string[]).join('') : ''
    return (
      <pre className={cn(pre, 'bg-destructive/10 text-foreground')} role="alert">
        <span className="font-semibold text-destructive-ink">
          {String(output.ename ?? 'Error')}: {String(output.evalue ?? '')}
        </span>
        {trace && '\n' + stripAnsi(trace)}
      </pre>
    )
  }
  const shown = richest(output.data)
  if (!shown) return null
  const text = typeof shown.value === 'string' ? shown.value : ''
  switch (shown.mime) {
    case 'image/png':
    case 'image/jpeg':
    case 'image/gif':
      return <img src={`data:${shown.mime};base64,${text.replace(/\s/g, '')}`} alt="Cell output" className="max-w-full rounded bg-white" />
    case 'image/svg+xml':
      // As a picture: an SVG shown so runs no script.
      return <img src={`data:image/svg+xml;charset=utf-8,${encodeURIComponent(text)}`} alt="Cell output (SVG)" className="max-w-full rounded bg-white" />
    case 'text/html':
      return <HtmlOutput html={text} />
    case 'text/markdown':
      return (
        <div className="px-2 text-sm">
          <Markdown text={text} />
        </div>
      )
    case 'text/latex':
      return (
        <div className="px-2 text-sm">
          <Markdown text={/^\s*\$/.test(text) ? text : `$$\n${text}\n$$`} />
        </div>
      )
    case 'application/json':
      return <pre className={pre}>{JSON.stringify(shown.value, null, 2)}</pre>
    default:
      return <pre className={pre}>{stripAnsi(text)}</pre>
  }
}

/**
 * An HTML output (a pandas table, a widget's static view) in a frame that runs no script and reaches nothing of the page:
 * as tall as it is (up to 600 px, then it scrolls), in the page's colours.
 */
function HtmlOutput({ html }: { html: string }) {
  const frame = useRef<HTMLIFrameElement>(null)
  const [height, setHeight] = useState(40)
  const style = typeof window === 'undefined' ? '' : (() => {
    const s = getComputedStyle(document.body)
    return `<style>:root{color-scheme:light dark}body{margin:4px;color:${s.color};background:transparent;font:13px ${s.fontFamily}}table{border-collapse:collapse}th,td{border:1px solid color-mix(in srgb, currentColor 25%, transparent);padding:2px 6px;text-align:right}a{color:inherit}</style>`
  })()
  return (
    <iframe
      ref={frame}
      title="HTML output"
      sandbox="allow-same-origin"
      srcDoc={style + html}
      onLoad={() => {
        const doc = frame.current?.contentDocument
        if (doc) setHeight(Math.min(600, Math.max(24, doc.documentElement.scrollHeight + 4)))
      }}
      style={{ height }}
      className="w-full rounded border-0"
    />
  )
}
