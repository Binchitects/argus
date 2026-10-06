import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronRight, ChevronsDownUp, Copy, FilePlus, Folder, FolderOpen, FolderPlus, GitCompareArrows, Pencil, RefreshCw, Trash2 } from 'lucide-react'
import { ContextMenu } from 'radix-ui'
import { useState, type KeyboardEvent, type ReactNode } from 'react'
import { useConfirm } from '@/components/ui/confirm'
import { menuContent, menuItem } from '@/components/ui/dropdown-menu'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { useEditor } from './editor-state'
import { FileIcon } from './file-icon'
import { PartHelp } from './help'
import { changesQuery, createEntry, deleteEntry, folderQuery, join, nameOf, parentOf, renameEntry, within, type Change, type Entry } from './ide-api'

/** A row's place in the tree, for keys and the context menu. */
interface Target {
  path: string
  kind: 'file' | 'dir'
}

/** What a tree row shows: the agent's change to it, or that one is under it. */
interface Marks {
  files: Map<string, Change>
  folders: Set<string>
}

/** The changed files, and every folder above one. */
function marksOf(changes: Change[] | undefined): Marks {
  const files = new Map<string, Change>()
  const folders = new Set<string>()
  for (const c of changes ?? []) {
    if (c.deleted) continue
    files.set(c.path, c)
    for (let p = parentOf(c.path); p; p = parentOf(p)) folders.add(p)
  }
  return { files, folders }
}

/** A row being made (a new file or folder) or renamed: its name typed in place. */
type Editing = { kind: 'new'; parent: string; type: 'file' | 'dir' } | { kind: 'rename'; path: string }

/**
 * The working directory's files: folders open on demand, a file opens in the
 * editor. New file, new folder, rename (F2) and delete (Delete) from the
 * toolbar, the keys or the context menu; the files the agent changed are
 * marked, as an editor marks a repository's changes.
 */
