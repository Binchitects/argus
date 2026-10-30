import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronRight, ChevronsDownUp, ClipboardPaste, Columns2, Copy, FilePlus, FileSearch, FolderInput, FolderOpen, FolderPlus, GitCompareArrows, Link, MessageSquarePlus, Pencil, RefreshCw, Scissors, SquareDashedMousePointer, SquareTerminal, Trash2, X } from 'lucide-react'
import { ContextMenu } from 'radix-ui'
import { useEffect, useId, useRef, useState, type DragEvent, type KeyboardEvent, type MouseEvent, type ReactNode } from 'react'
import { useConfirm } from '@/components/ui/confirm'
import { menuContent, menuItem } from '@/components/ui/dropdown-menu'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { onMac, useEditor } from './editor-state'
import { useIconTheme } from './extensions'
import { FileIcon, FolderIcon } from './file-icon'
import { PartHelp } from './help'
import { changesQuery, copyEntry, createEntry, deleteEntry, folderQuery, gitStatusQuery, join, nameOf, parentOf, renameEntry, uploadFile, within, type Change, type Entry, type GitFile } from './ide-api'
import { ApiError } from '@/lib/api'
import { takeReveal, useReveal } from './explorer-reveal'
import { copyName } from './copy-name'
import { useProblems } from './problems-store'
import { useSettings } from './settings'
import { wholePath } from './whole-path'
import { chatBridge } from './bridge'
import { runCommand, useCommands } from './commands'
import { useKeyFor } from './keyboard'
import { compareSelection } from './compare'
import { foldersQuery, removeFolder } from './folders'
import { Outline } from './outline'

/** A row's place in the tree, for keys and the context menu. */
interface Target {
  path: string
  kind: 'file' | 'dir'
}

/** A file's change as git has it, as VS Code's Explorer marks it: modified, untracked, added, renamed. */
type GitMark = 'M' | 'U' | 'A' | 'R'

/**
 * What a tree row shows: the agent's change to it, or that one is under it; git's change, and what git ignores; its
 * problems' errors and warnings, and those under a folder. Colours and letters as the settings have them.
 */
interface Marks {
  files: Map<string, Change>
  folders: Set<string>
  git: Map<string, GitMark>
  gitFolders: Set<string>
  ignored: Set<string>
  errors: Map<string, number>
  warnings: Map<string, number>
  errorFolders: Set<string>
  warningFolders: Set<string>
  colors: boolean
  badges: boolean
}

const above = (path: string, into: Set<string>) => {
  for (let p = parentOf(path); p; p = parentOf(p)) into.add(p)
}

const gitMarkOf = (f: GitFile): GitMark | null =>
  f.changed === '?' ? 'U' : f.staged === 'R' ? 'R' : f.staged === 'A' && !f.changed ? 'A' : f.changed === 'D' || (f.staged === 'D' && !f.changed) ? null : 'M'

/** The changed files, and every folder above one. */
function marksOf(
  changes: Change[] | undefined,
  git: { files?: GitFile[]; ignored?: string[] } | undefined,
  problems: { path: string; severity: string }[],
  settings: { colors: boolean; badges: boolean; git: boolean; problems: boolean },
): Marks {
  const m: Marks = {
    files: new Map(), folders: new Set(), git: new Map(), gitFolders: new Set(), ignored: new Set(), errors: new Map(), warnings: new Map(),
    errorFolders: new Set(), warningFolders: new Set(), colors: settings.colors, badges: settings.badges,
  }
  for (const c of changes ?? []) {
    if (c.deleted) continue
    m.files.set(c.path, c)
    above(c.path, m.folders)
  }
  if (settings.git) {
    for (const f of git?.files ?? []) {
      const mark = gitMarkOf(f)
      if (!mark) continue
      m.git.set(f.path, mark)
      above(f.path, m.gitFolders)
    }
    for (const p of git?.ignored ?? []) m.ignored.add(p)
  }
  if (settings.problems) {
    for (const p of problems) {
      if (p.severity === 'error') {
        m.errors.set(p.path, (m.errors.get(p.path) ?? 0) + 1)
        above(p.path, m.errorFolders)
      } else if (p.severity === 'warning') {
        m.warnings.set(p.path, (m.warnings.get(p.path) ?? 0) + 1)
        above(p.path, m.warningFolders)
      }
    }
  }
  return m
}

/** Whether git ignores this path: it, or a folder above it. */
const ignoredIn = (marks: Marks, path: string) => {
  if (marks.ignored.size === 0) return false
  for (let p = path; p; p = parentOf(p)) if (marks.ignored.has(p)) return true
  return false
}

