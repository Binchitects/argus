import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Code2, Download, FilePen, FileText, History, Maximize2, MessageSquareQuote, Minimize2, Plus, RotateCcw, Save, Scissors, Trash2, X } from 'lucide-react'
import { useEffect, useMemo, useRef, useState, type FormEvent, type KeyboardEvent } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { useConfirm } from '@/components/ui/confirm'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { EmptyState } from '@/components/ui/empty-state'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { ApiError, errorMessage } from '@/lib/api'
import { ago } from '@/lib/format'
import { cn } from '@/lib/utils'
import { saveBlob } from '@/lib/zip'
import {
  canvasesQuery,
  canvasQuery,
  createCanvas,
  deleteCanvas,
  exportUrl,
  restoreVersion,
  saveCanvas,
  versionQuery,
  versionsQuery,
  type Canvas,
  type CanvasExport,
  type CanvasSummary,
} from './canvas-api'
import { diffLines, diffRows, diffStats, type DiffRow } from './canvas-diff'
import { locate, selectionMessage, selectionOf, type CanvasSelection } from './canvas-selection'
import { markdownToHtml } from './export'
import { Markdown } from './markdown'

/** What the person typed and has not saved, per canvas: kept while they look at another one. */
interface Draft {
  content: string
  title: string
  base: number
  loaded: { content: string; title: string }
}
const drafts = new Map<string, Draft>()

/**
 * Like ChatGPT's canvas and Claude's artifacts, beside the chat: the chat's documents and
 * code, edited by the person and by the model. Each save and each change by the model is a
 * version; Versions shows what each changed and restores any of them. Text selected in a
 * canvas can be asked about, or made shorter, from here: it goes to the chat, quoted.
 */
export function CanvasPanel({
  chatId,
  selected,
  onSelect,
  onClose,
  wide,
  onWide,
  onSend,
  busy,
}: {
  chatId: string
  selected: string | null
  onSelect: (id: string | null) => void
  onClose: () => void
  wide?: boolean
  onWide?: (wide: boolean) => void
  /** Sends a message in the chat (a selection, quoted, with what to do with it). */
  onSend: (text: string) => Promise<boolean> | void
  /** An answer is being written: what goes to the chat waits until it is done. */
  busy: boolean
}) {
  const list = useQuery(canvasesQuery(chatId))
  const current = useQuery({ ...canvasQuery(selected ?? ''), enabled: !!selected })
  const [history, setHistory] = useState(false)
  const canvas = selected ? current.data : undefined
  const back = () => {
    setHistory(false)
    onSelect(null)
  }
  return (
    <aside aria-label="Canvas" className="flex h-full min-h-0 flex-col bg-card">
      <header className="flex h-12 shrink-0 items-center gap-2 border-b px-3">
        {selected ? (
          <Button variant="ghost" size="sm" className="-ml-1 min-w-0" onClick={back} aria-label="All canvases">
            <ArrowLeft /> <span className="truncate">{canvas?.title ?? 'Canvas'}</span>
          </Button>
        ) : (
          <h2 className="text-sm font-semibold">Canvas ({list.data?.length ?? 0})</h2>
        )}
        <span className="ml-auto flex shrink-0 items-center gap-1">
          {canvas && (
            <>
              <Tooltip content={history ? 'Back to the text' : 'Versions: what each change did, and restore'}>
                <Button variant={history ? 'secondary' : 'ghost'} size="icon-sm" onClick={() => setHistory(!history)} aria-pressed={history} aria-label="Versions">
                  <History />
                </Button>
              </Tooltip>
              <ExportMenu canvas={canvas} />
            </>
          )}
          {wide !== undefined && onWide && (
            <Tooltip content={wide ? 'Narrow the panel' : 'Widen the panel'}>
              <Button variant="ghost" size="icon-sm" onClick={() => onWide(!wide)} aria-label={wide ? 'Narrow the panel' : 'Widen the panel'} aria-pressed={wide}>
                {wide ? <Minimize2 /> : <Maximize2 />}
              </Button>
            </Tooltip>
          )}
          <Button variant="ghost" size="icon-sm" onClick={onClose} aria-label="Close canvas">
            <X />
          </Button>
        </span>
      </header>
      <div className="flex min-h-0 flex-1 flex-col">
        {!selected ? (
          <CanvasList chatId={chatId} canvases={list.data} loading={list.isPending} onSelect={onSelect} />
        ) : current.isPending ? (
          <div className="grid gap-2 p-3">
            <Skeleton className="h-8" />
            <Skeleton className="h-64" />
          </div>
        ) : current.error || !canvas ? (
          <p className="p-3 text-sm text-destructive-ink">{errorMessage(current.error, 'This canvas could not be opened.')}</p>
        ) : history ? (
          <CanvasHistory key={canvas.id} canvas={canvas} onRestored={() => setHistory(false)} />
        ) : (
          <CanvasEditor key={canvas.id} canvas={canvas} onSend={onSend} busy={busy} />
        )}
      </div>
    </aside>
  )
}