export function Explorer({ project, onOpenChanges }: { project: string; onOpenChanges: (path: string) => void }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const editor = useEditor()
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set())
  const [selected, setSelected] = useState<Target | null>(null)
  const [editing, setEditing] = useState<Editing | null>(null)
  const [menuFor, setMenuFor] = useState<Target | null>(null)
  const root = useQuery({ ...folderQuery(''), refetchOnWindowFocus: true })
  const changes = useQuery(changesQuery)
  const marks = marksOf(changes.data)

  const toggle = (path: string, open?: boolean) =>
    setExpanded((s) => {
      const next = new Set(s)
      if (open ?? !next.has(path)) next.add(path)
      else next.delete(path)
      return next
    })

  /** The folder new entries go in: the selected folder, the selected file's, or the working directory. */
  const folderFor = (t: Target | null) => (!t ? '' : t.kind === 'dir' ? t.path : parentOf(t.path))
  const startNew = (type: 'file' | 'dir', at: Target | null = selected) => {
    const parent = folderFor(at)
    if (parent) toggle(parent, true)
    setEditing({ kind: 'new', parent, type })
  }
  const reload = () => queryClient.invalidateQueries({ queryKey: ['code', 'files'] })

  const finish = async (name: string) => {
    const was = editing
    setEditing(null)
    const clean = name.trim().replace(/^\/+|\/+$/g, '')
    if (!was || !clean) return
    try {
      if (was.kind === 'new') {
        const made = await createEntry(join(was.parent, clean), was.type)
        await reload()
        setSelected({ path: made.path, kind: made.kind })
        if (made.kind === 'file') void editor.open(made.path)
      } else {
        const to = join(parentOf(was.path), clean)
        if (to === was.path) return
        const moved = await renameEntry(was.path, to)
        editor.moved(moved.from, moved.to)
        setExpanded((s) => new Set([...s].map((p) => (within(p, moved.from) ? moved.to + p.slice(moved.from.length) : p))))
        setSelected((t) => (t && within(t.path, moved.from) ? { ...t, path: moved.to + t.path.slice(moved.from.length) } : t))
        await Promise.all([reload(), queryClient.invalidateQueries({ queryKey: changesQuery.queryKey })])
      }
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }

  const remove = async (t: Target) => {
    const name = nameOf(t.path)
    // Their tabs stay open (the editor keeps what is typed): said here, so the delete is not taken to lose it.
    const unsaved = editor.tabs.filter((tab) => tab.kind === 'file' && tab.dirty && within(tab.path, t.path)).map((tab) => nameOf(tab.path))
    const kept =
      unsaved.length === 0 ? '' : unsaved.length === 1 ? ` ${unsaved[0]} has unsaved changes: its tab stays open, and saving it makes the file again.` : ` ${unsaved.join(', ')} have unsaved changes: their tabs stay open, and saving one makes the file again.`
    const ok = await confirm({
      title: `Delete ${name}?`,
      description: (t.kind === 'dir' ? `The folder ${t.path} and everything in it are deleted from the disk. This cannot be undone.` : `${t.path} is deleted from the disk. This cannot be undone.`) + kept,
      confirm: 'Delete',
      destructive: true,
    })
    if (!ok) return
    try {
      await deleteEntry(t.path)
      editor.removed(t.path)
      setSelected(null)
      await Promise.all([reload(), queryClient.invalidateQueries({ queryKey: changesQuery.queryKey })])
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }

  const activate = (t: Target) => {
    setSelected(t)
    if (t.kind === 'dir') toggle(t.path)
    else void editor.open(t.path)
  }

  /** A row's keys, as in a tree: arrows move and open, Enter opens, F2 renames, Delete deletes. */
  const onKey = (e: KeyboardEvent<HTMLElement>) => {
    const row = e.currentTarget
    if (e.target !== row) return
    const t: Target = { path: row.dataset.path!, kind: row.dataset.kind as 'file' | 'dir' }
    const rows = [...(row.closest('[role=tree]')?.querySelectorAll<HTMLElement>('[role=treeitem]') ?? [])]
    const at = rows.indexOf(row)
    const focus = (el: HTMLElement | undefined) => {
      if (!el) return
      el.focus()
      setSelected({ path: el.dataset.path!, kind: el.dataset.kind as 'file' | 'dir' })
    }
    const open = row.getAttribute('aria-expanded') === 'true'
    const keys: Record<string, () => void> = {
      ArrowDown: () => focus(rows[at + 1]),
      ArrowUp: () => focus(rows[at - 1]),
      Home: () => focus(rows[0]),
      End: () => focus(rows.at(-1)),
      ArrowRight: () => (t.kind === 'dir' && !open ? toggle(t.path, true) : t.kind === 'dir' ? focus(rows[at + 1]) : undefined),
      ArrowLeft: () => (open ? toggle(t.path, false) : focus(rows.find((r) => r.dataset.path === parentOf(t.path)))),
      Enter: () => activate(t),
      ' ': () => activate(t),
      F2: () => setEditing({ kind: 'rename', path: t.path }),
      Delete: () => void remove(t),
    }
    const run = keys[e.key]
    if (run) {
      e.preventDefault()
      run()
    }
  }

  const rows = { expanded, selected, editing, marks, onActivate: activate, onSelect: setSelected, onFinish: (name: string) => void finish(name), onMenu: setMenuFor, onKey }
  const first = root.data?.entries[0]
  const tools: [string, ReactNode, () => void][] = [
    ['New file', <FilePlus key="f" />, () => startNew('file')],
    ['New folder', <FolderPlus key="d" />, () => startNew('dir')],
    ['Refresh', <RefreshCw key="r" />, () => void reload()],
    ['Collapse folders', <ChevronsDownUp key="c" />, () => setExpanded(new Set())],
  ]
  // Right-clicked on a row, or on the space below them (the working directory).
  const at = menuFor ?? { path: '', kind: 'dir' as const }
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex h-9 shrink-0 items-center gap-1 pr-1.5 pl-3">
        <h2 className="min-w-0 flex-1 truncate text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase" title={project}>
          {project}
        </h2>
        {tools.map(([label, icon, run]) => (
          <Tooltip key={label} content={label}>
            <button type="button" aria-label={label} onClick={run} className="grid size-6 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring [&_svg]:size-3.5">
              {icon}
            </button>
          </Tooltip>
        ))}
        <PartHelp part="explorer" />
      </div>
      <ContextMenu.Root onOpenChange={(open) => !open && setMenuFor(null)}>
        <ContextMenu.Trigger asChild>
          <div className="min-h-0 flex-1 overflow-auto pb-4">
            {root.isPending && Array.from({ length: 6 }, (_, i) => <Skeleton key={i} className="mx-3 my-1.5 h-4" />)}
            {root.error && <p className="px-3 py-2 text-sm text-muted-foreground">{errorMessage(root.error)}</p>}
            {root.data && (
              <ul role="tree" aria-label="Files" className="text-[0.8125rem]">
                {editing?.kind === 'new' && editing.parent === '' && <NameInput level={0} type={editing.type} onFinish={rows.onFinish} />}
                {root.data.entries.map((e) => (
                  <Row key={e.path} entry={e} level={0} first={e === first} {...rows} />
                ))}
                {root.data.entries.length === 0 && editing === null && <li className="px-3 py-2 text-muted-foreground">The folder is empty.</li>}
              </ul>
            )}
          </div>
        </ContextMenu.Trigger>
        <ContextMenu.Portal>
          <ContextMenu.Content className={menuContent}>
            <ContextMenu.Item className={menuItem} onSelect={() => startNew('file', at)}>
              <FilePlus /> New file
            </ContextMenu.Item>
            <ContextMenu.Item className={menuItem} onSelect={() => startNew('dir', at)}>
              <FolderPlus /> New folder
            </ContextMenu.Item>
            {at.path && (
              <>
                <ContextMenu.Separator className="-mx-1 my-1 h-px bg-border" />
                {at.kind === 'file' && marks.files.has(at.path) && (
                  <ContextMenu.Item className={menuItem} onSelect={() => onOpenChanges(at.path)}>
                    <GitCompareArrows /> The agent&apos;s changes
                  </ContextMenu.Item>
                )}
                <ContextMenu.Item className={menuItem} onSelect={() => void navigator.clipboard?.writeText(at.path).catch(() => undefined)}>
                  <Copy /> Copy path
                </ContextMenu.Item>
                <ContextMenu.Item className={menuItem} onSelect={() => setEditing({ kind: 'rename', path: at.path })}>
                  <Pencil /> Rename <span className="ml-auto text-xs text-muted-foreground">F2</span>
                </ContextMenu.Item>
                <ContextMenu.Item className={cn(menuItem, 'text-destructive-ink focus:bg-destructive/10 focus:text-destructive-ink [&_svg]:text-destructive-ink')} onSelect={() => void remove(at)}>
                  <Trash2 /> Delete <span className="ml-auto text-xs">Del</span>
                </ContextMenu.Item>
              </>
            )}
          </ContextMenu.Content>
        </ContextMenu.Portal>
      </ContextMenu.Root>
    </div>
  )
}

