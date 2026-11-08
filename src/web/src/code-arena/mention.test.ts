import { describe, expect, it } from 'vitest'
import { outsidePath, withFolders } from './mention'

describe('@ in the message box', () => {
  it('matches every file and folder, not a handful', () => {
    const files = Array.from({ length: 300 }, (_, i) => `src/orders/order${i}.ts`).concat(['src/cart/cart.ts', 'README.md'])
    const all = withFolders(files, 'order')
    expect(all).toHaveLength(301)
    expect(all).toContain('src/orders/')
    expect(withFolders(files, 'cart').sort()).toEqual(['src/cart/', 'src/cart/cart.ts'])
    // Nothing typed yet: the whole project, folders first.
    expect(withFolders(files, '')).toHaveLength(305)
    expect(withFolders(files, '').slice(0, 3)).toEqual(['src/', 'src/orders/', 'src/cart/'])
  })

  it('a whole path is a folder outside the project', () => {
    expect(outsidePath('~/notes/to')).toEqual({ folder: '~/notes/', start: 'to' })
    expect(outsidePath('src/app')).toBeNull()
  })
})