function CanvasList({ chatId, canvases, loading, onSelect }: { chatId: string; canvases?: CanvasSummary[]; loading: boolean; onSelect: (id: string) => void }) {
  const [making, setMaking] = useState<'document' | 'code' | null>(null)
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const remove = async (c: CanvasSummary) => {
    if (!(await confirm({ title: `Delete “${c.title}”?`, description: 'The canvas and all its versions go for good.', confirm: 'Delete', destructive: true }))) return
    await deleteCanvas(c.id).catch((e) => toast.error(errorMessage(e)))
    drafts.delete(c.id)
    await queryClient.invalidateQueries({ queryKey: canvasesQuery(chatId).queryKey })
  }
  return (
    <div className="grid content-start gap-3 overflow-y-auto p-3">
      <div className="flex flex-wrap gap-2">
        <Button size="sm" variant="outline" onClick={() => setMaking('document')}>
          <FileText /> New document
        </Button>
        <Button size="sm" variant="outline" onClick={() => setMaking('code')}>
          <Code2 /> New code
        </Button>
      </div>
      {making && <NewCanvas chatId={chatId} kind={making} onCancel={() => setMaking(null)} onMade={onSelect} />}
      {loading ? (
        <Skeleton className="h-14" />
      ) : !canvases?.length ? (
        !making && (
          <EmptyState icon={FilePen} title="No canvas yet" className="border-0">
            Ask for a document or code “in a canvas”, or start one here. You and the model both edit it, and every change is kept as a version.
          </EmptyState>
        )
      ) : (
        <ul className="grid gap-1">
          {canvases.map((c) => (
            <li key={c.id} className="group flex items-center gap-1">
              <button
                type="button"
                onClick={() => onSelect(c.id)}
                className="flex min-w-0 flex-1 items-center gap-3 rounded-lg px-2 py-2 text-left outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring"
              >
                <span className="flex size-8 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary-ink">
                  {c.kind === 'code' ? <Code2 className="size-4" /> : <FileText className="size-4" />}
                </span>
                <span className="grid min-w-0">
                  <span className="truncate text-sm font-medium">{c.title}</span>
                  <span className="truncate text-xs text-muted-foreground">
                    {[c.kind === 'code' ? (c.language ?? 'code') : 'Document', `version ${c.version}`, `${c.lines} line${c.lines === 1 ? '' : 's'}`, ago(c.updatedAt)].join(' · ')}
                  </span>
                </span>
              </button>
              <Button variant="ghost" size="icon-sm" className="shrink-0 opacity-60 group-hover:opacity-100" onClick={() => void remove(c)} aria-label={`Delete ${c.title}`}>
                <Trash2 />
              </Button>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

function NewCanvas({ chatId, kind, onCancel, onMade }: { chatId: string; kind: 'document' | 'code'; onCancel: () => void; onMade: (id: string) => void }) {
  const queryClient = useQueryClient()
  const [title, setTitle] = useState('')
  const [language, setLanguage] = useState('')
  const [making, setMaking] = useState(false)
  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setMaking(true)
    try {
      const made = await createCanvas(chatId, { title: title.trim() || (kind === 'code' ? 'Untitled code' : 'Untitled document'), kind, language: kind === 'code' ? language.trim() || null : null, content: '' })
      queryClient.setQueryData(canvasQuery(made.id).queryKey, made)
      await queryClient.invalidateQueries({ queryKey: canvasesQuery(chatId).queryKey })
      onMade(made.id)
    } catch (err) {
      toast.error(errorMessage(err))
    } finally {
      setMaking(false)
    }
  }
  return (
    <form onSubmit={submit} className="grid gap-2 rounded-lg border p-3" aria-label={kind === 'code' ? 'New code' : 'New document'}>
      <Input value={title} onChange={(e) => setTitle(e.target.value)} placeholder={kind === 'code' ? 'File name, e.g. rename_photos.py' : 'Title'} aria-label="Title" autoFocus maxLength={200} />
      {kind === 'code' && <Input value={language} onChange={(e) => setLanguage(e.target.value)} placeholder="Language, e.g. python" aria-label="Language" maxLength={40} />}
      <div className="flex justify-end gap-2">
        <Button type="button" variant="ghost" size="sm" onClick={onCancel}>
          Cancel
        </Button>
        <Button type="submit" size="sm" loading={making}>
          <Plus /> Create
        </Button>
      </div>
    </form>
  )
}

/**
 * The canvas's text, to edit: code with line numbers, a document with a preview beside
 * Edit. Save (or Ctrl+S) makes a version. A newer version from the model replaces the
 * text at once when nothing here is unsaved, and otherwise waits for the person's choice.
 */
function CanvasEditor({ canvas, onSend, busy }: { canvas: Canvas; onSend: (text: string) => Promise<boolean> | void; busy: boolean }) {
  const queryClient = useQueryClient()
  const kept = drafts.get(canvas.id)
  const [content, setContent] = useState(kept?.content ?? canvas.content)
  const [title, setTitle] = useState(kept?.title ?? canvas.title)
  const [language, setLanguage] = useState(canvas.language ?? '')
  /** The version the text here was started from, and that version's text. */
  const [base, setBase] = useState(kept?.base ?? canvas.version)
  const [loaded, setLoaded] = useState(kept?.loaded ?? { content: canvas.content, title: canvas.title })
  const [seen, setSeen] = useState(canvas.version)
  const [saving, setSaving] = useState(false)
  const [view, setView] = useState<'edit' | 'preview'>('edit')
  const [sel, setSel] = useState<CanvasSelection | null>(null)
  const [asking, setAsking] = useState(false)
  const [question, setQuestion] = useState('')
  const box = useRef<HTMLTextAreaElement>(null)
  const code = canvas.kind === 'code'
  const dirty = content !== loaded.content || title !== loaded.title || (code && language !== (canvas.language ?? ''))

  const take = (c: Canvas) => {
    setContent(c.content)
    setTitle(c.title)
    setLanguage(c.language ?? '')
    setBase(c.version)
    setLoaded({ content: c.content, title: c.title })
    setSel(null)
    drafts.delete(c.id)
  }
  // A newer version arrived (the model's change, a restore): taken at once when nothing here is unsaved.
  if (canvas.version !== seen) {
    setSeen(canvas.version)
    if (!dirty) take(canvas)
  }
  const behind = canvas.version !== base

  const keep = (next: Partial<Draft>) => drafts.set(canvas.id, { content, title, base, loaded, ...next })

  const save = async (over = base): Promise<Canvas | null> => {
    setSaving(true)
    try {
      const saved = await saveCanvas(canvas.id, { baseVersion: over, content, title, ...(code ? { language } : {}) })
      take(saved)
      setSeen(saved.version)
      queryClient.setQueryData(canvasQuery(canvas.id).queryKey, saved)
      void queryClient.invalidateQueries({ queryKey: canvasesQuery(canvas.conversationId).queryKey })
      void queryClient.invalidateQueries({ queryKey: versionsQuery(canvas.id).queryKey })
      return saved
    } catch (e) {
      // Changed by the model meanwhile: its version comes in, and the person chooses.
      if (e instanceof ApiError && e.http === 409) await queryClient.invalidateQueries({ queryKey: canvasQuery(canvas.id).queryKey })
      else toast.error(errorMessage(e, 'The canvas could not be saved.'))
      return null
    } finally {
      setSaving(false)
    }
  }

  const onKey = (e: KeyboardEvent) => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
      e.preventDefault()
      if (dirty && !behind) void save()
    }
  }

  const select = () => {
    const el = box.current
    if (el) setSel(selectionOf(content, el.selectionStart, el.selectionEnd))
  }
  // Text selected in the preview is found in the source, for its lines.
  const preview = useRef<HTMLDivElement>(null)
  const previewing = view === 'preview' && !code
  useEffect(() => {
    if (!previewing) return
    const changed = () => {
      const s = window.getSelection?.()
      if (!s || !preview.current?.contains(s.anchorNode)) return
      const text = s.toString()
      setSel(text.trim() ? locate(content, text) : null)
    }
    document.addEventListener('selectionchange', changed)
    return () => document.removeEventListener('selectionchange', changed)
  }, [previewing, content])

  /** Sends the selection to the chat, quoted with where it is; unsaved text is saved first, so the model reads what is shown. */
  const act = async (ask: string) => {
    if (!sel || !ask.trim()) return
    const now = dirty ? await save() : canvas
    if (!now) return
    const sent = await onSend(selectionMessage(now, sel, ask))
    if (sent !== false) {
      setSel(null)
      setAsking(false)
      setQuestion('')
    }
  }

  const lines = sel ? (sel.from === sel.to ? `Line ${sel.from}` : `Lines ${sel.from}–${sel.to}`) : ''
  return (
    <div className="flex min-h-0 flex-1 flex-col gap-2 p-3">
      <div className="flex flex-wrap items-center gap-2">
        <Input
          value={title}
          onChange={(e) => {
            setTitle(e.target.value)
            keep({ title: e.target.value })
          }}
          aria-label="Title"
          maxLength={200}
          className="h-8 min-w-40 flex-1 font-medium"
        />
        {code ? (
          <Input value={language} onChange={(e) => setLanguage(e.target.value)} aria-label="Language" placeholder="language" maxLength={40} className="h-8 w-28" />
        ) : (
          <Tabs value={view} onValueChange={(v) => setView(v as 'edit' | 'preview')}>
            <TabsList className="h-8">
              <TabsTrigger value="edit" className="px-2 text-xs">
                Edit
              </TabsTrigger>
              <TabsTrigger value="preview" className="px-2 text-xs">
                Preview
              </TabsTrigger>
            </TabsList>
          </Tabs>
        )}
        <Button size="sm" className="h-8" onClick={() => void save()} loading={saving} disabled={!dirty || behind} aria-keyshortcuts="Control+S">
          <Save /> Save
        </Button>
      </div>
      <p className="text-xs text-muted-foreground" aria-live="polite">
        {dirty ? 'Unsaved changes' : `Version ${canvas.version} · saved ${ago(canvas.updatedAt)}`}
      </p>
      {behind && dirty && (
        <Alert variant="warning" title="The model changed this canvas while you were editing it.">
          <p>Its version {canvas.version} is saved. Load it (your edits here go), or keep yours as the newest version (its version stays in Versions).</p>
          <div className="mt-2 flex flex-wrap gap-2">
            <Button size="sm" variant="outline" className="h-7" onClick={() => take(canvas)}>
              Load its version
            </Button>
            <Button size="sm" className="h-7" onClick={() => void save(canvas.version)} loading={saving}>
              Keep mine
            </Button>
          </div>
        </Alert>
      )}
      {previewing ? (
        <div ref={preview} className="min-h-0 flex-1 overflow-y-auto rounded-md border px-4 py-3">
          {content.trim() ? <Markdown text={content} /> : <p className="text-sm text-muted-foreground">Nothing written yet.</p>}
        </div>
      ) : (
        <LineEditor
          box={box}
          value={content}
          code={code}
          label={`${canvas.title}: text`}
          onChange={(v) => {
            setContent(v)
            keep({ content: v })
          }}
          onSelect={select}
          onKeyDown={onKey}
        />
      )}
      {sel && (
        <div role="toolbar" aria-label="Selection" className="flex flex-wrap items-center gap-2 rounded-lg border bg-muted/40 px-2 py-1.5 text-xs">
          <span className="text-muted-foreground">{lines} selected</span>
          <Button size="sm" variant="outline" className="h-7" onClick={() => setAsking(true)} disabled={busy}>
            <MessageSquareQuote /> Ask about this
          </Button>
          <Button size="sm" variant="outline" className="h-7" onClick={() => void act('Make this shorter.')} disabled={busy || saving}>
            <Scissors /> Make this shorter
          </Button>
          {busy && <span className="text-muted-foreground">Once the answer is done.</span>}
        </div>
      )}
      {sel && asking && (
        <form
          className="flex gap-2"
          onSubmit={(e) => {
            e.preventDefault()
            void act(question)
          }}
        >
          <Input value={question} onChange={(e) => setQuestion(e.target.value)} placeholder={`Ask about ${lines.toLowerCase()}…`} aria-label="Your question about the selection" autoFocus className="h-8" />
          <Button type="submit" size="sm" className="h-8" disabled={busy || saving || !question.trim()}>
            Send
          </Button>
        </form>
      )}
    </div>
  )
}

/** A plain text box: code with its line numbers beside it (they scroll with it), and no wrapping, so each number is its line. */
function LineEditor({
  box,
  value,
  code,
  label,
  onChange,
  onSelect,
  onKeyDown,
}: {
  box: React.RefObject<HTMLTextAreaElement | null>
  value: string
  code: boolean
  label: string
  onChange: (value: string) => void
  onSelect: () => void
  onKeyDown: (e: KeyboardEvent) => void
}) {
  const gutter = useRef<HTMLDivElement>(null)
  const count = Math.max(1, value.split('\n').length)
  return (
    <div className={cn('flex min-h-0 flex-1 overflow-hidden rounded-md border bg-background focus-within:border-primary focus-within:ring-[3px] focus-within:ring-ring', code ? 'font-mono text-[0.8125rem] leading-6' : 'text-sm leading-6')}>
      {code && (
        <div ref={gutter} aria-hidden="true" className="shrink-0 overflow-hidden border-r bg-muted/40 py-2 pr-2 pl-3 text-right text-muted-foreground tabular-nums select-none">
          {Array.from({ length: count }, (_, i) => (
            <div key={i}>{i + 1}</div>
          ))}
        </div>
      )}
      <textarea
        ref={box}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        onSelect={onSelect}
        onKeyDown={onKeyDown}
        onScroll={(e) => {
          if (gutter.current) gutter.current.scrollTop = e.currentTarget.scrollTop
        }}
        wrap={code ? 'off' : 'soft'}
        spellCheck={!code}
        dir={code ? 'ltr' : 'auto'}
        aria-label={label}
        className={cn('min-h-0 flex-1 resize-none bg-transparent px-3 py-2 outline-none', code && 'whitespace-pre')}
      />
    </div>
  )
}

/** Every version, newest first: who made it, when, its summary; chosen, what it changed (a line diff with the one before), and Restore. */
function CanvasHistory({ canvas, onRestored }: { canvas: Canvas; onRestored: () => void }) {
  const queryClient = useQueryClient()
  const versions = useQuery(versionsQuery(canvas.id))
  const [chosen, setChosen] = useState<number | null>(null)
  const number = chosen ?? versions.data?.[0]?.number ?? null
  const shown = useQuery({ ...versionQuery(canvas.id, number ?? 0), enabled: number !== null })
  const before = useQuery({ ...versionQuery(canvas.id, (number ?? 1) - 1), enabled: number !== null && number > 1 })
  const diff = useMemo(() => (shown.data && (number === 1 || before.data) ? diffLines(number === 1 ? '' : before.data!.content, shown.data.content) : null), [shown.data, before.data, number])
  const [restoring, setRestoring] = useState(false)
  const restore = async (n: number) => {
    setRestoring(true)
    try {
      const now = await restoreVersion(canvas.id, n)
      queryClient.setQueryData(canvasQuery(canvas.id).queryKey, now)
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: versionsQuery(canvas.id).queryKey }),
        queryClient.invalidateQueries({ queryKey: canvasesQuery(canvas.conversationId).queryKey }),
      ])
      toast.success(`Restored version ${n}`, { description: `It is version ${now.version} now; the others are still here.` })
      onRestored()
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setRestoring(false)
    }
  }
  if (versions.isPending) return <Skeleton className="m-3 h-40" />
  if (versions.error) return <p className="p-3 text-sm text-destructive-ink">{errorMessage(versions.error)}</p>
  const info = versions.data.find((v) => v.number === number)
  const stats = diff ? diffStats(diff) : null
  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <ol aria-label="Versions" className="max-h-56 shrink-0 overflow-y-auto border-b p-2">
        {versions.data.map((v) => (
          <li key={v.number}>
            <button
              type="button"
              onClick={() => setChosen(v.number)}
              aria-current={v.number === number ? 'true' : undefined}
              className={cn('flex w-full items-baseline gap-2 rounded-md px-2 py-1.5 text-left text-sm outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring', v.number === number && 'bg-accent')}
            >
              <span className="shrink-0 font-medium tabular-nums">v{v.number}</span>
              <span className="min-w-0 flex-1 truncate">{v.summary}</span>
              <span className="shrink-0 text-xs text-muted-foreground">
                {v.author === 'model' ? 'The model' : 'You'} · {ago(v.createdAt)}
              </span>
            </button>
          </li>
        ))}
      </ol>
      <div className="grid min-h-0 flex-1 content-start gap-2 overflow-y-auto p-3">
        {info && (
          <div className="flex flex-wrap items-center gap-2">
            <p className="min-w-0 flex-1 text-sm">
              <span className="font-medium">Version {info.number}</span> by {info.author === 'model' ? 'the model' : 'you'}
              {stats && (
                <span className="ms-2 text-xs text-muted-foreground tabular-nums">
                  +{stats.added} −{stats.removed}
                </span>
              )}
            </p>
            <Button size="sm" className="h-8" onClick={() => void restore(info.number)} loading={restoring} disabled={info.number === canvas.version}>
              <RotateCcw /> {info.number === canvas.version ? 'The current version' : 'Restore this version'}
            </Button>
          </div>
        )}
        {shown.isPending || (number !== null && number > 1 && before.isPending) ? <Skeleton className="h-40" /> : diff ? <DiffView rows={diffRows(diff)} /> : null}
      </div>
    </div>
  )
}

