import { FolderInput, FolderOpen, History, HardDrive, Undo2 } from 'lucide-react'
import { createElement } from 'react'
import { toast } from '@/components/ui/toaster'
import { api, ApiError } from './http'
import { quickPick, type PickItem } from './quick-pick'

/** A folder's folders, as code-arena lists them for the picker. */
export interface FolderList {
  path: string
  parent: string | null
  folders: string[]
  /** Why some or all are not listed (too many, not readable). */
  why: string | null
  /** The folder open now. */
  current: string
  home: string
  separator: string
  drives: string[]
  recent: string[]
  /** The folders added to this workspace (Add Folder), whole. */
  added: string[]
  /** Its files' names, when asked for (@ in the chat). */
  files?: string[] | null
}

export const listFolders = (path?: string, files = false) =>
  api<FolderList>(`/api/folders${path || files ? '?' : ''}${path ? `path=${encodeURIComponent(path)}` : ''}${path && files ? '&' : ''}${files ? 'files=1' : ''}`)

export const foldersQuery = { queryKey: ['code', 'folders'] as const, queryFn: () => listFolders() }

/** Add Folder to Workspace…: chosen, then added (the tools may use it, the Explorer shows it); the folders added now. */
export async function chooseAndAddFolder(): Promise<string[] | undefined> {
  const path = await pickFolder('Add Folder to Workspace', 'Add')
  if (!path) return undefined
  try {
    return (await api<{ added: string[] }>('/api/folders/add', { body: { path } })).added
  } catch (e) {
    toast.error(e instanceof ApiError ? e.message : `${path} could not be added.`)
    return undefined
  }
}

/** A folder added to the workspace, taken out of it again; the folders added now. */
export const removeFolder = (path: string) => api<{ added: string[] }>('/api/folders/remove', { body: { path } })

/** Whether a typed path stands on its own (a root, a drive, the home folder), not under the folder shown. */
const absolute = (path: string) => /^([/\\~]|[A-Za-z]:)/.test(path)

const inside = (folder: string, name: string, separator: string) => (folder.endsWith(separator) ? folder + name : folder + separator + name)

type Step = { kind: 'go' | 'take'; path: string }

/**
 * A folder chosen as VS Code's simple dialog chooses one: the folder shown and its folders, Enter goes into one, the
 * first row takes the folder shown; a path typed goes there (from the folder shown, or whole). The recent ones and, on
 * Windows, the drives are listed too. Undefined: nothing chosen.
 */
export async function pickFolder(title: string, action: string, start?: string): Promise<string | undefined> {
  let at: FolderList
  try {
    at = await listFolders(start)
  } catch (e) {
    toast.error(e instanceof ApiError ? e.message : 'The folders could not be listed.')
    return undefined
  }
  for (;;) {
    const here = at
    const item = (id: string, label: string, step: Step, extra: Partial<PickItem<Step>> = {}): PickItem<Step> => ({ id, label, value: step, ...extra })
    const items: PickItem<Step>[] = [
      item('take', `${action} ${here.path.split(here.separator).filter(Boolean).at(-1) ?? here.path}`, { kind: 'take', path: here.path }, { description: here.path, icon: createElement(FolderOpen), detail: here.why ?? undefined }),
      ...(here.parent ? [item('up', '..', { kind: 'go', path: here.parent }, { description: here.parent, icon: createElement(Undo2) })] : []),
      ...here.folders.map((name) => item(`f:${name}`, name, { kind: 'go', path: inside(here.path, name, here.separator) }, { icon: createElement(FolderInput) })),
      ...here.recent
        .filter((f) => f !== here.path)
        .map((f) => item(`r:${f}`, f, { kind: 'go', path: f }, { group: 'recently opened', icon: createElement(History) })),
      ...here.drives.map((d) => item(`d:${d}`, d, { kind: 'go', path: d }, { group: 'drives', icon: createElement(HardDrive) })),
    ]
    const chosen = await quickPick<Step>({
      title,
      placeholder: `${here.path}: type a folder's name or path, Enter goes into it`,
      items,
      free: (typed) => {
        const t = typed.trim()
        if (!t) return null
        const path = absolute(t) ? t : inside(here.path, t, here.separator)
        return item('typed', `Go to ${path}`, { kind: 'go', path })
      },
    })
    if (!chosen?.value) return undefined
    if (chosen.value.kind === 'take') return chosen.value.path
    try {
      at = await listFolders(chosen.value.path)
    } catch (e) {
      toast.error(e instanceof ApiError ? e.message : `${chosen.value.path} could not be listed.`)
    }
  }
}

/**
 * Opens the folder here: code-arena starts again on it, at this address, and the page loads it once it answers for
 * that folder (within a minute; else it says so).
 */
export async function openFolder(path: string, onOpening: (folder: string) => void): Promise<void> {
  let folder: string
  try {
    folder = (await api<{ folder: string }>('/api/folders/open', { body: { path } })).folder
  } catch (e) {
    toast.error(e instanceof ApiError ? e.message : `${path} could not be opened.`)
    return
  }
  onOpening(folder)
  for (let waited = 0; waited < 60_000; waited += 300) {
    await new Promise((r) => setTimeout(r, 300))
    try {
      if ((await listFolders()).current === folder) {
        window.location.reload()
        return
      }
    } catch {
      // Between the two runs: not answering for a moment.
    }
  }
  toast.error(`Code Arena did not start again on ${folder}: see its terminal.`)
}

/** Open Folder…: chosen, then opened. */
export async function chooseAndOpenFolder(onOpening: (folder: string) => void) {
  const path = await pickFolder('Open Folder', 'Open')
  if (path) await openFolder(path, onOpening)
}

/** Open Recent…: one of the folders opened lately, then opened. */
export async function openRecentFolder(onOpening: (folder: string) => void) {
  let list: FolderList
  try {
    list = await listFolders()
  } catch (e) {
    toast.error(e instanceof ApiError ? e.message : 'The recent folders could not be listed.')
    return
  }
  const recent = list.recent.filter((f) => f !== list.current)
  const chosen = await quickPick<string>({
    title: 'Open Recent',
    placeholder: recent.length ? 'A folder opened lately' : 'No other folder was opened lately: Open Folder… chooses one',
    items: [
      ...recent.map((f) => ({ id: f, label: f.split(list.separator).filter(Boolean).at(-1) ?? f, description: f, value: f, icon: createElement(History) })),
      { id: 'more', label: 'More…', description: 'Open Folder', value: '', icon: createElement(FolderOpen) },
    ],
  })
  if (chosen?.id === 'more') await chooseAndOpenFolder(onOpening)
  else if (chosen?.value) await openFolder(chosen.value, onOpening)
}
