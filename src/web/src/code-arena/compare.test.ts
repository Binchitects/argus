import { describe, expect, it } from 'vitest'
import { revertChanges, takeChanges, touched, type LineChange } from './compare'

const change = (os: number, oe: number, ms: number, me: number): LineChange => ({ originalStartLineNumber: os, originalEndLineNumber: oe, modifiedStartLineNumber: ms, modifiedEndLineNumber: me })

describe('one change of a compare', () => {
  // Left: a b c d (and a final newline); right: a B c d e, with "x" gone after a: three changes.
  const left = 'a\nb\nc\nd\n'
  const right = 'a\nB\nc\nd\ne\n'
  const changes = [change(2, 2, 2, 2), change(4, 0, 5, 5)]

  it('takes the changes chosen from the right side, and only those', () => {
    expect(takeChanges(left, right, [changes[0]!])).toBe('a\nB\nc\nd\n')
    expect(takeChanges(left, right, [changes[1]!])).toBe('a\nb\nc\nd\ne\n')
    expect(takeChanges(left, right, changes)).toBe(right)
    // A deletion: the right side has no lines there.
    expect(takeChanges('a\nx\nb\n', 'a\nb\n', [change(2, 2, 1, 0)])).toBe('a\nb\n')
    // An insertion at the very start, and a file without a final newline.
    expect(takeChanges('b', 'a\nb', [change(0, 0, 1, 1)])).toBe('a\nb')
  })

  it('puts the changes chosen back as the left side has them', () => {
    expect(revertChanges(left, right, [changes[0]!])).toBe('a\nb\nc\nd\ne\n')
    expect(revertChanges(left, right, changes)).toBe(left)
    expect(revertChanges('a\nx\nb\n', 'a\nb\n', [change(2, 2, 1, 0)])).toBe('a\nx\nb\n')
    // CRLF lines are kept as they are.
    expect(takeChanges('a\r\nb\r\n', 'a\r\nB\r\n', [change(2, 2, 2, 2)])).toBe('a\r\nB\r\n')
  })

  it('finds the changes the selection or the cursor touches', () => {
    expect(touched(changes, 2, 2)).toEqual([changes[0]])
    expect(touched(changes, 3, 3)).toEqual([])
    expect(touched(changes, 1, 5)).toEqual(changes)
    // A deletion is touched from the line it sits after, and the next.
    expect(touched([change(2, 2, 1, 0)], 1, 1)).toHaveLength(1)
    expect(touched([change(2, 2, 1, 0)], 2, 2)).toHaveLength(1)
    expect(touched([change(2, 2, 1, 0)], 3, 3)).toHaveLength(0)
  })
})