/** A line diff: removed lines red with −, added green with +, a few unchanged around each change. */
function DiffView({ rows }: { rows: DiffRow[] }) {
  if (!rows.some((r) => r.kind === 'added' || r.kind === 'removed')) return <p className="text-sm text-muted-foreground">The text did not change (only the title).</p>
  return (
    <div className="overflow-x-auto rounded-md border font-mono text-xs leading-5">
      <table className="w-full border-collapse" aria-label="Changes">
        <tbody>
          {rows.map((r, i) =>
            r.kind === 'skip' ? (
              <tr key={i} className="bg-muted/40 text-muted-foreground">
                <td colSpan={4} className="px-3 py-0.5 font-sans">
                  {r.count} unchanged line{r.count === 1 ? '' : 's'}
                </td>
              </tr>
            ) : (
              <tr key={i} data-kind={r.kind} className={cn(r.kind === 'added' && 'bg-success/10', r.kind === 'removed' && 'bg-destructive/10')}>
                <td className="w-8 px-1.5 text-right text-muted-foreground select-none">{r.old ?? ''}</td>
                <td className="w-8 px-1.5 text-right text-muted-foreground select-none">{r.new ?? ''}</td>
                <td className={cn('w-4 select-none', r.kind === 'added' ? 'text-success' : 'text-destructive-ink')}>
                  {r.kind === 'added' ? '+' : r.kind === 'removed' ? '−' : ''}
                  <span className="sr-only">{r.kind === 'added' ? 'Added' : r.kind === 'removed' ? 'Removed' : ''}</span>
                </td>
                <td className="pr-3 break-words whitespace-pre-wrap">{r.text || ' '}</td>
              </tr>
            ),
          )}
        </tbody>
      </table>
    </div>
  )
}

