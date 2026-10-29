import { useEffect, useMemo, useState } from 'react'
import { errorMessage } from '@/lib/api'
import { useEditor } from './editor-state'
import { outlineOf, type OutlineSymbol } from './ide-api'
import { nest, type Node } from './outline-tree'

/**
 * A file's symbols, nested, from the text the editor holds (unsaved edits too), read again 300 ms after typing pauses:
 * the Outline's and the breadcrumbs'. Null path or off: nothing is read.
 */
export function useOutline(path: string | null, on = true): { tree: Node[]; read: boolean; error?: string } {
  const ed = useEditor()
  const [read, setRead] = useState<{ path: string; symbols: OutlineSymbol[]; error?: string } | null>(null)
  useEffect(() => {
    if (!path || !on) return
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
  }, [ed, path, on])
  const tree = useMemo(() => (read && read.path === path ? nest(read.symbols) : []), [read, path])
  return { tree, read: !!read && read.path === path, error: read?.path === path ? read.error : undefined }
}
