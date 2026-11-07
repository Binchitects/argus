import type * as Monaco from 'monaco-editor'
import { createContext } from 'react'
import type { Command } from './commands'
import type { EditorApi, FileModel, Tab } from './editor-state'
import { quickPick } from './quick-pick'

// The editor groups, as VS Code's: the editor split right or down into groups side by side (or one above another), each
// with its own tabs and its own active tab, all showing the same files (one Monaco model per file, whichever group shows
// it, so an edit in one is in the other at once). The first group is the editor's own provider; each other is a provider
// of its own that takes this store. The workbench's commands (save, close, go to a file…) work on the group with the focus.

/** What a group is made with, or what a tab dragged into it opens there: a tab's kind and what it shows. */
export type Opening = Pick<Tab, 'kind' | 'path' | 'source' | 'left' | 'right'>

/** How the groups are laid out: their ids in order, side by side ("row") or one above another ("column"), and their sizes. */
export interface GroupLayout {
  groups: number[]
  orientation: 'row' | 'column'
  /** Each group's share of the space, by id (1 each when not resized). */
  sizes: Record<number, number>
}

export interface GroupStore {
  files: Map<string, FileModel>
  loading: Map<string, Promise<FileModel | 'binary' | 'tooLarge'>>
  peeked: Map<string, true>
  /** Every group's code editors and diff editors: a model going away goes from all of them. */
  codeEditors: Set<Monaco.editor.IStandaloneCodeEditor>
  diffEditors: Set<Monaco.editor.IStandaloneDiffEditor>
  /** Each group's way of marking its tabs unsaved or not, for a file changed in any. */
  markers: Map<number, (f: FileModel) => void>
  apis: Map<number, EditorApi>
  layout: GroupLayout
  focused: number
  version: number
  /** What the workbench reaches the editor through (routedApi), made again as the groups change. */
  routed: EditorApi | null
  subscribe: (listener: () => void) => () => void
  /** A group there, or its api changed. */
  join: (group: number, api: EditorApi, tabs: () => Tab[], collect: () => void) => void
  /** A group gone. */
  leave: (group: number) => void
  /** The tabs of every group but this one. */
  others: (group: number) => Tab[]
  /** The first group lets go of the models no group holds. */
  collect: () => void
  focus: (group: number) => void
  /** A new group beside (or below) `from`, made with `opening` (then `then`, once it is open there); its id. */
  split: (from: number, direction: 'right' | 'down', opening?: Opening, then?: () => void) => number
  closeGroup: (group: number) => void
  resize: (group: number, share: number) => void
  /** What a new group opens once it is there (once), and what follows. */
  takePending: (group: number) => { opening: Opening; then?: () => void } | undefined
  setOrientation: (o: GroupLayout['orientation']) => void
  /** Every group's tabs back in the first, the others closed. */
  joinAll: () => void
  forget: (ed: Monaco.editor.IStandaloneCodeEditor | Monaco.editor.IStandaloneDiffEditor) => void
}

/** The most groups: as many as fit is VS Code's, eight is plenty in a browser's window. */
export const mostGroups = 8

export function newGroupStore(): GroupStore {
  const listeners = new Set<() => void>()
  const tabsOf = new Map<number, () => Tab[]>()
  const collectors = new Map<number, () => void>()
  const pending = new Map<number, { opening: Opening; then?: () => void }>()
  let next = 2
  const changed = () => {
    store.version++
    store.routed = store.apis.size > 0 ? routedApi(store.apis, store.focused, store.apis.get(1) ?? [...store.apis.values()][0]!) : null
    for (const l of listeners) l()
  }
  const store: GroupStore = {
    files: new Map(),
    loading: new Map(),
    peeked: new Map(),
    codeEditors: new Set(),
    diffEditors: new Set(),
    markers: new Map(),
    apis: new Map(),
    layout: { groups: [1], orientation: 'row', sizes: {} },
    focused: 1,
    version: 0,
    routed: null,
    subscribe: (l) => {
      listeners.add(l)
      return () => {
        listeners.delete(l)
      }
    },
    join: (group, api, tabs, collect) => {
      tabsOf.set(group, tabs)
      collectors.set(group, collect)
      if (store.apis.get(group) === api) return
      store.apis.set(group, api)
      changed()
    },
    leave: (group) => {
      tabsOf.delete(group)
      collectors.delete(group)
      store.apis.delete(group)
      store.markers.delete(group)
      changed()
    },
    others: (group) => [...tabsOf].filter(([g]) => g !== group).flatMap(([, tabs]) => tabs()),
    collect: () => collectors.get(1)?.(),
    focus: (group) => {
      if (store.focused === group || !store.layout.groups.includes(group)) return
      store.focused = group
      changed()
    },
    split: (from, direction, opening, then) => {
      const l = store.layout
      if (l.groups.length >= mostGroups) return from
      const id = next++
      const at = l.groups.indexOf(from)
      // Split the other way: every group takes it (the groups are a row or a column).
      const orientation = direction === 'right' ? 'row' : 'column'
      store.layout = { groups: [...l.groups.slice(0, at + 1), id, ...l.groups.slice(at + 1)], orientation, sizes: l.sizes }
      if (opening) pending.set(id, { opening, then })
      store.focused = id
      changed()
      return id
    },
    closeGroup: (group) => {
      if (group === 1 || !store.layout.groups.includes(group)) return
      const l = store.layout
      const at = l.groups.indexOf(group)
      const groups = l.groups.filter((g) => g !== group)
      const sizes = { ...l.sizes }
      delete sizes[group]
      store.layout = { ...l, groups, sizes }
      if (store.focused === group) store.focused = groups[Math.max(0, at - 1)] ?? 1
      changed()
    },
    resize: (group, share) => {
      store.layout = { ...store.layout, sizes: { ...store.layout.sizes, [group]: share } }
      changed()
    },
    takePending: (group) => {
      const p = pending.get(group)
      pending.delete(group)
      return p
    },
    setOrientation: (o) => {
      store.layout = { ...store.layout, orientation: o }
      changed()
    },
    joinAll: () => {
      const first = store.apis.get(1)
      for (const g of store.layout.groups.filter((x) => x !== 1)) {
        const api = store.apis.get(g)
        if (!api || !first) continue
        for (const t of api.tabs) void openIn(first, t, true)
      }
      store.layout = { groups: [1], orientation: store.layout.orientation, sizes: {} }
      store.focused = 1
      changed()
    },
    forget: (ed) => {
      store.codeEditors.delete(ed as Monaco.editor.IStandaloneCodeEditor)
      store.diffEditors.delete(ed as Monaco.editor.IStandaloneDiffEditor)
    },
  }
  return store
}