/** The file name a download carries (Content-Disposition), UTF-8 name first. */
function fileNameOf(header: string | null, fallback: string) {
  const star = header?.match(/filename\*=UTF-8''([^;]+)/i)?.[1]
  if (star) return decodeURIComponent(star)
  return header?.match(/filename="?([^";]+)"?/i)?.[1] ?? fallback
}

/** The saved canvas as a file: a document as Markdown, Word or PDF; code as its file. */
function ExportMenu({ canvas }: { canvas: Canvas }) {
  const download = async (format: CanvasExport) => {
    try {
      const res = await fetch(exportUrl(canvas.id, format), { credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } })
      if (format === 'pdf' && res.status === 503) return printed(canvas)
      if (!res.ok) {
        const d = (await res.json().catch(() => ({}))) as { status?: string; error?: string }
        throw new ApiError(res.status, d.status ?? String(res.status), d.error ?? `The export failed (HTTP ${res.status}).`)
      }
      saveBlob(await res.blob(), fileNameOf(res.headers.get('Content-Disposition'), `${canvas.title}.${format}`))
    } catch (e) {
      toast.error(errorMessage(e, 'The canvas could not be exported.'))
    }
  }
  return (
    <DropdownMenu>
      <Tooltip content="Export">
        <DropdownMenuTrigger asChild>
          <Button variant="ghost" size="icon-sm" aria-label="Export">
            <Download />
          </Button>
        </DropdownMenuTrigger>
      </Tooltip>
      <DropdownMenuContent align="end">
        <DropdownMenuLabel className="text-xs font-normal text-muted-foreground">The saved version</DropdownMenuLabel>
        {canvas.kind === 'code' ? (
          <DropdownMenuItem onSelect={() => void download('file')}>Download the file</DropdownMenuItem>
        ) : (
          <>
            <DropdownMenuItem onSelect={() => void download('md')}>Markdown (.md)</DropdownMenuItem>
            <DropdownMenuItem onSelect={() => void download('docx')}>Word (.docx)</DropdownMenuItem>
            <DropdownMenuItem onSelect={() => void download('pdf')}>PDF (.pdf)</DropdownMenuItem>
          </>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

/** Without the sandbox, the browser makes the PDF: the document as a page of its own, printed (Save as PDF). */
function printed(canvas: Canvas) {
  const url = URL.createObjectURL(new Blob([markdownToHtml(canvas.title, canvas.content)], { type: 'text/html' }))
  const page = window.open(url, '_blank')
  if (!page) {
    toast.error('The browser blocked the new tab: allow pop-ups for this site, or export as Word.')
    return
  }
  toast.info('The sandbox is not running, so your browser makes the PDF: choose Save as PDF.')
  page.addEventListener('load', () => page.print(), { once: true })
  setTimeout(() => URL.revokeObjectURL(url), 60_000)
}