interface RowProps {
  expanded: Set<string>
  selected: Target | null
  editing: Editing | null
  marks: Marks
  onActivate: (t: Target) => void
  onSelect: (t: Target) => void
  onFinish: (name: string) => void
  onMenu: (t: Target) => void
  onKey: (e: KeyboardEvent<HTMLElement>) => void
}

const indent = (level: number) => ({ paddingLeft: `${0.5 + level * 0.75}rem` })

function Row({ entry, level, first, ...props }: RowProps & { entry: Entry; level: number; first?: boolean }) {
  const { expanded, selected, editing, marks, onActivate, onSelect, onFinish, onMenu, onKey } = props
  const dir = entry.kind === 'dir'
  const open = dir && expanded.has(entry.path)
  const target: Target = { path: entry.path, kind: entry.kind }
  const isSelected = selected?.path === entry.path
  const change = marks.files.get(entry.path)
  const changedUnder = dir && marks.folders.has(entry.path)
  if (editing?.kind === 'rename' && editing.path === entry.path) {
    return <NameInput level={level} type={entry.kind} initial={entry.name} onFinish={onFinish} />
  }
  return (
    <li role="none">
      <div
        role="treeitem"
        aria-level={level + 1}
        aria-expanded={dir ? open : undefined}
        aria-selected={isSelected}
        tabIndex={isSelected || (!selected && first) ? 0 : -1}
        aria-label={entry.name + (change ? (change.created ? ', made by the agent' : ', changed by the agent') : '') + (changedUnder && !open ? ', the agent changed files in it' : '')}
        data-path={entry.path}
        data-kind={entry.kind}
        title={change ? `${entry.path} · ${change.created ? 'made' : 'changed'} by the agent` : entry.path}
        onClick={() => onActivate(target)}
        onKeyDown={onKey}
        onFocus={(e) => e.target === e.currentTarget && !isSelected && onSelect(target)}
        onContextMenu={() => {
          onSelect(target)
          onMenu(target)
        }}
        style={indent(level)}
        className={cn(
          'flex h-[1.375rem] cursor-pointer items-center gap-1 pr-2 outline-none select-none hover:bg-accent/70 focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-inset',
          isSelected && 'bg-accent text-accent-foreground',
          change && (change.created ? 'text-success-ink' : 'text-warning-ink'),
        )}
      >
        {dir ? (
          <>
            <ChevronRight className={cn('size-3.5 shrink-0 text-muted-foreground transition-transform duration-150', open && 'rotate-90')} aria-hidden="true" />
            {open ? <FolderOpen className="size-4 shrink-0 text-primary/80" aria-hidden="true" /> : <Folder className="size-4 shrink-0 text-primary/80" aria-hidden="true" />}
          </>
        ) : (
          <>
            <span className="w-3.5 shrink-0" />
            <FileIcon path={entry.path} />
          </>
        )}
        <span className="min-w-0 flex-1 truncate">{entry.name}</span>
        {change && (
          <span className="shrink-0 font-mono text-[0.6875rem] font-semibold" aria-hidden="true">
            {change.created ? 'A' : 'M'}
          </span>
        )}
        {changedUnder && !open && <span className="size-1.5 shrink-0 rounded-full bg-warning" aria-hidden="true" />}
      </div>
      {open && <Children path={entry.path} level={level + 1} {...props} />}
    </li>
  )
}