/** Opens in a group what a tab of another shows: its file, compare, preview or page (behind the tab shown, with `behind`). */
export async function openIn(api: EditorApi, t: Opening, behind = false) {
  switch (t.kind) {
    case 'file':
    case 'media':
      if (behind) await api.keep([t.path])
      else await api.open(t.path)
      return
    case 'preview':
      await api.openPreview(t.path)
      return
    case 'merge':
      await api.openMerge(t.path)
      return
    case 'notebook':
      await api.open(t.path)
      return
    case 'settings':
    case 'keybindings':
      api.openPage(t.kind)
      return
    case 'diff':
      if (t.source && t.source !== 'compare') await api.openDiff(t.path, t.source)
      else if (t.left && t.right) await api.compare(t.left, t.right)
  }
}

/**
 * What the workbench reaches the editor through: the focused group's api, but for what concerns every group's tabs (a
 * file changed, renamed or deleted on disk, the check's problems, Save All, the automatic save on leaving).
 */
export function routedApi(apis: GroupStore['apis'], focused: number, own: EditorApi): EditorApi {
  const api = apis.get(focused) ?? own
  const all = () => (apis.size > 0 ? [...apis.values()] : [own])
  if (all().length <= 1) return api
  return {
    ...api,
    refresh: async (path) => {
      await Promise.all(all().map((a) => a.refresh(path)))
    },
    moved: (from, to) => {
      for (const a of all()) a.moved(from, to)
    },
    removed: (path) => {
      for (const a of all()) a.removed(path)
    },
    showProblems: (problems) => {
      for (const a of all()) a.showProblems(problems)
    },
    saveAll: async () => {
      for (const a of all()) await a.saveAll()
    },
    autoSaveOn: async (event) => {
      for (const a of all()) await a.autoSaveOn(event)
    },
  }
}

export const GroupStoreContext = createContext<GroupStore | null>(null)

/** A group's own api, for its own tab strip and editor (the workbench's EditorContext is the focused group's). */
export const OwnEditorContext = createContext<EditorApi | null>(null)

/**
 * Open to the Side, as VS Code's: in the group right of (or below) the one with the focus, made when there is none, and
 * the focus goes there.
 */
export async function openToSide(store: GroupStore, opening: Opening) {
  const groups = store.layout.groups
  const next = groups[groups.indexOf(store.focused) + 1]
  const api = next === undefined ? undefined : store.apis.get(next)
  if (next === undefined || !api) {
    if (groups.length >= mostGroups) {
      const own = store.apis.get(store.focused)
      if (own) await openIn(own, opening)
      return
    }
    store.split(store.focused, store.layout.orientation === 'column' && groups.length > 1 ? 'down' : 'right', opening)
    return
  }
  await openIn(api, opening)
  store.focus(next)
  api.focusEditor()
}

const ordinals = ['First', 'Second', 'Third', 'Fourth', 'Fifth', 'Sixth', 'Seventh', 'Eighth']

/**
 * The editor groups' commands, under VS Code's ids: split, join, close a group, move the active editor to another, and
 * focus a group by its place (Ctrl+2 with one group splits to the right, as VS Code's).
 */
