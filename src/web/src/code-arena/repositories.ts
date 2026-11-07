import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useMemo } from 'react'
import { useEditor } from './editor-state'
import { isUserFile } from './editor-options'
import { gitRepositoriesQuery, type GitRepository, type RepositoryDetection } from './ide-api'
import { useSetting } from './settings'

/**
 * The repositories Source Control shows, as VS Code finds them: the folder's own and each added folder's, and as
 * "git.autoRepositoryDetection" says, those in their folders (down to "git.repositoryScanMaxDepth", but the folders
 * "git.repositoryScanIgnoredFolders" names) and those of the files open in the editor. Null until code-arena lists them,
 * and when it cannot.
 */
export function useRepositories(): { repositories: GitRepository[] | null } {
  const detect = useSetting<boolean | string>('git.autoRepositoryDetection')
  const depth = useSetting<number>('git.repositoryScanMaxDepth')
  const ignored = useSetting<string[]>('git.repositoryScanIgnoredFolders')
  const { tabs } = useEditor()
  const mode: RepositoryDetection['detect'] = detect === 'subFolders' || detect === 'openEditors' ? detect : detect !== false && detect !== 'false'
  // The files open, when they count: each once, in order, so the question is the same while they are.
  const open = useMemo(
    () => (mode === true || mode === 'openEditors' ? [...new Set(tabs.filter((t) => t.kind === 'file' && !isUserFile(t.path)).map((t) => t.path))].sort() : []),
    [mode, tabs],
  )
  const detection: RepositoryDetection = { detect: mode, depth: typeof depth === 'number' ? depth : 1, ignored: Array.isArray(ignored) ? ignored : ['node_modules'], open }
  // A file opened in a repository already listed asks again; what was listed stays meanwhile.
  const list = useQuery({ ...gitRepositoriesQuery(detection), placeholderData: keepPreviousData })
  return { repositories: list.data ?? null }
}
