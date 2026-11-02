import { ChevronRight, ChevronsDownUp, List, ListTree } from 'lucide-react'
import type { ReactNode } from 'react'
import { Tooltip } from '@/components/ui/tooltip'
import { cn } from '@/lib/utils'
import { folderTree, rowKeys, treeIndent, type ChangeView, type Folder } from './change-folds'
import { FolderIcon } from './file-icon'

// The parts of the lists of changed files that fold as VS Code's do (Source Control, Agent changes): a group's heading,
// its files as a list or a tree of folders, and the view's title buttons. change-folds.ts keeps what is folded.

const tool =
  'grid size-6 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring [&_svg]:size-3.5'

/** A view's title buttons: View as Tree (or as List), and Collapse All. */
export function ViewTools({ view, keys }: { view: ChangeView; keys: readonly string[] }) {
  return (
    <>
      <Tooltip content={view.tree ? 'View as List' : 'View as Tree'}>
        <button type="button" aria-label={view.tree ? 'View as List' : 'View as Tree'} onClick={() => view.setTree(!view.tree)} className={tool}>
          {view.tree ? <List /> : <ListTree />}
        </button>
      </Tooltip>
      <Tooltip content="Collapse All">
        <button type="button" aria-label="Collapse All" onClick={() => view.foldAll(keys)} className={tool}>
          <ChevronsDownUp />
        </button>
      </Tooltip>
    </>
  )
}

const files = (n: number) => `${n} ${n === 1 ? 'file' : 'files'}`

/**
 * A group of a view under its heading, as VS Code's: the chevron (or Left and Right) folds it, its actions show on hover
 * or focus, its count or `aside` at the end.
 */
export function FoldGroup({
  label,
  title = label,
  count,
  aside,
  open,
  onToggle,
  actions,
  className,
  children,
}: {
  label: string
  title?: ReactNode
  count?: number
  aside?: ReactNode
  open: boolean
  onToggle: () => void
  actions?: ReactNode
  className?: string
  children: ReactNode
}) {
  return (
    <div data-node className={className}>
      <div className="group/head flex h-[1.375rem] items-center pr-1.5 hover:bg-accent/70 focus-within:bg-accent/70">
        <button
          type="button"
          data-row
          data-head
          onKeyDown={rowKeys}
          aria-expanded={open}
          aria-label={count === undefined ? label : `${label}, ${files(count)}`}
          onClick={onToggle}
          className="flex h-full min-w-0 flex-1 items-center gap-0.5 pl-1 text-left text-xs font-semibold text-muted-foreground outline-none focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-inset"
        >
          <ChevronRight className={cn('size-3.5 shrink-0 transition-transform duration-150', open && 'rotate-90')} aria-hidden="true" />
          <span className="truncate">{title}</span>
        </button>
        {actions && <span className="hidden shrink-0 items-center group-focus-within/head:flex group-hover/head:flex">{actions}</span>}
        {count !== undefined && (
          <span className="ml-1 min-w-4 shrink-0 rounded-full bg-muted px-1 text-center text-[0.6875rem] text-muted-foreground tabular-nums" aria-hidden="true">
            {count}
          </span>
        )}
        {aside}
      </div>
      {open && children}
    </div>
  )
}

/**
 * A group's files: a list (each with its folder beside it, `depth` null), or a tree of their folders, each folding with
 * its chevron and having its own actions (on every file under it).
 */
export function ChangeFiles<T>({
  label,
  group,
  items,
  pathOf,
  view,
  row,
  folderActions,
}: {
  label: string
  group: string
  items: readonly T[]
  pathOf: (item: T) => string
  view: ChangeView
  row: (item: T, depth: number | null) => ReactNode
  folderActions?: (folder: Folder<T>, paths: string[]) => ReactNode
}) {
  if (!view.tree) return <ul aria-label={label}>{items.map((item) => row(item, null))}</ul>
  const under = (folder: Folder<T>) => items.map(pathOf).filter((p) => p.startsWith(`${folder.path}/`))
  const draw = (at: Folder<T>, depth: number): ReactNode[] => [
    ...at.folders.map((f) => {
      const key = `${group}:${f.path}`
      const open = !view.folded(key)
      return (
        <li key={key} data-node>
          <div className="group flex h-[1.375rem] items-center pr-1.5 hover:bg-accent/70 focus-within:bg-accent/70">
            <button
              type="button"
              data-row
              data-head
              onKeyDown={rowKeys}
              aria-expanded={open}
              aria-label={`${f.name}, ${files(f.count)}`}
              title={f.path}
              onClick={() => view.toggle(key)}
              style={treeIndent(depth, true)}
              className="flex h-full min-w-0 flex-1 items-center gap-1 text-left outline-none focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-inset"
            >
              <ChevronRight className={cn('size-3.5 shrink-0 text-muted-foreground transition-transform duration-150', open && 'rotate-90')} aria-hidden="true" />
              <FolderIcon path={f.path} open={open} />
              <span className="truncate">{f.name}</span>
            </button>
            {folderActions && <span className="hidden shrink-0 items-center group-focus-within:flex group-hover:flex">{folderActions(f, under(f))}</span>}
          </div>
          {open && <ul aria-label={`Files in ${f.path}`}>{draw(f, depth + 1)}</ul>}
        </li>
      )
    }),
    ...at.files.map((item) => row(item, depth)),
  ]
  return <ul aria-label={label}>{draw(folderTree(items, pathOf, view.compact), 0)}</ul>
}

