import { describe, expect, it } from 'vitest'
import { diffLines, diffRows, diffStats, linesOf } from './canvas-diff'
import { locate, selectionMessage, selectionOf } from './canvas-selection'

const kinds = (text: string, other: string) => diffLines(text, other).map((l) => `${l.kind === 'same' ? ' ' : l.kind === 'added' ? '+' : '-'}${l.text}`)

describe('canvas line diff', () => {
  it('finds the one line that changed, and leaves the rest the same', () => {
    expect(kinds('a\nb\nc\n', 'a\nB\nc\n')).toEqual([' a', '-b', '+B', ' c'])
    const d = diffLines('# T\n\nGoals\n\nRisks\n', '# T\n\nGoals\n\nRisks, named\n')
    expect(d.filter((l) => l.kind !== 'same').map((l) => [l.kind, l.old, l.new])).toEqual([['removed', 5, null], ['added', null, 5]])
  })

  it('finds lines added and removed in the middle, with their numbers', () => {
    expect(kinds('a\nb\nc\nd', 'a\nc\nx\nd')).toEqual([' a', '-b', ' c', '+x', ' d'])
    expect(kinds('', 'one\ntwo')).toEqual(['+one', '+two'])
    expect(kinds('one\ntwo', '')).toEqual(['-one', '-two'])
    expect(diffStats(diffLines('a\nb\nc', 'a\nc\nd\ne'))).toEqual({ added: 2, removed: 1 })
  })

  it('is the shortest edit even when lines repeat', () => {
    const before = 'x\ny\nx\ny\nx'
    const after = 'y\nx\ny\nx\ny'
    const d = diffLines(before, after)
    expect(diffStats(d).added + diffStats(d).removed).toBe(2)
    // Applying it gives the new text back.
    expect(d.filter((l) => l.kind !== 'removed').map((l) => l.text)).toEqual(linesOf(after))
    expect(d.filter((l) => l.kind !== 'added').map((l) => l.text)).toEqual(linesOf(before))
  })

  it('folds unchanged lines away from the changes', () => {
    const before = Array.from({ length: 30 }, (_, i) => `line ${i + 1}`).join('\n')
    const after = before.replace('line 15', 'line fifteen')
    const rows = diffRows(diffLines(before, after))
    expect(rows[0]).toEqual({ kind: 'skip', count: 11 })
    expect(rows.at(-1)).toEqual({ kind: 'skip', count: 12 })
    expect(rows.filter((r) => r.kind === 'same')).toHaveLength(6)
  })
})

describe('canvas selections', () => {
  const text = '# Plan\n\nShip by March.\nTwo people.\n\n## Risks\n'

  it('knows the lines a selection spans', () => {
    const start = text.indexOf('Ship')
    expect(selectionOf(text, start, text.indexOf('\n\n## Risks'))).toMatchObject({ from: 3, to: 4, text: 'Ship by March.\nTwo people.' })
    // Ending just after a line break, it ends on the line before; backwards is the same.
    expect(selectionOf(text, text.indexOf('Two people.\n') + 12, start)).toMatchObject({ from: 3, to: 4 })
    expect(selectionOf(text, 3, 3)).toBeNull()
    expect(selectionOf(text, 6, 8)).toBeNull()
  })

  it('finds text selected in the preview in the source, its marks left out', () => {
    expect(locate(text, 'Two people.')).toMatchObject({ from: 4, to: 4 })
    expect(locate(text, 'Plan\n\nShip by March.')).toMatchObject({ from: 1, to: 3 })
    expect(locate(text, 'Nowhere')).toBeNull()
  })

  it('quotes a selection with its canvas id and lines, and asks for canvas_edit on that part', () => {
    const sel = selectionOf(text, text.indexOf('Ship'), text.indexOf('\n\n## Risks'))!
    const message = selectionMessage({ id: 'k1', title: 'Plan', kind: 'document', language: null }, sel, 'Make this shorter.')
    expect(message).toBe(
      'About lines 3–4 of the canvas “Plan” (id k1):\n\n> Ship by March.\n> Two people.\n\nMake this shorter. If that needs a change, change only this part, with canvas_edit.',
    )
    // Code is quoted as code, in its language.
    const code = 'def f():\n    return 1\n'
    const quoted = selectionMessage({ id: 'k2', title: 'f.py', kind: 'code', language: 'python' }, selectionOf(code, 0, code.length)!, 'Why?')
    expect(quoted).toContain('About lines 1–2 of the canvas “f.py” (id k2):\n\n```python\ndef f():\n    return 1\n```')
  })
})
