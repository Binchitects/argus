import { describe, expect, it } from 'vitest'
import { conflictParts, findConflicts, resolveAll, resolveText, sideText, takenText } from './merge-conflicts'
import { conflictNear } from './merge-inline'

const file = ['top', '<<<<<<< HEAD', 'ours 1', 'ours 2', '=======', 'theirs', '>>>>>>> feature', 'middle', '<<<<<<< HEAD', 'a', '||||||| base', 'o', '=======', 'b', '>>>>>>> feature', 'end', ''].join('\n')

describe('merge conflicts', () => {
  it('finds each conflict: its markers, its parts, the common ancestor with diff3, and the labels', () => {
    const [one, two] = findConflicts(file)
    expect(one).toMatchObject({ start: 2, middle: 5, end: 7, baseMarker: null, current: { start: 3, end: 5 }, incoming: { start: 6, end: 7 }, base: null, currentLabel: 'HEAD', incomingLabel: 'feature' })
    expect(two).toMatchObject({ start: 9, baseMarker: 11, middle: 13, end: 15, current: { start: 10, end: 11 }, base: { start: 12, end: 13 }, incoming: { start: 14, end: 15 } })
    // Markers that do not close, or a line that only starts like one, are no conflict.
    expect(findConflicts('<<<<<<< HEAD\na\n=======\nb\n')).toEqual([])
    expect(findConflicts('a\n<<<<<<<< not\n')).toEqual([])
    expect(findConflicts('x = 1')).toEqual([])
  })

  it('resolves a conflict to a side, both (current first), the ancestor or nothing, the rest as it was', () => {
    const [one, two] = findConflicts(file)
    expect(resolveText(file, one!, 'current')).toBe(file.replace('<<<<<<< HEAD\nours 1\nours 2\n=======\ntheirs\n>>>>>>> feature\n', 'ours 1\nours 2\n'))
    expect(resolveText(file, one!, 'incoming').split('\n').slice(0, 3)).toEqual(['top', 'theirs', 'middle'])
    expect(resolveText(file, one!, 'both').split('\n').slice(0, 5)).toEqual(['top', 'ours 1', 'ours 2', 'theirs', 'middle'])
    expect(resolveText(file, two!, 'base').split('\n').slice(-3)).toEqual(['o', 'end', ''])
    expect(resolveText(file, two!, 'none').split('\n').slice(-3)).toEqual(['middle', 'end', ''])
    expect(resolveAll(file, 'incoming')).toBe('top\ntheirs\nmiddle\nb\nend\n')
    expect(findConflicts(resolveAll(file, 'both'))).toEqual([])
  })

  it('keeps the line endings: CRLF stays, and a conflict at the very end leaves no line break the file had not', () => {
    const crlf = 'a\r\n<<<<<<< HEAD\r\nx\r\n=======\r\ny\r\n>>>>>>> b\r\nz\r\n'
    expect(resolveAll(crlf, 'both')).toBe('a\r\nx\r\ny\r\nz\r\n')
    const atEnd = 'a\n<<<<<<< HEAD\nx\n=======\ny\n>>>>>>> b'
    expect(resolveAll(atEnd, 'incoming')).toBe('a\ny')
  })

  it("makes each side's text with where each conflict's part is in it", () => {
    expect(sideText(file, 'current')).toEqual({ text: 'top\nours 1\nours 2\nmiddle\na\nend\n', ranges: [{ start: 2, end: 4 }, { start: 5, end: 6 }] })
    expect(sideText(file, 'incoming')).toEqual({ text: 'top\ntheirs\nmiddle\nb\nend\n', ranges: [{ start: 2, end: 3 }, { start: 4, end: 5 }] })
  })

  it('goes to the conflict at the cursor, the next and the previous, round the file', () => {
    const conflicts = findConflicts(file)
    expect(conflictNear(conflicts, 4, 'at')?.start).toBe(2)
    expect(conflictNear(conflicts, 8, 'at')).toBeUndefined()
    expect(conflictNear(conflicts, 8, 'next')?.start).toBe(9)
    expect(conflictNear(conflicts, 16, 'next')?.start).toBe(2)
    expect(conflictNear(conflicts, 8, 'previous')?.start).toBe(2)
    expect(conflictNear(conflicts, 1, 'previous')?.start).toBe(9)
  })
})

describe('the merge editor', () => {
  it('puts in the result what is taken of a conflict: one side, both, or the conflict back when neither', () => {
    const [parts] = conflictParts(file, findConflicts(file))
    expect(parts).toEqual({ current: 'ours 1\nours 2\n', incoming: 'theirs\n', whole: '<<<<<<< HEAD\nours 1\nours 2\n=======\ntheirs\n>>>>>>> feature\n' })
    expect(takenText(parts!, { current: true, incoming: false })).toBe('ours 1\nours 2\n')
    expect(takenText(parts!, { current: false, incoming: true })).toBe('theirs\n')
    expect(takenText(parts!, { current: true, incoming: true })).toBe('ours 1\nours 2\ntheirs\n')
    expect(takenText(parts!, { current: false, incoming: false })).toBe(parts!.whole)
  })
})
