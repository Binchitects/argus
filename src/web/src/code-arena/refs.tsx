import { useQuery } from '@tanstack/react-query'
import { useMemo, type ReactNode } from 'react'
import { FileRefs, type FileRefsApi } from '@/pages/chat/file-refs'
import { useEditor } from './editor-state'
import { allFilesQuery } from './ide-api'

/**
 * The files the agent cites in its answers, as links into the editor (at the lines cited): the folder's files only, by
 * the path in the folder; a path written whole (the folder's own path first) counts as the same file.
 */
export function CodeRefsProvider({ folder, children }: { folder: string; children: ReactNode }) {
  const { open } = useEditor()
  const all = useQuery(allFilesQuery)
  const value = useMemo<FileRefsApi>(() => {
    const files = new Set(all.data?.files ?? [])
    const root = folder.replace(/\\/g, '/').replace(/\/+$/, '') + '/'
    return {
      known: (cited) => {
        let path = cited.replace(/\\/g, '/')
        if (path.startsWith(root)) path = path.slice(root.length)
        path = path.replace(/^(\.\/)+/, '')
        if (!path || path.startsWith('/') || /^[a-z]:/i.test(path) || path.split('/').includes('..')) return null
        // Past the list's end (a very big folder), a path with a file's name is taken at its word: opening it says if it is not there.
        return files.has(path) || (all.data?.truncated && /\.\w+$/.test(path)) ? path : null
      },
      open: (ref) => void open(ref.path, ref.line ? { line: ref.line, column: ref.column, endLine: ref.end } : undefined),
    }
  }, [all.data, folder, open])
  return <FileRefs value={value}>{children}</FileRefs>
}