const gitWords: Record<GitMark, string> = { M: 'modified', U: 'untracked', A: 'added', R: 'renamed' }

/** A row being made (a new file or folder) or renamed: its name typed in place. */
type Editing = { kind: 'new'; parent: string; type: 'file' | 'dir' } | { kind: 'rename'; path: string }

/** The Explorer's own clipboard (Copy, Cut): the page's, as VS Code's is the window's. */
const clipboard: { items: Target[]; cut: boolean } = { items: [], cut: false }
const hold = (items: Target[], cut: boolean) => Object.assign(clipboard, { items, cut })

/** What a drag between rows carries: the rows dragged. */
const dragType = 'application/x-code-arena-entries'


/**
 * The working directory's files: folders open on demand, a file opens in the
 * editor. New file, new folder, rename (F2) and delete (Delete) from the
 * toolbar, the keys or the context menu; the files the agent changed are
 * marked, as an editor marks a repository's changes.
 */
export function Explorer({
  project,
  folder,
  onOpenChanges,
  onFindIn,
  onTerminalIn,
}: {
  project: string
  /** The folder's full path, for Copy Path. */
  folder: string
  onOpenChanges: (path: string) => void
  /** Find in Folder: Search, with the folder as the files to include. */
  onFindIn: (folder: string) => void
  /** Open in Integrated Terminal: a shell in that folder. */
  onTerminalIn: (folder: string) => void
}) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const editor = useEditor()
  const settings = useSettings()
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set())
  const [selected, setSelected] = useState<Target | null>(null)
  // Several rows selected at once (Ctrl+click, Shift+click, Ctrl+A); empty: the one selected alone.
  const [marked, setMarked] = useState<ReadonlyMap<string, 'file' | 'dir'>>(new Map())
  const anchor = useRef<string | null>(null)
  const treeRef = useRef<HTMLDivElement>(null)
  const [dropAt, setDropAt] = useState<string | null>(null)
  const [editing, setEditing] = useState<Editing | null>(null)
  const [menuFor, setMenuFor] = useState<Target | null>(null)
  const root = useQuery({ ...folderQuery(''), refetchOnWindowFocus: true })
  const changes = useQuery(changesQuery)
  const git = useQuery(gitStatusQuery)
  const problems = useProblems()
  const on = (key: string) => settings.get(key) !== false
  const marks = marksOf(changes.data, git.data, problems, {
    colors: on('explorer.decorations.colors'), badges: on('explorer.decorations.badges'), git: on('git.decorations.enabled'), problems: on('problems.decorations.enabled'),
  })

  // Reveal: the file the editor shows (explorer.autoReveal), or one asked for (Reveal in Explorer View): its folders
  // opened, it selected, and the row scrolled to once it is drawn (its folders' entries may still be on their way).
  const [scrollTo, setScrollTo] = useState<string | null>(null)
  const reveal = (path: string, scroll: boolean) => {
    const folders = new Set<string>()
    above(path, folders)
    setExpanded((s) => (folders.size === 0 || [...folders].every((f) => s.has(f)) ? s : new Set([...s, ...folders])))
    setSelected({ path, kind: 'file' })
    if (scroll) setScrollTo(path)
  }
  const autoReveal = settings.get('explorer.autoReveal')
  const shown = editor.active && (editor.active.kind === 'file' || editor.active.kind === 'preview') ? editor.active.path : null
  const [followed, setFollowed] = useState<string | null>(null)
  if (shown !== followed) {
    setFollowed(shown)
    if (shown && autoReveal !== false && autoReveal !== 'false') reveal(shown, autoReveal !== 'focusNoScroll')
  }
  const asked = useReveal()
  useEffect(() => {
    if (asked && takeReveal(asked)) reveal(asked.path, asked.scroll)
  }, [asked])

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

  /** Deletes these (the rows selected), after one question. */
  const remove = async (targets: Target[]) => {
    if (targets.length === 0) return
    const t = targets[0]!
    // Their tabs stay open (the editor keeps what is typed): said here, so the delete is not taken to lose it.
    const unsaved = editor.tabs.filter((tab) => tab.kind === 'file' && tab.dirty && targets.some((x) => within(tab.path, x.path))).map((tab) => nameOf(tab.path))
    const kept =
      unsaved.length === 0 ? '' : unsaved.length === 1 ? ` ${unsaved[0]} has unsaved changes: its tab stays open, and saving it makes the file again.` : ` ${unsaved.join(', ')} have unsaved changes: their tabs stay open, and saving one makes the file again.`
    const ok = await confirm({
      title: targets.length === 1 ? `Delete ${nameOf(t.path)}?` : `Delete ${targets.length} items?`,
      description:
        (targets.length > 1
          ? `${targets.map((x) => x.path).join(', ')} are deleted from the disk, the folders with everything in them. This cannot be undone.`
          : t.kind === 'dir'
            ? `The folder ${t.path} and everything in it are deleted from the disk. This cannot be undone.`
            : `${t.path} is deleted from the disk. This cannot be undone.`) + kept,
      confirm: 'Delete',
      destructive: true,
    })
    if (!ok) return
    try {
      for (const x of targets) {
        await deleteEntry(x.path)
        editor.removed(x.path)
      }
      setSelected(null)
      setMarked(new Map())
      await Promise.all([reload(), queryClient.invalidateQueries({ queryKey: changesQuery.queryKey })])
    } catch (e) {
      toast.error(errorMessage(e))
      await reload()
    }
  }

  /** The rows of the folder's own tree, as drawn (open folders' entries in place). */
  const drawnRows = () => [...(treeRef.current?.querySelectorAll<HTMLElement>('[role=tree][aria-label="Files"] [role=treeitem]') ?? [])]
  const targetOf = (row: HTMLElement): Target => ({ path: row.dataset.path!, kind: row.dataset.kind as 'file' | 'dir' })
  /** The rows the menu, Delete, Copy and Cut act on: the ones selected when this one is among them, else this one alone. */
  const actOn = (t: Target | null): Target[] =>
    !t ? [] : marked.size > 1 && marked.has(t.path) ? [...marked].map(([path, kind]) => ({ path, kind })) : [t]

  /** A row clicked: alone (it opens), with Ctrl (Cmd) added to the selection or taken out, with Shift the rows from the last one clicked. */
  const click = (t: Target, e: MouseEvent) => {
    if (e.ctrlKey || e.metaKey) {
      const next = new Map(marked.size > 0 ? marked : selected ? [[selected.path, selected.kind] as const] : [])
      if (next.has(t.path)) next.delete(t.path)
      else next.set(t.path, t.kind)
      setMarked(next)
      setSelected(t)
      anchor.current = t.path
      return
    }
    if (e.shiftKey && anchor.current) {
      const rows = drawnRows()
      const from = rows.findIndex((r) => r.dataset.path === anchor.current)
      const to = rows.findIndex((r) => r.dataset.path === t.path)
      if (from >= 0 && to >= 0) {
        const [a, b] = from < to ? [from, to] : [to, from]
        setMarked(new Map(rows.slice(a, b + 1).map((r) => [r.dataset.path!, r.dataset.kind as 'file' | 'dir'])))
        setSelected(t)
        return
      }
    }
    setMarked(new Map())
    anchor.current = t.path
    activate(t)
  }
  /** The focus on a row by the keys: it alone is selected. */
  const focusOn = (t: Target) => {
    setSelected(t)
    setMarked(new Map())
    anchor.current = t.path
  }

  /** Copied or cut rows put in a folder: a copy beside its original gets a name of its own; a cut moves (its tabs follow). */
  const paste = async (into: string, items = clipboard.items, cut = clipboard.cut) => {
    if (items.length === 0) return
    try {
      const listing = await queryClient.fetchQuery(folderQuery(into))
      const taken = new Set(listing.entries.map((e) => e.name))
      for (const item of items) {
        const name = nameOf(item.path)
        if (cut) {
          const to = join(into, name)
          if (to === item.path) continue
          if (item.kind === 'dir' && within(into, item.path)) throw new Error(`${item.path} cannot move into itself.`)
          await move(item.path, to)
        } else {
          const copy = taken.has(name) ? copyName(name, taken, item.kind === 'dir') : name
          taken.add(copy)
          await copyEntry(item.path, join(into, copy))
        }
      }
      if (cut && items === clipboard.items) hold([], false)
      if (into) toggle(into, true)
    } catch (e) {
      toast.error(errorMessage(e))
    }
    await Promise.all([reload(), queryClient.invalidateQueries({ queryKey: changesQuery.queryKey })])
  }

  /** A file or folder moved (a cut pasted, a drag): its tabs, open folders and selection follow it. */
  const move = async (from: string, to: string) => {
    const moved = await renameEntry(from, to)
    editor.moved(moved.from, moved.to)
    setExpanded((s) => new Set([...s].map((p) => (within(p, moved.from) ? moved.to + p.slice(moved.from.length) : p))))
    setSelected((t) => (t && within(t.path, moved.from) ? { ...t, path: moved.to + t.path.slice(moved.from.length) } : t))
  }

  /** Where a drop goes: a folder dropped on, a file's folder, or the folder's root. */
  const dropFolder = (t: Target | null) => (t ? folderFor(t) : '')
  const copying = (e: DragEvent) => (onMac() ? e.altKey : e.ctrlKey)
  const dragStart = (t: Target, e: DragEvent) => {
    e.dataTransfer.setData(dragType, JSON.stringify(actOn(t)))
    e.dataTransfer.effectAllowed = 'copyMove'
  }
  const dragOver = (t: Target | null, e: DragEvent) => {
    const types = [...e.dataTransfer.types]
    if (!types.includes(dragType) && !types.includes('Files')) return
    e.preventDefault()
    e.stopPropagation()
    e.dataTransfer.dropEffect = types.includes('Files') || copying(e) ? 'copy' : 'move'
    setDropAt(dropFolder(t))
  }
  const drop = async (t: Target | null, e: DragEvent) => {
    const types = [...e.dataTransfer.types]
    if (!types.includes(dragType) && !types.includes('Files')) return
    e.preventDefault()
    e.stopPropagation()
    setDropAt(null)
    const into = dropFolder(t)
    if (types.includes(dragType)) {
      const items = (JSON.parse(e.dataTransfer.getData(dragType) || '[]') as Target[]).filter((x) => parentOf(x.path) !== into || copying(e))
      if (items.length === 0) return
      if (!copying(e) && settings.get('explorer.confirmDragAndDrop') !== false) {
        const ok = await confirm({
          title: items.length === 1 ? `Move ${nameOf(items[0]!.path)} into ${into || project}?` : `Move ${items.length} items into ${into || project}?`,
          description: 'Their open tabs follow them. Turn this question off with "explorer.confirmDragAndDrop".',
          confirm: 'Move',
        })
        if (!ok) return
      }
      await paste(into, items, !copying(e))
      return
    }
    // Files from the computer: written into the folder (one there already: asked first).
    for (const file of [...e.dataTransfer.files]) {
      const path = join(into, file.name)
      try {
        await uploadFile(path, file)
      } catch (err) {
        if (!(err instanceof ApiError) || err.http !== 409) {
          toast.error(errorMessage(err))
          continue
        }
        if (await confirm({ title: `Replace ${path}?`, description: 'A file of that name is there already: the one dropped takes its place.', confirm: 'Replace', destructive: true })) {
          await uploadFile(path, file, true).catch((x: unknown) => toast.error(errorMessage(x)))
        }
      }
    }
    if (into) toggle(into, true)
    await reload()
  }

  /** A folder opens or closes; a file opens as a preview (a single click), or for good (a double click). */
  const activate = (t: Target, preview = true) => {
    setSelected(t)
    if (t.kind === 'dir') toggle(t.path)
    else void editor.open(t.path, undefined, { preview })
  }

  /**
   * The row with the focus (else the one selected): renameFile and deleteFile act on it. Their keys (F2, Delete) are the
   * keymap's, so keybindings.json can change or take them away.
   */
  const focusedTarget = (): Target | null => {
    const row = document.activeElement instanceof HTMLElement ? document.activeElement.closest<HTMLElement>('[data-context="explorer"] [role=treeitem]') : null
    return row ? { path: row.dataset.path!, kind: row.dataset.kind as 'file' | 'dir' } : selected
  }
  useCommands([
    { id: 'renameFile', category: 'Explorer', title: 'Rename', hidden: true, enabled: () => !!focusedTarget(), run: () => {
      const t = focusedTarget()
      if (t) setEditing({ kind: 'rename', path: t.path })
    } },
    { id: 'deleteFile', category: 'Explorer', title: 'Delete', hidden: true, enabled: () => !!focusedTarget(), run: () => void remove(actOn(focusedTarget())) },
    { id: 'filesExplorer.copy', category: 'Explorer', title: 'Copy', hidden: true, enabled: () => !!focusedTarget(), run: () => hold(actOn(focusedTarget()), false) },
    { id: 'filesExplorer.cut', category: 'Explorer', title: 'Cut', hidden: true, enabled: () => !!focusedTarget(), run: () => hold(actOn(focusedTarget()), true) },
    { id: 'filesExplorer.paste', category: 'Explorer', title: 'Paste', hidden: true, enabled: () => clipboard.items.length > 0, run: () => void paste(folderFor(focusedTarget())) },
    { id: 'list.selectAll', category: 'Explorer', title: 'Select All', hidden: true, enabled: () => drawnRows().length > 0, run: () => {
      setMarked(new Map(drawnRows().map((r) => [r.dataset.path!, r.dataset.kind as 'file' | 'dir'])))
    } },
  ])
  const renameKey = useKeyFor('renameFile')
  const deleteKey = useKeyFor('deleteFile')

  /** A row's keys, as in a tree: arrows move and open, Enter opens (F2 and Delete are the keymap's: renameFile, deleteFile). */
  const onKey = (e: KeyboardEvent<HTMLElement>) => {
    const row = e.currentTarget
    if (e.target !== row) return
    const t: Target = { path: row.dataset.path!, kind: row.dataset.kind as 'file' | 'dir' }
    const rows = [...(row.closest('[role=tree]')?.querySelectorAll<HTMLElement>('[role=treeitem]') ?? [])]
    const at = rows.indexOf(row)
    const focus = (el: HTMLElement | undefined) => {
      if (!el) return
      el.focus()
      focusOn(targetOf(el))
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
    }
    const run = keys[e.key]
    if (run) {
      e.preventDefault()
      run()
    }
  }

  const rows: RowProps = {
    expanded, selected, marked, editing, marks, onActivate: activate, onClick: click, onSelect: focusOn, onFinish: (name: string) => void finish(name), onMenu: setMenuFor, onKey,
    scrollTo, onScrolled: () => setScrollTo(null), dropAt, onDragStart: dragStart, onDragOver: dragOver, onDrop: (t, e) => void drop(t, e), onDragEnd: () => setDropAt(null),
  }
  // The folders added to the workspace (Add Folder), each a root below this folder's files.
  const folders = useQuery(foldersQuery)
  const added = folders.data?.added ?? []
  const takeOut = async (folder: string) => {
    try {
      await removeFolder(folder)
      await queryClient.invalidateQueries({ queryKey: foldersQuery.queryKey })
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }
  const first = root.data?.entries[0]
  const tools: [string, ReactNode, () => void][] = [
    ['New file', <FilePlus key="f" />, () => startNew('file')],
    ['New folder', <FolderPlus key="d" />, () => startNew('dir')],
    ['Refresh', <RefreshCw key="r" />, () => void reload()],
    ['Collapse folders', <ChevronsDownUp key="c" />, () => setExpanded(new Set())],
    ['Open folder…', <FolderOpen key="o" />, () => runCommand('workbench.action.files.openFolder')],
    ['Add folder to workspace…', <FolderInput key="a" />, () => runCommand('workbench.action.addRootFolder')],
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
          <div
            ref={treeRef}
            onDragOver={(e) => dragOver(null, e)}
            onDragLeave={(e) => e.currentTarget === e.target && setDropAt(null)}
            onDrop={(e) => void drop(null, e)}
            className={cn('min-h-0 flex-1 overflow-auto pb-4', dropAt === '' && 'bg-accent/40')}
          >
            {root.isPending && Array.from({ length: 6 }, (_, i) => <Skeleton key={i} className="mx-3 my-1.5 h-4" />)}
            {root.error && <p className="px-3 py-2 text-sm text-muted-foreground">{errorMessage(root.error)}</p>}
            {root.data && (
              <ul role="tree" aria-label="Files" aria-multiselectable="true" data-context="explorer" className="text-[0.8125rem]">
                {editing?.kind === 'new' && editing.parent === '' && <NameInput level={0} type={editing.type} onFinish={rows.onFinish} />}
                {root.data.entries.map((e) => (
                  <Row key={e.path} entry={e} level={0} first={e === first} {...rows} />
                ))}
                {root.data.entries.length === 0 && editing === null && <li className="px-3 py-2 text-muted-foreground">The folder is empty.</li>}
              </ul>
            )}
            {added.map((folder) => (
              <AddedFolder key={folder} folder={folder} rows={rows} onRemove={() => void takeOut(folder)} />
            ))}
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
            {clipboard.items.length > 0 && (
              <ContextMenu.Item className={menuItem} onSelect={() => void paste(folderFor(at.path ? at : null))}>
                <ClipboardPaste /> Paste
              </ContextMenu.Item>
            )}
            {at.path && (
              <>
                <ContextMenu.Separator className="-mx-1 my-1 h-px bg-border" />
                {at.kind === 'file' && marks.files.has(at.path) && (
                  <ContextMenu.Item className={menuItem} onSelect={() => onOpenChanges(at.path)}>
                    <GitCompareArrows /> The agent&apos;s changes
                  </ContextMenu.Item>
                )}
                <ContextMenu.Item className={menuItem} onSelect={() => chatBridge.current?.mention(at.path)}>
                  <MessageSquarePlus /> Add to chat
                </ContextMenu.Item>
                {at.kind === 'file' && actOn(at).length === 2 && actOn(at).every((x) => x.kind === 'file') && (
                  <ContextMenu.Item className={menuItem} onSelect={() => void editor.compare({ kind: 'file', path: actOn(at)[0]!.path }, { kind: 'file', path: actOn(at)[1]!.path })}>
                    <Columns2 /> Compare Selected
                  </ContextMenu.Item>
                )}
                {at.kind === 'file' && actOn(at).length === 1 && (
                  <>
                    <ContextMenu.Separator className="-mx-1 my-1 h-px bg-border" />
                    <ContextMenu.Item className={menuItem} onSelect={() => (compareSelection.path = at.path)}>
                      <SquareDashedMousePointer /> Select for Compare
                    </ContextMenu.Item>
                    {compareSelection.path && compareSelection.path !== at.path && (
                      <ContextMenu.Item className={menuItem} onSelect={() => void editor.compare({ kind: 'file', path: compareSelection.path! }, { kind: 'file', path: at.path })}>
                        <Columns2 /> Compare with Selected
                      </ContextMenu.Item>
                    )}
                  </>
                )}
                <ContextMenu.Separator className="-mx-1 my-1 h-px bg-border" />
                <ContextMenu.Item className={menuItem} onSelect={() => hold(actOn(at), true)}>
                  <Scissors /> Cut
                </ContextMenu.Item>
                <ContextMenu.Item className={menuItem} onSelect={() => hold(actOn(at), false)}>
                  <Copy /> Copy
                </ContextMenu.Item>
                <ContextMenu.Separator className="-mx-1 my-1 h-px bg-border" />
                <ContextMenu.Item className={menuItem} onSelect={() => void navigator.clipboard?.writeText(wholePath(folder, at.path)).catch(() => undefined)}>
                  <Copy /> Copy Path
                </ContextMenu.Item>
                <ContextMenu.Item className={menuItem} onSelect={() => void navigator.clipboard?.writeText(at.path).catch(() => undefined)}>
                  <Copy /> Copy Relative Path
                </ContextMenu.Item>
                <ContextMenu.Separator className="-mx-1 my-1 h-px bg-border" />
                <ContextMenu.Item className={menuItem} onSelect={() => onFindIn(folderFor(at))}>
                  <FileSearch /> Find in Folder…
                </ContextMenu.Item>
                <ContextMenu.Item className={menuItem} onSelect={() => onTerminalIn(folderFor(at))}>
                  <SquareTerminal /> Open in Integrated Terminal
                </ContextMenu.Item>
                <ContextMenu.Separator className="-mx-1 my-1 h-px bg-border" />
                <ContextMenu.Item className={menuItem} onSelect={() => setEditing({ kind: 'rename', path: at.path })}>
                  <Pencil /> Rename {renameKey && <span className="ml-auto text-xs text-muted-foreground">{renameKey}</span>}
                </ContextMenu.Item>
                <ContextMenu.Item className={cn(menuItem, 'text-destructive-ink focus:bg-destructive/10 focus:text-destructive-ink [&_svg]:text-destructive-ink')} onSelect={() => void remove(actOn(at))}>
                  <Trash2 /> Delete {deleteKey && <span className="ml-auto text-xs">{deleteKey}</span>}
                </ContextMenu.Item>
              </>
            )}
          </ContextMenu.Content>
        </ContextMenu.Portal>
      </ContextMenu.Root>
      <Outline />
    </div>
  )
}

