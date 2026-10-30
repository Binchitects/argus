import { describe, expect, it } from 'vitest'
import { copyName } from './copy-name'

describe("a copy's name beside its original", () => {
  it('is "name copy.ext", then "name copy 2.ext" and on, as VS Code names it', () => {
    expect(copyName('app.ts', new Set(['app.ts']))).toBe('app copy.ts')
    expect(copyName('app.ts', new Set(['app.ts', 'app copy.ts']))).toBe('app copy 2.ts')
    expect(copyName('app.test.ts', new Set())).toBe('app.test copy.ts')
  })

  it('keeps a dotfile and a folder whole', () => {
    expect(copyName('.env', new Set())).toBe('.env copy')
    expect(copyName('v1.2', new Set(), true)).toBe('v1.2 copy')
    expect(copyName('Makefile', new Set(['Makefile copy']))).toBe('Makefile copy 2')
  })
})
