import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type KeyboardEvent } from 'react'
import { saveWorkspaceState, workspaceStateQuery, type WorkspaceState } from './ide-api'
import { useSetting } from './settings'

// What the lists of changed files that fold as VS Code's do (Source Control, Agent changes) are drawn from: the files
// under their folders, what is folded and whether as a list or a tree, kept with the folder; and a tree's keys.

/** A folder of changed files in a tree: those in it, the folders below it, and how many files are under it in all. */
export interface Folder<T> {
  name: string
  path: string
  folders: Folder<T>[]
  files: T[]
  count: number
}

/**
 * The files under their folders, folders first and each by name. Compact: a folder with nothing in it but one folder is
 * one row with it (tests/unit), as VS Code's scm.compactFolders.
 */
export function folderTree<T>(items: readonly T[], pathOf: (item: T) => string, compact = true): Folder<T> {
  const root: Folder<T> = { name: '', path: '', folders: [], files: [], count: 0 }
  for (const item of items) {
    let at = root
    at.count++
    for (const part of pathOf(item).split('/').slice(0, -1)) {
      const path = at.path ? `${at.path}/${part}` : part
      let next = at.folders.find((f) => f.path === path)
      if (!next) {
        next = { name: part, path, folders: [], files: [], count: 0 }
        at.folders.push(next)
      }
      next.count++
      at = next
    }
    at.files.push(item)
  }
  const byName = (a: string, b: string) => a.localeCompare(b, undefined, { numeric: true })
  const tidy = (f: Folder<T>): Folder<T> => {
    let folder = f
    while (compact && folder.path && folder.files.length === 0 && folder.folders.length === 1) {
      const only = folder.folders[0]!
      folder = { ...only, name: `${folder.name}/${only.name}` }
    }
    return {
      ...folder,
      folders: folder.folders.map(tidy).sort((a, b) => byName(a.name, b.name)),
      files: [...folder.files].sort((a, b) => byName(pathOf(a), pathOf(b))),
    }
  }
  return tidy(root)
}

/** The keys of a group's folders as its tree shows them (`group:path`). */
export function folderKeys<T>(group: string, tree: Folder<T>): string[] {
  return tree.folders.flatMap((f) => [`${group}:${f.path}`, ...folderKeys(group, f)])
}

/** How a view of changes shows them: list or tree, what is folded; kept with the folder (its workspace state). */
export interface ChangeView {
  tree: boolean
  compact: boolean
  setTree: (tree: boolean) => void
  folded: (key: string) => boolean
  toggle: (key: string) => void
  /** Folds these, and only these (Collapse All: every group and folder there is). */
  foldAll: (keys: readonly string[]) => void
}

/**
 * A view's folds and its list or tree, kept for the folder as VS Code keeps a view's state for a workspace: under
 * "<scope>.folded" and "<scope>.viewMode". The first view is scm.defaultViewMode's. Only the keys still there are kept.
 */
export function useChangeView(scope: 'scm' | 'changes' | 'skills', keys: readonly string[]): ChangeView {
  const queryClient = useQueryClient()
  const saved = useQuery(workspaceStateQuery).data
  const defaultMode = useSetting<string>('scm.defaultViewMode')
  const compact = useSetting<boolean>('scm.compactFolders') !== false
  const [own, setOwn] = useState<{ folded?: ReadonlySet<string>; tree?: boolean }>({})
  const keptFolded = saved?.[`${scope}.folded`]
  const folded = own.folded ?? new Set(Array.isArray(keptFolded) ? keptFolded.filter((k): k is string => typeof k === 'string') : [])
  const keptMode = saved?.[`${scope}.viewMode`]
  const tree = own.tree ?? (keptMode === 'tree' || keptMode === 'list' ? keptMode === 'tree' : defaultMode === 'tree')
  const keep = (change: Record<string, unknown>) =>
    void saveWorkspaceState(change).then(
      (all) => queryClient.setQueryData<WorkspaceState>(workspaceStateQuery.queryKey, (s) => ({ ...s, ...all })),
      () => undefined,
    )
  const setFolded = (next: ReadonlySet<string>) => {
    setOwn((o) => ({ ...o, folded: next }))
    keep({ [`${scope}.folded`]: [...next].filter((k) => keys.includes(k)) })
  }
  return {
    tree,
    compact,
    setTree: (t) => {
      setOwn((o) => ({ ...o, tree: t }))
      keep({ [`${scope}.viewMode`]: t ? 'tree' : 'list' })
    },
    folded: (key) => folded.has(key),
    toggle: (key) => {
      const next = new Set(folded)
      if (!next.delete(key)) next.add(key)
      setFolded(next)
    },
    foldAll: (all) => setFolded(new Set(all)),
  }
}

/** Where a row of a tree starts: deeper by a step per folder; a file past where a folder's chevron is. */
export const treeIndent = (depth: number, folder = false) => ({ paddingLeft: `${0.75 + depth * 0.75 + (folder ? 0 : 1.125)}rem` })

/**
 * A row's keys, as in VS Code's trees: Up and Down move, Home and End go to the ends, Right unfolds (or goes in), Left
 * folds (or goes up to the group or folder the row is in). Rows are the [data-row] of the view's [data-tree]; a group's
 * or a folder's own row is [data-head] in its [data-node].
 */
export function rowKeys(e: KeyboardEvent<HTMLElement>) {
  const row = e.currentTarget
  if (e.target !== row || e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return
  const rows = [...(row.closest('[data-tree]')?.querySelectorAll<HTMLElement>('[data-row]') ?? [])]
  const at = rows.indexOf(row)
  const open = row.getAttribute('aria-expanded')
  const node = row.closest('[data-node]')
  const parent = row.hasAttribute('data-head') ? node?.parentElement?.closest('[data-node]') : node
  const keys: Record<string, () => void> = {
    ArrowDown: () => rows[at + 1]?.focus(),
    ArrowUp: () => rows[at - 1]?.focus(),
    Home: () => rows[0]?.focus(),
    End: () => rows.at(-1)?.focus(),
    ArrowRight: () => (open === 'false' ? row.click() : open === 'true' ? rows[at + 1]?.focus() : undefined),
    ArrowLeft: () => (open === 'true' ? row.click() : parent?.querySelector<HTMLElement>('[data-head]')?.focus()),
  }
  const run = keys[e.key]
  if (run) {
    e.preventDefault()
    run()
  }
}