/** A folder added to the workspace (Add Folder): its name as a root, its files below it as the folder's are, × takes it out. */
function AddedFolder({ folder, rows, onRemove }: { folder: string; rows: RowProps; onRemove: () => void }) {
  const [open, setOpen] = useState(true)
  const list = useQuery({ ...folderQuery(folder), enabled: open })
  const name = folder.split('/').filter(Boolean).at(-1) ?? folder
  return (
    <section aria-label={`Added folder ${name}`} className="mt-2">
      <div className="flex h-6 items-center gap-1 pr-1.5 pl-1">
        <button
          type="button"
          aria-expanded={open}
          title={folder}
          onClick={() => setOpen(!open)}
          className="flex min-w-0 flex-1 items-center gap-1 rounded-sm text-left text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          <ChevronRight className={cn('size-3.5 shrink-0 transition-transform', open && 'rotate-90')} aria-hidden="true" />
          <span className="truncate">{name}</span>
        </button>
        <Tooltip content="Remove folder from workspace">
          <button type="button" aria-label={`Remove ${name} from the workspace`} onClick={onRemove} className="grid size-6 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring [&_svg]:size-3.5">
            <X />
          </button>
        </Tooltip>
      </div>
      {open && list.isPending && <Skeleton className="mx-3 my-1.5 h-4" />}
      {open && list.error && <p className="px-3 py-1 text-sm text-muted-foreground">{errorMessage(list.error)}</p>}
      {open && list.data && (
        <ul role="tree" aria-label={`Files of ${name}`} data-context="explorer" className="text-[0.8125rem]">
          {list.data.entries.map((e) => (
            <Row key={e.path} entry={e} level={0} {...rows} />
          ))}
          {list.data.entries.length === 0 && <li className="px-3 py-1 text-muted-foreground">The folder is empty.</li>}
        </ul>
      )}
    </section>
  )
}

