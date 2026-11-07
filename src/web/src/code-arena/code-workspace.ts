import type { QueryClient } from '@tanstack/react-query'
import { File, FolderOpen } from 'lucide-react'
import { createElement } from 'react'
import { toast } from '@/components/ui/toaster'
import { chooseAndAddFolder, foldersQuery, listFolders, pickFolder, removeFolder, waitForFolder, type FolderList } from './folders'
import { api, ApiError } from './http'
import { parse, setProperty } from './jsonc'
import { inputBox, quickPick, type PickItem } from './quick-pick'

// VS Code's multi-root workspace in Code Arena: a NAME.code-workspace file opened, saved, closed; its folders in the
// Explorer, Source Control and search; its "settings" a layer between the person's and the folder's. When one is open,
// Add Folder and Remove Folder change its "folders" (its comments kept), as VS Code does.

export interface WorkspaceFolder {
  /** As the page names it: "." for the folder open, else its whole path. */
  path: string
  full: string
  name: string
  exists: boolean
  root: boolean
}

export interface WorkspaceFileInfo {
  /** The file; null when the folder is open alone. */
  path: string | null
  name?: string
  dir?: string
  text?: string
  version?: string
  settings?: Record<string, unknown>
  folders?: WorkspaceFolder[]
  rootInIt?: boolean
  problems?: string[]
}

