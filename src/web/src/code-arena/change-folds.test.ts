import { describe, expect, it } from 'vitest'
import { folderKeys, folderTree, type Folder } from './change-folds'

const paths = ['src/app.ts', 'README.md', 'src/lib/tax.ts', 'tests/unit/cart.test.ts', 'src/lib/cart.ts', 'docs/a/b/c.md', 'docs/a/d.md']
const shape = (f: Folder<string>): unknown => ({ [f.name || '.']: [...f.folders.map(shape), ...f.files], count: f.count })

describe('changed files under their folders', () => {
  it('puts folders first, each by name, and counts the files under each', () => {
    const tree = folderTree(paths, (p) => p, false)
    expect(shape(tree)).toEqual({
      '.': [
        { docs: [{ a: [{ b: ['docs/a/b/c.md'], count: 1 }, 'docs/a/d.md'], count: 2 }], count: 2 },
        { src: [{ lib: ['src/lib/cart.ts', 'src/lib/tax.ts'], count: 2 }, 'src/app.ts'], count: 3 },
        { tests: [{ unit: ['tests/unit/cart.test.ts'], count: 1 }], count: 1 },
        'README.md',
      ],
      count: 7,
    })
  })

  it('compacts a folder with nothing in it but one folder into one row, as VS Code does', () => {
    const tree = folderTree(paths, (p) => p)
    expect(tree.folders.map((f) => [f.name, f.path])).toEqual([['docs/a', 'docs/a'], ['src', 'src'], ['tests/unit', 'tests/unit']])
  })

  it('has a key for every folder the tree draws, with compact folders and without', () => {
    const drawn = (f: Folder<string>): string[] => f.folders.flatMap((d) => [`changes:${d.path}`, ...drawn(d)])
    const keys = folderKeys('changes', paths, (p) => p)
    expect(keys).toEqual(['changes:src', 'changes:src/lib', 'changes:tests', 'changes:tests/unit', 'changes:docs', 'changes:docs/a', 'changes:docs/a/b'])
    for (const compact of [true, false]) expect(keys).toEqual(expect.arrayContaining(drawn(folderTree(paths, (p) => p, compact))))
  })
})