interface RowProps {
  expanded: Set<string>
  selected: Target | null
  /** The rows selected together; empty: the one selected alone. */
  marked: ReadonlyMap<string, 'file' | 'dir'>
  editing: Editing | null
  marks: Marks
  onActivate: (t: Target, preview?: boolean) => void
  onClick: (t: Target, e: MouseEvent) => void
  onSelect: (t: Target) => void
  onFinish: (name: string) => void
  onMenu: (t: Target) => void
  onKey: (e: KeyboardEvent<HTMLElement>) => void
  /** The row to scroll to as it is drawn (a file revealed), and that it was. */
  scrollTo: string | null
  onScrolled: () => void
  /** The folder a drag is over ('' the root), and the drag's steps. */
  dropAt: string | null
  onDragStart: (t: Target, e: DragEvent) => void
  onDragOver: (t: Target, e: DragEvent) => void
  onDrop: (t: Target, e: DragEvent) => void
  onDragEnd: () => void
}

const indent = (level: number) => ({ paddingLeft: `${0.5 + level * 0.75}rem` })

function Row({ entry, level, first, ...props }: RowProps & { entry: Entry; level: number; first?: boolean }) {
  const { expanded, selected, marked, editing, marks, onActivate, onClick, onSelect, onFinish, onMenu, onKey, scrollTo, onScrolled, dropAt, onDragStart, onDragOver, onDrop, onDragEnd } = props
  const dir = entry.kind === 'dir'
  const open = dir && expanded.has(entry.path)
  const target: Target = { path: entry.path, kind: entry.kind }
  const isSelected = marked.size > 0 ? marked.has(entry.path) : selected?.path === entry.path
  // The focus a click gives is the click's to select by (with Ctrl or Shift, among others): not the focus's.
  const pointed = useRef(false)
  const change = marks.files.get(entry.path)
  const changedUnder = dir && marks.folders.has(entry.path)
  const icons = useIconTheme()
  const described = useId()
  const rowRef = useRef<HTMLDivElement>(null)
  // Revealed: scrolled to as it is drawn (its folder's entries may have come after the reveal).
  const scrollHere = scrollTo === entry.path
  useEffect(() => {
    if (!scrollHere) return
    rowRef.current?.scrollIntoView?.({ block: 'nearest' })
    onScrolled()
  }, [scrollHere, onScrolled])
  const gitMark = dir ? undefined : marks.git.get(entry.path)
  const errors = dir ? 0 : (marks.errors.get(entry.path) ?? 0)
  const warnings = dir ? 0 : (marks.warnings.get(entry.path) ?? 0)
  const errorsUnder = dir && marks.errorFolders.has(entry.path)
  const warningsUnder = dir && marks.warningFolders.has(entry.path)
  const ignored = ignoredIn(marks, entry.path)
  // What the colours and letters say, for a screen reader: git's change, the problems, ignored, a link.
  const says = [
    gitMark && gitWords[gitMark],
    errors > 0 && `${errors} ${errors === 1 ? 'error' : 'errors'}`,
    warnings > 0 && `${warnings} ${warnings === 1 ? 'warning' : 'warnings'}`,
    errorsUnder && 'errors in it',
    !errorsUnder && warningsUnder && 'warnings in it',
    dir && marks.gitFolders.has(entry.path) && 'git changes in it',
    ignored && 'ignored by git',
    entry.link && 'a link',
  ].filter((x): x is string => !!x)
  const color = !marks.colors
    ? undefined
    : errors > 0 || errorsUnder
      ? 'text-destructive-ink'
      : warnings > 0 || warningsUnder
        ? 'text-warning-ink'
        : change
          ? change.created ? 'text-success-ink' : 'text-warning-ink'
          : gitMark
            ? gitMark === 'M' || gitMark === 'R' ? 'text-warning-ink' : 'text-success-ink'
            : dir && marks.gitFolders.has(entry.path) ? 'text-warning-ink' : undefined
  if (editing?.kind === 'rename' && editing.path === entry.path) {
    return <NameInput level={level} type={entry.kind} initial={entry.name} onFinish={onFinish} />
  }
  return (
    <li role="none">
      <div
        ref={rowRef}
        role="treeitem"
        aria-level={level + 1}
        aria-expanded={dir ? open : undefined}
        aria-selected={isSelected}
        tabIndex={selected?.path === entry.path || (!selected && first) ? 0 : -1}
        aria-label={entry.name + (change ? (change.created ? ', made by the agent' : ', changed by the agent') : '') + (changedUnder && !open ? ', the agent changed files in it' : '')}
        data-path={entry.path}
        data-kind={entry.kind}
        aria-describedby={says.length > 0 ? described : undefined}
        title={[entry.path, change && `${change.created ? 'made' : 'changed'} by the agent`, ...says].filter(Boolean).join(' · ')}
        draggable={editing === null}
        onPointerDown={() => (pointed.current = true)}
        onClick={(e) => {
          pointed.current = false
          onClick(target, e)
        }}
        onDoubleClick={() => target.kind === 'file' && onActivate(target, false)}
        onKeyDown={onKey}
        onFocus={(e) => {
          if (pointed.current || e.target !== e.currentTarget || selected?.path === entry.path) return
          onSelect(target)
        }}
        onContextMenu={() => {
          pointed.current = false
          if (!isSelected) onSelect(target)
          onMenu(target)
        }}
        onDragStart={(e) => onDragStart(target, e)}
        onDragOver={(e) => onDragOver(target, e)}
        onDrop={(e) => onDrop(target, e)}
        onDragEnd={onDragEnd}
        style={indent(level)}
        className={cn(
          'flex h-[1.375rem] cursor-pointer items-center gap-1 pr-2 outline-none select-none hover:bg-accent/70 focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-inset',
          isSelected && 'bg-accent text-accent-foreground',
          dir && dropAt === entry.path && 'bg-accent/60 ring-1 ring-ring ring-inset',
          color,
          ignored && 'opacity-60',
        )}
      >
        {says.length > 0 && (
          <span id={described} hidden>
            {says.join(', ')}
          </span>
        )}
        {dir ? (
          <>
            {/* An icon theme whose folder icons say open or closed may hide the arrows, as in VS Code. */}
            {icons?.theme.doc.hidesExplorerArrows ? <span className="w-3.5 shrink-0" /> : <ChevronRight className={cn('size-3.5 shrink-0 text-muted-foreground transition-transform duration-150', open && 'rotate-90')} aria-hidden="true" />}
            <FolderIcon path={entry.path} open={open} />
          </>
        ) : (
          <>
            <span className="w-3.5 shrink-0" />
            <FileIcon path={entry.path} />
          </>
        )}
        <span className="min-w-0 flex-1 truncate">{entry.name}</span>
        {entry.link && <Link className="size-3 shrink-0 text-muted-foreground" aria-hidden="true" />}
        {marks.badges && (errors > 0 || warnings > 0) && (
          <span className="shrink-0 text-[0.6875rem] font-semibold" aria-hidden="true">
            {errors > 0 ? errors : warnings}
          </span>
        )}
        {change ? (
          <span className="shrink-0 font-mono text-[0.6875rem] font-semibold" aria-hidden="true">
            {change.created ? 'A' : 'M'}
          </span>
        ) : (
          marks.badges && gitMark && (
            <span className="shrink-0 font-mono text-[0.6875rem] font-semibold" aria-hidden="true">
              {gitMark}
            </span>
          )
        )}
        {changedUnder && !open && <span className="size-1.5 shrink-0 rounded-full bg-warning" aria-hidden="true" />}
        {!changedUnder && (errorsUnder || warningsUnder) && marks.colors && !open && (
          <span className={cn('size-1.5 shrink-0 rounded-full', errorsUnder ? 'bg-destructive' : 'bg-warning')} aria-hidden="true" />
        )}
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
      {type === 'dir' ? <FolderIcon path={name} /> : <FileIcon path={name} />}
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