export const workspaceFileQuery = {
  queryKey: ['code', 'workspace-file'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<WorkspaceFileInfo>('/api/workspace-file', { signal }),
  staleTime: 10_000,
}

export const isWorkspaceFile = (path: string) => /\.code-workspace$/i.test(path)

/** The file's text written (the page's edit of it, the version it read): code-arena applies its folders. */
export const writeWorkspaceFile = (text: string, version: string | undefined) => api<WorkspaceFileInfo>('/api/workspace-file', { body: { text, version } })

/** A folder as a workspace file names it: from the file's folder with / (".", "../lib", "api"), else whole. */
export function relativeTo(dir: string, full: string): string {
  const norm = (p: string) => p.replace(/\\/g, '/').replace(/\/+$/, '')
  const a = norm(dir).split('/')
  const b = norm(full).split('/')
  // Another drive (C: and D:): whole.
  if (a[0]?.toLowerCase() !== b[0]?.toLowerCase()) return norm(full)
  let i = 0
  while (i < a.length && i < b.length && a[i] === b[i]) i++
  const up = a.slice(i).map(() => '..')
  const rel = [...up, ...b.slice(i)].join('/')
  return rel === '' ? '.' : rel
}

/** Everything read again that the workspace's folders show: the Explorer's roots, Source Control's repositories, the settings. */
async function refresh(queryClient: QueryClient) {
  await Promise.all([
    queryClient.invalidateQueries({ queryKey: workspaceFileQuery.queryKey }),
    queryClient.invalidateQueries({ queryKey: foldersQuery.queryKey }),
    queryClient.invalidateQueries({ queryKey: ['code', 'git'] }),
    queryClient.invalidateQueries({ queryKey: ['code', 'search'] }),
  ])
}

/** The open workspace file's "folders" changed as `change` says, its comments and the rest kept; false when none is open. */
async function editFolders(queryClient: QueryClient, change: (folders: unknown[], dir: string) => unknown[]): Promise<boolean> {
  const info = await queryClient.fetchQuery({ ...workspaceFileQuery, staleTime: 0 })
  if (!info.path || info.text === undefined || !info.dir) return false
  const value = parse(info.text).value as { folders?: unknown } | null
  const folders = Array.isArray(value?.folders) ? value.folders : []
  const text = setProperty(info.text, ['folders'], change(folders, info.dir))
  queryClient.setQueryData(workspaceFileQuery.queryKey, await writeWorkspaceFile(text, info.version))
  return true
}

/** A whole path with its . and .. worked out, / between parts. */
export function normalize(path: string): string {
  const parts: string[] = []
  const [first, ...rest] = path.replace(/\\/g, '/').split('/')
  for (const p of rest) {
    if (p === '' || p === '.') continue
    if (p === '..') parts.pop()
    else parts.push(p)
  }
  return `${first}/${parts.join('/')}`
}

/** Where a folder entry of the file leads, whole (its path from the file's folder). */
const entryFull = (entry: unknown, dir: string) => {
  const path = entry && typeof entry === 'object' && typeof (entry as { path?: unknown }).path === 'string' ? (entry as { path: string }).path : null
  if (!path) return null
  return normalize(/^([/\\]|[A-Za-z]:)/.test(path) ? path : `${dir}/${path}`)
}

/** Add Folder to Workspace…: added, and named in the workspace file when one is open. */
export async function addFolder(queryClient: QueryClient) {
  const added = await chooseAndAddFolder()
  if (!added) return
  const folder = added.at(-1)!
  try {
    await editFolders(queryClient, (folders, dir) => [...folders, { path: relativeTo(dir, folder) }])
  } catch (e) {
    toast.error(e instanceof ApiError ? e.message : 'The workspace file could not be changed.')
  }
  await refresh(queryClient)
}

/** Remove Folder from Workspace: taken out, and out of the workspace file when one is open. */
export async function takeOutFolder(queryClient: QueryClient, folder: string) {
  try {
    const inFile = await editFolders(queryClient, (folders, dir) => folders.filter((f) => {
      const full = entryFull(f, dir)
      return !full || full !== normalize(folder)
    }))
    // The file applied: the folder is out already.
    if (!inFile) await removeFolder(folder)
  } catch (e) {
    toast.error(e instanceof ApiError ? e.message : `${folder} could not be taken out.`)
  }
  await refresh(queryClient)
}

/** Opens a workspace file: here at once when it names the folder open, else code-arena starts again on its first folder. */
export async function openWorkspace(path: string, onOpening: (folder: string) => void, queryClient?: QueryClient) {
  let answer: { folder: string; restart: boolean }
  try {
    answer = await api<{ folder: string; restart: boolean }>('/api/workspace-file/open', { body: { path } })
  } catch (e) {
    toast.error(e instanceof ApiError ? e.message : `${path} could not be opened.`)
    return
  }
  if (!answer.restart) {
    if (queryClient) await refresh(queryClient)
    else window.location.reload()
    toast.success(`${path.split(/[\\/]/).at(-1)} is open.`)
    return
  }
  onOpening(answer.folder)
  await waitForFolder(answer.folder)
}

/** Open Workspace from File…: a .code-workspace file chosen as Open Folder chooses a folder, its files listed too. */
export async function chooseAndOpenWorkspace(onOpening: (folder: string) => void, queryClient: QueryClient) {
  let at: FolderList
  try {
    at = await listFolders(undefined, true)
  } catch (e) {
    toast.error(e instanceof ApiError ? e.message : 'The folders could not be listed.')
    return
  }
  for (;;) {
    const here = at
    const inside = (name: string) => (here.path.endsWith(here.separator) ? here.path + name : here.path + here.separator + name)
    type Step = { kind: 'go' | 'take'; path: string }
    const items: PickItem<Step>[] = [
      ...(here.files ?? []).filter(isWorkspaceFile).map((name) => ({ id: `w:${name}`, label: name, description: 'workspace', icon: createElement(File), value: { kind: 'take' as const, path: inside(name) } })),
      ...(here.parent ? [{ id: 'up', label: '..', description: here.parent, value: { kind: 'go' as const, path: here.parent } }] : []),
      ...here.folders.map((name) => ({ id: `f:${name}`, label: name, icon: createElement(FolderOpen), value: { kind: 'go' as const, path: inside(name) } })),
      ...here.recent.filter(isWorkspaceFile).map((f) => ({ id: `r:${f}`, label: f.split(/[\\/]/).at(-1) ?? f, description: f, group: 'recently opened', value: { kind: 'take' as const, path: f } })),
    ]
    const chosen = await quickPick<Step>({
      title: 'Open Workspace from File',
      placeholder: `${here.path}: a .code-workspace file, or a folder to look in`,
      items,
      free: (typed) => {
        const t = typed.trim()
        if (!t) return null
        const path = /^([/\\~]|[A-Za-z]:)/.test(t) ? t : inside(t)
        return { id: 'typed', label: isWorkspaceFile(path) ? `Open ${path}` : `Go to ${path}`, value: { kind: isWorkspaceFile(path) ? 'take' : 'go', path } }
      },
    })
    if (!chosen?.value) return
    if (chosen.value.kind === 'take') {
      await openWorkspace(chosen.value.path, onOpening, queryClient)
      return
    }
    try {
      at = await listFolders(chosen.value.path, true)
    } catch (e) {
      toast.error(e instanceof ApiError ? e.message : `${chosen.value.path} could not be listed.`)
    }
  }
}

/** Save Workspace As…: the folder open and those added, in a .code-workspace file of a folder chosen. */
export async function saveWorkspaceAs(queryClient: QueryClient) {
  const list = await listFolders().catch(() => null)
  const dir = await pickFolder('Save Workspace As: the folder to keep it in', 'Save in', list?.path)
  if (!dir) return
  const project = (list?.current ?? '').split(/[\\/]/).filter(Boolean).at(-1) ?? 'workspace'
  const name = await inputBox({ title: 'Save Workspace As', placeholder: 'The workspace file name', value: `${project}.code-workspace`, validate: (v) => (/^[^\\/]+$/.test(v.trim()) ? null : 'A file name, with no folder in it.') })
  if (!name) return
  const sep = list?.separator ?? '/'
  const path = `${dir.endsWith(sep) ? dir : dir + sep}${name.trim()}`
  const save = (replace: boolean) => api<WorkspaceFileInfo>('/api/workspace-file/save-as', { body: { path, replace } })
  try {
    queryClient.setQueryData(workspaceFileQuery.queryKey, await save(false))
  } catch (e) {
    if (!(e instanceof ApiError && e.http === 409)) {
      toast.error(e instanceof ApiError ? e.message : 'The workspace could not be saved.')
      return
    }
    const replace = await quickPick<boolean>({ title: `${name} exists already`, placeholder: 'Replace it?', items: [{ id: 'yes', label: 'Replace', value: true }, { id: 'no', label: 'Cancel', value: false }] })
    if (!replace?.value) return
    queryClient.setQueryData(workspaceFileQuery.queryKey, await save(true))
  }
  toast.success(`The workspace is saved as ${path}.`)
  await refresh(queryClient)
}

/** Close Workspace: the folder open alone again. */
export async function closeWorkspace(queryClient: QueryClient) {
  try {
    await api('/api/workspace-file/close', { body: {} })
  } catch (e) {
    toast.error(e instanceof ApiError ? e.message : 'The workspace could not be closed.')
  }
  await refresh(queryClient)
}

/** A setting set in the workspace file's "settings" (for a language when given; undefined: taken out), its comments kept. */
export async function setWorkspaceFileSetting(queryClient: QueryClient, key: string, value: unknown, language?: string) {
  const info = await queryClient.fetchQuery({ ...workspaceFileQuery, staleTime: 0 })
  if (!info.path || info.text === undefined) throw new Error('No workspace file is open.')
  const text = setProperty(info.text, language ? ['settings', `[${language}]`, key] : ['settings', key], value)
  queryClient.setQueryData(workspaceFileQuery.queryKey, await writeWorkspaceFile(text, info.version))
}
