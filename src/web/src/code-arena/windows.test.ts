import { describe, expect, it } from 'vitest'
import { detachedView, windowUrl } from './windows'

describe("the IDE's windows", () => {
  it('reads what a window is from its address', () => {
    expect(detachedView('')).toBeNull()
    expect(detachedView('?window=chat')).toEqual({ kind: 'chat' })
    expect(detachedView('?window=editor&path=src%2Fapp.ts&id=w1')).toEqual({ kind: 'editor', path: 'src/app.ts', id: 'w1' })
    // An editor with no file is the IDE.
    expect(detachedView('?window=editor')).toBeNull()
  })

  it("keeps the IDE's own address (a Code Studio prefix, its query) and puts the window's part in", () => {
    const url = new URL(windowUrl({ kind: 'editor', path: 'src/a b.ts', id: 'w2' }, 'https://arena.example/api/studio/w/7/?theme=dark#x'))
    expect(url.pathname).toBe('/api/studio/w/7/')
    expect(url.searchParams.get('theme')).toBe('dark')
    expect(url.searchParams.get('path')).toBe('src/a b.ts')
    expect(url.hash).toBe('')
    // The chat's has no file of an editor's left in it.
    const chat = new URL(windowUrl({ kind: 'chat' }, url.toString()))
    expect(chat.searchParams.get('window')).toBe('chat')
    expect(chat.searchParams.has('path')).toBe(false)
  })
})