export function groupCommands(store: GroupStore): Command[] {
  const groups = () => store.layout.groups
  const at = () => groups().indexOf(store.focused)
  const activeTab = () => store.apis.get(store.focused)?.active ?? null
  /** The group's editor takes the focus (once it is there: a new group is made at the next render). */
  const focusGroup = (g: number) => {
    store.focus(g)
    requestAnimationFrame(() => store.apis.get(g)?.focusEditor())
  }
  const split = (direction: 'right' | 'down') => {
    const t = activeTab()
    store.split(store.focused, direction, t ?? undefined)
  }
  /** The active editor into the group at `to` (a new one to the right past the last), closed where it was. */
  const moveTo = async (to: number) => {
    const t = activeTab()
    const from = store.apis.get(store.focused)
    if (!t || !from) return
    const target = groups()[to]
    if (target === undefined) {
      if (to < 0) return
      store.split(groups().at(-1)!, 'right', t, () => void from.close(t.id))
      return
    }
    const api = store.apis.get(target)
    if (!api || target === store.focused) return
    await openIn(api, t)
    await from.close(t.id)
    focusGroup(target)
  }
  const several = () => groups().length > 1
  return [
    { id: 'workbench.action.splitEditor', category: 'View', title: 'Split Editor', enabled: () => groups().length < mostGroups, run: () => split(store.layout.orientation === 'column' && several() ? 'down' : 'right') },
    { id: 'workbench.action.splitEditorRight', category: 'View', title: 'Split Editor Right', enabled: () => groups().length < mostGroups, run: () => split('right') },
    { id: 'workbench.action.splitEditorDown', category: 'View', title: 'Split Editor Down', enabled: () => groups().length < mostGroups, run: () => split('down') },
    { id: 'workbench.action.splitEditorOrthogonal', category: 'View', title: 'Split Editor in Orthogonal Direction', enabled: () => groups().length < mostGroups, run: () => split(store.layout.orientation === 'row' ? 'down' : 'right') },
    { id: 'workbench.action.joinAllGroups', category: 'View', title: 'Join All Editor Groups', enabled: several, run: () => store.joinAll() },
    { id: 'workbench.action.closeGroup', category: 'View', title: 'Close Editor Group', enabled: () => store.focused !== 1, run: () => store.closeGroup(store.focused) },
    { id: 'workbench.action.toggleEditorGroupLayout', category: 'View', title: 'Toggle Vertical/Horizontal Editor Layout', enabled: several, run: () => store.setOrientation(store.layout.orientation === 'row' ? 'column' : 'row') },
    { id: 'workbench.action.moveEditorToNextGroup', category: 'View', title: 'Move Editor into Next Group', enabled: () => !!activeTab(), run: () => moveTo(at() + 1) },
    { id: 'workbench.action.moveEditorToPreviousGroup', category: 'View', title: 'Move Editor into Previous Group', enabled: () => !!activeTab() && at() > 0, run: () => moveTo(at() - 1) },
    { id: 'workbench.action.focusNextGroup', category: 'View', title: 'Focus Next Editor Group', enabled: several, run: () => focusGroup(groups()[(at() + 1) % groups().length]!) },
    { id: 'workbench.action.focusPreviousGroup', category: 'View', title: 'Focus Previous Editor Group', enabled: several, run: () => focusGroup(groups()[(at() - 1 + groups().length) % groups().length]!) },
    { id: 'workbench.action.showAllEditors', category: 'View', title: 'Show All Editors By Appearance', run: async () => {
      const items = groups().flatMap((g, i) =>
        (store.apis.get(g)?.tabs ?? []).map((t) => ({ id: `${g}\n${t.id}`, label: t.path.slice(t.path.lastIndexOf('/') + 1) || t.path, description: t.path, group: several() ? `Group ${i + 1}` : undefined, value: { g, id: t.id } })),
      )
      const picked = await quickPick({ title: 'Open editors', placeholder: 'Type the name of an open editor', items })
      if (!picked?.value) return
      const api = store.apis.get(picked.value.g)
      api?.activate(picked.value.id)
      focusGroup(picked.value.g)
    } },
    { id: 'workbench.action.focusRightGroup', category: 'View', title: 'Focus Editor Group to the Right', hidden: true, enabled: () => store.layout.orientation === 'row' && at() < groups().length - 1, run: () => focusGroup(groups()[at() + 1]!) },
    { id: 'workbench.action.focusLeftGroup', category: 'View', title: 'Focus Editor Group to the Left', hidden: true, enabled: () => store.layout.orientation === 'row' && at() > 0, run: () => focusGroup(groups()[at() - 1]!) },
    ...ordinals.map((o, i): Command => ({
      id: `workbench.action.focus${o}EditorGroup`,
      category: 'View',
      title: `Focus ${o} Editor Group`,
      hidden: i > 0,
      run: () => {
        const g = groups()[i]
        if (g !== undefined) return focusGroup(g)
        // One past the last: a new group to the right with the active editor, as VS Code's.
        if (i === groups().length) split('right')
      },
    })),
  ]
}
