import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from './http'
import { openFolder, pickFolder, type FolderList } from './folders'
import { quickPick, type PickItem } from './quick-pick'

vi.mock('./http', () => ({ api: vi.fn(), ApiError: class ApiError extends Error {} }))
vi.mock('./quick-pick', () => ({ quickPick: vi.fn() }))
vi.mock('@/components/ui/toaster', () => ({ toast: { info: vi.fn(), error: vi.fn() } }))

const list = (path: string, folders: string[], extra: Partial<FolderList> = {}): FolderList => ({
  path, parent: path === '/' ? null : path.slice(0, path.lastIndexOf('/')) || '/', folders, why: null, current: '/home/me/shop', home: '/home/me', separator: '/', drives: [], recent: ['/home/me/shop'], added: [], ...extra,
})

describe('Open Folder', () => {
  beforeEach(() => {
    vi.mocked(api).mockReset()
  })

  it('goes into folders and takes the one shown, a typed path from the folder shown', async () => {
    const listings: Record<string, FolderList> = { '': list('/home/me', ['shop', 'notes']), '/home/me/notes': list('/home/me/notes', ['2026']), '/home/me/notes/2026': list('/home/me/notes/2026', []) }
    vi.mocked(api).mockImplementation(async (path: string) => listings[new URLSearchParams(path.split('?')[1] ?? '').get('path') ?? '']!)
    const picks: PickItem<{ kind: string; path: string }>[][] = []
    const answers = [(items: PickItem<{ kind: string; path: string }>[]) => items.find((i) => i.label === 'notes'), (_: unknown, free: (t: string) => PickItem | null) => free('2026'), (items: PickItem<{ kind: string; path: string }>[]) => items[0]]
    vi.mocked(quickPick).mockImplementation(async (o) => {
      picks.push(o.items as PickItem<{ kind: string; path: string }>[])
      return answers[picks.length - 1]!(o.items as PickItem<{ kind: string; path: string }>[], o.free as (t: string) => PickItem | null) as never
    })
    expect(await pickFolder('Open Folder', 'Open')).toBe('/home/me/notes/2026')
    // The folder shown first, its parent, its folders, then the recent ones.
    expect(picks[0]!.map((i) => i.label)).toEqual(['Open me', '..', 'shop', 'notes', '/home/me/shop'])
    expect(picks[0]![4]!.group).toBe('recently opened')
    expect(picks[2]![0]!.value).toEqual({ kind: 'take', path: '/home/me/notes/2026' })
  })

  it('opens it, and loads the page once code-arena answers for that folder', async () => {
    vi.useFakeTimers()
    const reload = vi.fn()
    vi.stubGlobal('location', { ...window.location, reload })
    let current = '/home/me/shop'
    vi.mocked(api).mockImplementation(async (path: string) => {
      if (path === '/api/folders/open') {
        setTimeout(() => (current = '/home/me/notes'), 1000)
        return { folder: '/home/me/notes' }
      }
      return list('/home/me', [], { current })
    })
    const opening = vi.fn()
    const done = openFolder('../notes', opening)
    await vi.advanceTimersByTimeAsync(600)
    expect(opening).toHaveBeenCalledWith('/home/me/notes')
    expect(reload).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(1500)
    await done
    expect(reload).toHaveBeenCalledOnce()
    expect(vi.mocked(api)).toHaveBeenCalledWith('/api/folders/open', { body: { path: '../notes' } })
    vi.useRealTimers()
    vi.unstubAllGlobals()
  })
})

describe('@ with a whole path', () => {
  it('lists the folder typed so far and the start of a name in it, for /, ~/ and a drive only', async () => {
    const { outsidePath } = await import('./mention')
    expect(outsidePath('/home/me/no')).toEqual({ folder: '/home/me/', start: 'no' })
    expect(outsidePath('~/')).toEqual({ folder: '~/', start: '' })
    expect(outsidePath('C:\\Users\\me\\Doc')).toEqual({ folder: 'C:/Users/me/', start: 'Doc' })
    expect(outsidePath('src/app.ts')).toBeNull()
    expect(outsidePath('~')).toBeNull()
    expect(outsidePath(null)).toBeNull()
  })
})
