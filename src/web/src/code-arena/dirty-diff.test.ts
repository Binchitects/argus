import { describe, expect, it } from 'vitest'
import { dirtyDecoration, lineChanges } from './dirty-diff'

describe('quick diff', () => {
  it('marks lines added, changed and the place of lines taken away', () => {
    expect(lineChanges('a\nb\nc\n', 'a\nb\nc\n')).toEqual([])
    expect(lineChanges('a\nb\nc\n', 'a\nx\nb\nc\n')).toEqual([{ kind: 'added', start: 2, end: 2 }])
    expect(lineChanges('a\nb\nc\n', 'a\nB\nc\n')).toEqual([{ kind: 'modified', start: 2, end: 2 }])
    expect(lineChanges('a\nb\nc\n', 'a\nc\n')).toEqual([{ kind: 'deleted', start: 1, end: 1 }])
    expect(lineChanges('a\nb\n', 'b\n')).toEqual([{ kind: 'deleted', start: 0, end: 0 }])
    expect(lineChanges('', 'a\nb')).toEqual([{ kind: 'modified', start: 1, end: 2 }])
    expect(lineChanges('a\nb\nc\nd\ne\n', 'a\nB\nc\nd\nE\nf\n')).toEqual([
      { kind: 'modified', start: 2, end: 2 },
      { kind: 'modified', start: 5, end: 6 },
    ])
  })

  it('marks as few lines as a shortest diff does, whatever the texts', () => {
    // Random texts of a few letters: the lines marked added or changed are as many as a longest common subsequence leaves.
    let seed = 7
    const rand = (n: number) => {
      seed = (seed * 1103515245 + 12345) & 0x7fffffff
      return seed % n
    }
    for (let round = 0; round < 300; round++) {
      const a = Array.from({ length: rand(12) }, () => 'abcd'[rand(4)]).join('\n')
      const b = Array.from({ length: rand(12) }, () => 'abcd'[rand(4)]).join('\n')
      const al = a.split('\n')
      const bl = b.split('\n')
      const dp = Array.from({ length: al.length + 1 }, () => new Array<number>(bl.length + 1).fill(0))
      for (let i = al.length - 1; i >= 0; i--) for (let j = bl.length - 1; j >= 0; j--) dp[i]![j] = al[i] === bl[j] ? dp[i + 1]![j + 1]! + 1 : Math.max(dp[i + 1]![j]!, dp[i]![j + 1]!)
      const marked = lineChanges(a, b)
        .filter((c) => c.kind !== 'deleted')
        .reduce((n, c) => n + c.end - c.start + 1, 0)
      expect(marked, `${JSON.stringify(a)} → ${JSON.stringify(b)}`).toBe(bl.length - dp[0]![0]!)
      // Every change is inside the new text.
      for (const c of lineChanges(a, b)) expect(c.end).toBeLessThanOrEqual(bl.length)
    }
  })

  it('draws the changes where "scm.diffDecorations" says: beside the line numbers, in the scroll bar, in the minimap', () => {
    const lanes = { overview: 1, minimap: 2 }
    const at = (mode: Parameters<typeof dirtyDecoration>[1]) => {
      const d = dirtyDecoration('added', mode, lanes)
      return [!!d.linesDecorationsClassName, !!d.overviewRuler, !!d.minimap]
    }
    expect(at('all')).toEqual([true, true, true])
    expect(at('gutter')).toEqual([true, false, false])
    expect(at('overview')).toEqual([false, true, false])
    expect(at('minimap')).toEqual([false, false, true])
    expect(dirtyDecoration('deleted', 'all', lanes)).toMatchObject({ linesDecorationsClassName: 'arena-dirty-deleted', overviewRuler: { color: '#e5140099', position: 1 }, minimap: { color: '#e5140099', position: 2 } })
  })

  it('compares lines without their ends: a CRLF file against an LF index is unchanged', () => {
    // core.autocrlf: the index has LF, the file on disk CRLF.
    expect(lineChanges('a\nb\nc\n', 'a\r\nb\r\nc\r\n')).toEqual([])
    expect(lineChanges('a\nb\nc\n', 'a\r\nB\r\nc\r\n')).toEqual([{ kind: 'modified', start: 2, end: 2 }])
    expect(lineChanges('a\r\nb\r\n', 'a\nb\nc\n')).toEqual([{ kind: 'added', start: 3, end: 3 }])
  })

  it('gives up on a rewrite too large to diff as it is typed: the lines from the first to the last change are one change', () => {
    // Every other line of thousands changed: the shortest diff is thousands of edits long, too slow and too large to find
    // at each key.
    const a = Array.from({ length: 5000 }, (_, i) => `line ${i}`)
    const b = a.map((l, i) => (i % 2 ? `${l} changed` : l))
    const started = performance.now()
    expect(lineChanges(a.join('\n'), b.join('\n'))).toEqual([{ kind: 'modified', start: 2, end: 5000 }])
    expect(performance.now() - started).toBeLessThan(1000)
    // Lines only added, or only taken away, are found at once however many.
    const added = [...a.slice(0, 10), ...Array.from({ length: 6000 }, (_, i) => `new ${i}`), ...a.slice(10)]
    expect(lineChanges(a.join('\n'), added.join('\n'))).toEqual([{ kind: 'added', start: 11, end: 6010 }])
    expect(lineChanges(added.join('\n'), a.join('\n'))).toEqual([{ kind: 'deleted', start: 10, end: 10 }])
  })
})
