import { describe, expect, it, vi } from 'vitest'
import type { EditorApi, Tab } from './editor-state'
import { groupCommands, mostGroups, newGroupStore } from './editor-groups'

const tab = (path: string): Tab => ({ id: `file:${path}`, kind: 'file', path, status: 'ready', dirty: false }) as Tab

/** A group's api with only what the groups use: its tabs, open, close, focus. */
function fakeApi(tabs: Tab[] = []) {
  const api = {
    tabs,
    active: tabs[0] ?? null,
    open: vi.fn(async (path: string) => {
      api.tabs.push(tab(path))
    }),
    keep: vi.fn(async () => {}),
    close: vi.fn(async (id: string) => {
      api.tabs = api.tabs.filter((t) => t.id !== id)
      return true
    }),
    focusEditor: vi.fn(),
  }
  return api
}

const command = (store: ReturnType<typeof newGroupStore>, id: string) => groupCommands(store).find((c) => c.id === id)!

describe('editor groups', () => {
  it('splits right or down beside the group, at most eight, and closes any group but the first', () => {
    const store = newGroupStore()
    const second = store.split(1, 'right')
    expect(store.layout).toMatchObject({ groups: [1, second], orientation: 'row' })
    expect(store.focused).toBe(second)
    const third = store.split(1, 'down')
    // Beside the group it was split from, the whole layout one above another.
    expect(store.layout).toMatchObject({ groups: [1, third, second], orientation: 'column' })
    for (let i = 3; i < mostGroups + 2; i++) store.split(1, 'right')
    expect(store.layout.groups).toHaveLength(mostGroups)
    store.closeGroup(1)
    expect(store.layout.groups).toContain(1)
    // The one closed had the focus: the group before it takes it.
    const before = store.layout.groups[store.layout.groups.indexOf(third) - 1]
    store.focus(third)
    store.closeGroup(third)
    expect(store.layout.groups).not.toContain(third)
    expect(store.focused).toBe(before)
  })

  it("routes Save All and a file removed on disk to every group, and the rest to the focused group's", async () => {
    const store = newGroupStore()
    const one = { ...fakeApi(), saveAll: vi.fn(async () => {}), removed: vi.fn(), save: vi.fn() } as unknown as EditorApi
    const two = { ...fakeApi(), saveAll: vi.fn(async () => {}), removed: vi.fn(), save: vi.fn() } as unknown as EditorApi
    store.join(1, one, () => [], () => {})
    const g = store.split(1, 'right')
    store.join(g, two, () => [], () => {})
    await store.routed!.saveAll()
    store.routed!.removed('a.ts')
    expect(one.saveAll).toHaveBeenCalled()
    expect(two.saveAll).toHaveBeenCalled()
    expect(one.removed).toHaveBeenCalledWith('a.ts')
    expect(two.removed).toHaveBeenCalledWith('a.ts')
    void store.routed!.save()
    expect(two.save).toHaveBeenCalled()
    expect(one.save).not.toHaveBeenCalled()
  })

  it('focuses a group by its place, and one past the last splits to the right with the editor shown, as Ctrl+2 does', async () => {
    const store = newGroupStore()
    const one = fakeApi([tab('README.md')])
    store.join(1, one as unknown as EditorApi, () => one.tabs, () => {})
    command(store, 'workbench.action.focusSecondEditorGroup').run()
    expect(store.layout.groups).toHaveLength(2)
    const second = store.layout.groups[1]!
    // The new group opens the active editor once it is there.
    expect(store.takePending(second)?.opening.path).toBe('README.md')
    command(store, 'workbench.action.focusFirstEditorGroup').run()
    expect(store.focused).toBe(1)
    // A third place with two groups splits again; a fifth does nothing.
    command(store, 'workbench.action.focusFifthEditorGroup').run()
    expect(store.layout.groups).toHaveLength(2)
  })

  it('moves the active editor into the next group (closing it here) and joins every group into the first', async () => {
    const store = newGroupStore()
    const one = fakeApi([tab('README.md')])
    const two = fakeApi([tab('src/app.ts')])
    store.join(1, one as unknown as EditorApi, () => one.tabs, () => {})
    const g = store.split(1, 'right')
    store.join(g, two as unknown as EditorApi, () => two.tabs, () => {})
    store.focus(1)
    await command(store, 'workbench.action.moveEditorToNextGroup').run()
    expect(two.open).toHaveBeenCalledWith('README.md')
    expect(one.close).toHaveBeenCalledWith('file:README.md')
    expect(store.focused).toBe(g)
    expect(command(store, 'workbench.action.toggleEditorGroupLayout').enabled!()).toBe(true)
    command(store, 'workbench.action.joinAllGroups').run()
    expect(one.keep).toHaveBeenCalledWith(['src/app.ts'])
    expect(store.layout.groups).toEqual([1])
    expect(command(store, 'workbench.action.joinAllGroups').enabled!()).toBe(false)
  })
})
