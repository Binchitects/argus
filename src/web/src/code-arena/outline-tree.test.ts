import { describe, expect, it } from 'vitest'
import type { OutlineSymbol } from './ide-api'
import { innermost, nest } from './outline-tree'

const s = (qualified: string, start: number, end: number, parent: string | null = null): OutlineSymbol => ({
  name: qualified.split('.').at(-1)!, kind: 'method', qualified, parent, start, end, signature: qualified,
})

describe('the Outline', () => {
  it('nests symbols under what holds them, in order, and finds the one a line is in', () => {
    const tree = nest([s('Shop.Cart.Total', 5, 7, 'Shop.Cart'), s('Shop.Cart', 3, 12, 'Shop'), s('Shop', 1, 20), s('Shop.Cart.Add', 9, 11, 'Shop.Cart'), s('Shop.Pay', 14, 18, 'Shop')])
    expect(tree.map((n) => n.symbol.name)).toEqual(['Shop'])
    expect(tree[0]!.children.map((n) => n.symbol.name)).toEqual(['Cart', 'Pay'])
    expect(tree[0]!.children[0]!.children.map((n) => n.symbol.name)).toEqual(['Total', 'Add'])
    expect(innermost(tree, 10)?.qualified).toBe('Shop.Cart.Add')
    expect(innermost(tree, 13)?.qualified).toBe('Shop')
    expect(innermost(tree, 30)).toBeNull()
  })

  it('keeps a symbol whose holder is not in the file at the top', () => {
    expect(nest([s('A.b', 2, 3, 'A')]).map((n) => n.symbol.qualified)).toEqual(['A.b'])
  })
})