/** An open folder's entries, listed when it opens. */
function Children({ path, level, ...props }: RowProps & { path: string; level: number }) {
  const list = useQuery({ ...folderQuery(path), refetchOnWindowFocus: true })
  const editing = props.editing
  return (
    // oxlint-disable-next-line jsx-a11y/prefer-tag-over-role -- a tree's open folder holds its rows in a group (the ARIA tree pattern)
    <ul role="group">
      {editing?.kind === 'new' && editing.parent === path && <NameInput level={level} type={editing.type} onFinish={props.onFinish} />}
      {list.isPending && (
        <li role="none" style={indent(level)} className="py-1">
          <Skeleton className="h-3.5 w-32" />
        </li>
      )}
      {list.error && (
        <li role="none" style={indent(level)} className="py-0.5 text-muted-foreground">
          {errorMessage(list.error)}
        </li>
      )}
      {list.data?.entries.map((e) => (
        <Row key={e.path} entry={e} level={level} {...props} />
      ))}
      {list.data?.entries.length === 0 && !(editing?.kind === 'new' && editing.parent === path) && (
        <li role="none" style={indent(level + 1)} className="py-0.5 text-muted-foreground italic">
          empty
        </li>
      )}
    </ul>
  )
}

/** A name typed in the tree: Enter makes or renames, Escape or leaving it cancels. */
function NameInput({ level, type, initial = '', onFinish }: { level: number; type: 'file' | 'dir'; initial?: string; onFinish: (name: string) => void }) {
  const [name, setName] = useState(initial)
  const [done, setDone] = useState(false)
  const end = (value: string) => {
    if (done) return
    setDone(true)
    onFinish(value)
  }
  return (
    <li role="none" style={indent(level)} className="flex h-[1.375rem] items-center gap-1 pr-2">
      <span className="w-3.5 shrink-0" />
      {type === 'dir' ? <Folder className="size-4 shrink-0 text-primary/80" aria-hidden="true" /> : <FileIcon path={name} />}
      <input
        autoFocus
        value={name}
        onChange={(e) => setName(e.target.value)}
        onFocus={(e) => {
          // The name without its extension is selected, as an editor does.
          const dot = initial.lastIndexOf('.')
          e.target.setSelectionRange(0, dot > 0 && type === 'file' ? dot : initial.length)
        }}
        onKeyDown={(e) => {
          e.stopPropagation()
          if (e.key === 'Enter') end(name)
          if (e.key === 'Escape') end('')
        }}
        onBlur={() => end(name)}
        aria-label={initial ? `New name for ${initial}` : type === 'dir' ? 'Name of the new folder' : 'Name of the new file'}
        className="h-5 min-w-0 flex-1 rounded-sm border border-primary bg-card px-1 text-[0.8125rem] outline-none"
      />
    </li>
  )
}
