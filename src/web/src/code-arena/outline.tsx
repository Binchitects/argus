import { ChevronDown, ChevronRight, ChevronsDownUp } from 'lucide-react'
import { useEffect, useMemo, useState } from 'react'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { useEditor } from './editor-state'
import { outlineOf, type OutlineSymbol } from './ide-api'
import { innermost, nest, type Node } from './outline-tree'

const kindMark: Record<string, string> = {
  namespace: '{}', class: 'C', struct: 'S', interface: 'I', record: 'R', enum: 'E', method: 'm', function: 'ƒ', constructor: 'c', property: 'p', field: 'f',
  const: 'k', event: 'e', type: 'T', component: '<>', accessor: 'p', heading: '#', indexer: '[]', operator: '±', delegate: 'D',
}

/**
 * The Outline at the foot of the Explorer, as VS Code's: the symbols of the file shown, nested, read from the text the editor
 * holds (unsaved edits too, 300 ms after typing pauses), the one the cursor is in marked; a click goes to it.
 */
export function Outline() {
  const ed = useEditor()
  const path = ed.active?.kind === 'file' && ed.active.status === 'ready' ? ed.active.path : null
  const [open, setOpen] = useState(true)
  const [shut, setShut] = useState<Set<string>>(new Set())
  const [read, setRead] = useState<{ path: string; symbols: OutlineSymbol[]; error?: string } | null>(null)
  useEffect(() => {
    if (!path || !open) return
    let gone = false
    let wait = 0
    let listen: { dispose(): void } | null = null
    const ask = (text: string) =>
      outlineOf(path, text).then(
        (r) => !gone && setRead({ path, symbols: r.symbols }),
        (e: unknown) => !gone && setRead({ path, symbols: [], error: errorMessage(e) }),
      )
    void ed.modelFor(path).then((model) => {
      if (gone || !model) return
      void ask(model.getValue())
      listen = model.onDidChangeContent(() => {
        clearTimeout(wait)
        wait = window.setTimeout(() => !model.isDisposed() && void ask(model.getValue()), 300)
      })
    })
    return () => {
      gone = true
      clearTimeout(wait)
      listen?.dispose()
    }
  }, [ed, path, open])
  const tree = useMemo(() => (read && read.path === path ? nest(read.symbols) : []), [read, path])
  const here = ed.cursor ? innermost(tree, ed.cursor.line) : null
  const rows: { node: Node; level: number }[] = []
  const walk = (nodes: Node[], level: number) => {
    for (const n of nodes) {
      rows.push({ node: n, level })
      if (n.children.length > 0 && !shut.has(n.symbol.qualified + ':' + n.symbol.start)) walk(n.children, level + 1)
    }
  }
  walk(tree, 0)
  return (
    <section aria-label="Outline" className={cn('flex min-h-0 shrink-0 flex-col border-t', open && 'max-h-[40%]')}>
      <div className="flex h-7 shrink-0 items-center pr-1.5">
        <button type="button" aria-expanded={open} onClick={() => setOpen((o) => !o)} className="flex min-w-0 flex-1 items-center gap-1 pl-1.5 text-left text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase outline-none focus-visible:ring-2 focus-visible:ring-ring">
          {open ? <ChevronDown className="size-3.5" aria-hidden="true" /> : <ChevronRight className="size-3.5" aria-hidden="true" />}
          Outline
        </button>
        {open && tree.length > 0 && (
          <button type="button" aria-label="Collapse all" title="Collapse all" onClick={() => setShut(new Set(rows.filter((r) => r.node.children.length > 0).map((r) => r.node.symbol.qualified + ':' + r.node.symbol.start)))} className="grid size-6 place-items-center rounded-sm text-muted-foreground hover:bg-accent hover:text-foreground [&_svg]:size-3.5">
            <ChevronsDownUp />
          </button>
        )}
      </div>
      {open && (
        <div className="min-h-0 overflow-auto pb-2 text-[0.8125rem]">
          {!path && <p className="px-3 py-1 text-muted-foreground">No file is open.</p>}
          {path && read?.error && <p className="px-3 py-1 text-muted-foreground">{read.error}</p>}
          {path && read && read.path === path && !read.error && tree.length === 0 && <p className="px-3 py-1 text-muted-foreground">No symbols in this file.</p>}
          {rows.length > 0 && (
            <ul role="tree" aria-label={`Symbols of ${path}`}>
              {rows.map(({ node, level }) => {
                const s = node.symbol
                const key = s.qualified + ':' + s.start
                const folded = shut.has(key)
                const current = here === s
                return (
                  <li key={key} role="treeitem" aria-selected={current} aria-expanded={node.children.length > 0 ? !folded : undefined} aria-level={level + 1} className="list-none">
                    <div className={cn('flex h-[22px] items-center gap-1 pr-2 hover:bg-accent', current && 'bg-accent/60')} style={{ paddingLeft: 8 + level * 12 }}>
                      {node.children.length > 0 ? (
                        <button
                          type="button"
                          aria-label={folded ? `Expand ${s.name}` : `Collapse ${s.name}`}
                          onClick={() => setShut((x) => (folded ? new Set([...x].filter((k) => k !== key)) : new Set([...x, key])))}
                          className="grid size-4 place-items-center text-muted-foreground"
                        >
                          {folded ? <ChevronRight className="size-3" aria-hidden="true" /> : <ChevronDown className="size-3" aria-hidden="true" />}
                        </button>
                      ) : (
                        <span className="w-4" />
                      )}
                      <button type="button" title={s.signature} onClick={() => path && void ed.open(path, { line: s.start })} className="flex min-w-0 flex-1 items-center gap-1.5 text-left outline-none focus-visible:ring-2 focus-visible:ring-ring">
                        <span className="w-4 shrink-0 text-center font-mono text-[0.6875rem] text-muted-foreground" aria-hidden="true">
                          {kindMark[s.kind] ?? '·'}
                        </span>
                        <span className="truncate">{s.name}</span>
                        <span className="sr-only">, {s.kind}, line {s.start}</span>
                      </button>
                    </div>
                  </li>
                )
              })}
            </ul>
          )}
        </div>
      )}
    </section>
  )
}
