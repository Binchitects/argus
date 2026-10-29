import { useQuery } from '@tanstack/react-query'
import { ChevronRight } from 'lucide-react'
import type { ReactNode } from 'react'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { useEditor } from './editor-state'
import { folderQuery } from './ide-api'
import { useOutline } from './outline-data'
import type { Node } from './outline-tree'
import { useSetting } from './settings'

/** The symbols holding a line, outermost first, each with its siblings (for its picker). */
function chainAt(nodes: Node[], line: number): { node: Node; siblings: Node[] }[] {
  for (const n of nodes) {
    if (n.symbol.start <= line && line <= n.symbol.end) return [{ node: n, siblings: nodes }, ...chainAt(n.children, line)]
  }
  return []
}

const crumb = 'flex h-6 items-center rounded px-1 outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring'

/**
 * The breadcrumbs over the editor, as VS Code's: the file's folders (each lists its files to open), the file, then the
 * symbols the cursor is in (each lists its siblings to go to). "breadcrumbs.enabled", "breadcrumbs.filePath" and
 * "breadcrumbs.symbolPath" say what shows.
 */
export function Breadcrumbs({ path, end }: { path: string; end?: ReactNode }) {
  const ed = useEditor()
  const symbolsOn = useSetting<string>('breadcrumbs.symbolPath') !== 'off'
  const pathOn = useSetting<string>('breadcrumbs.filePath') !== 'off'
  const { tree } = useOutline(path, symbolsOn)
  const parts = path.split('/')
  const chain = symbolsOn && ed.cursor ? chainAt(tree, ed.cursor.line) : []
  const sep = <ChevronRight className="size-3 shrink-0 opacity-60" aria-hidden="true" />
  return (
    <nav aria-label="Breadcrumbs" className="flex h-8 shrink-0 items-center gap-0.5 overflow-hidden border-b px-2 text-xs text-muted-foreground" data-breadcrumbs="">
      {pathOn &&
        parts.slice(0, -1).map((name, i) => (
          <span key={i} className="flex shrink-0 items-center gap-0.5">
            <FolderCrumb folder={parts.slice(0, i + 1).join('/')} name={name} />
            {sep}
          </span>
        ))}
      <span className="shrink-0 px-1 font-medium text-foreground">{parts.at(-1)}</span>
      {chain.map(({ node, siblings }) => (
        <span key={node.symbol.qualified + ':' + node.symbol.start} className="flex min-w-0 items-center gap-0.5">
          {sep}
          <DropdownMenu>
            <DropdownMenuTrigger className={crumb} aria-label={`${node.symbol.name}: the symbols beside it`}>
              <span className="truncate">{node.symbol.name}</span>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="start" className="max-h-80 overflow-auto">
              {siblings.map((s) => (
                <DropdownMenuItem key={s.symbol.qualified + ':' + s.symbol.start} onSelect={() => void ed.open(path, { line: s.symbol.start })}>
                  <span className="truncate">{s.symbol.name}</span>
                  <span className="ml-auto pl-4 text-xs text-muted-foreground">{s.symbol.kind}</span>
                </DropdownMenuItem>
              ))}
            </DropdownMenuContent>
          </DropdownMenu>
        </span>
      ))}
      {end}
    </nav>
  )
}

/** A folder of the file's path: its files, to open one. */
function FolderCrumb({ folder, name }: { folder: string; name: string }) {
  const ed = useEditor()
  const list = useQuery({ ...folderQuery(folder), staleTime: 5_000 })
  return (
    <DropdownMenu>
      <DropdownMenuTrigger className={crumb} aria-label={`${name}: its files`}>
        {name}
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="max-h-80 overflow-auto">
        {list.isPending && <p className="px-2 py-1.5 text-xs text-muted-foreground">Listing…</p>}
        {list.data?.entries
          .filter((e) => e.kind === 'file')
          .map((e) => (
            <DropdownMenuItem key={e.path} onSelect={() => void ed.open(e.path)}>
              {e.name}
            </DropdownMenuItem>
          ))}
        {list.data && list.data.entries.every((e) => e.kind !== 'file') && <p className="px-2 py-1.5 text-xs text-muted-foreground">No files here, only folders.</p>}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
