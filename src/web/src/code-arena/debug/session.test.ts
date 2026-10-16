import { afterEach, describe, expect, it } from 'vitest'
import { escapeMarkdown, fenced } from './editor-debug'
import { debug } from './session'

afterEach(() => {
  debug.breakpoints = {}
})

describe('breakpoints', () => {
  it('keep one per line: lines deleted bring theirs together, the first kept with what the others had', () => {
    debug.setBreakpoints('src/app.ts', [
      { line: 6, enabled: true, logMessage: 'at six' },
      { line: 5, enabled: true, condition: 'i > 3' },
      { line: 5, enabled: false, condition: 'never', hitCondition: '2' },
    ])
    expect(debug.breakpoints['src/app.ts']).toEqual([
      { line: 5, enabled: true, condition: 'i > 3', hitCondition: '2' },
      { line: 6, enabled: true, logMessage: 'at six' },
    ])
    // Lines 5 and 6 deleted: both on line 5 now.
    debug.moveBreakpoints('src/app.ts', new Map([[6, 5]]))
    expect(debug.breakpoints['src/app.ts']).toEqual([{ line: 5, enabled: true, condition: 'i > 3', hitCondition: '2', logMessage: 'at six' }])
  })

  it("loads without Run to Cursor's: the one it stood in for comes back where it is now", () => {
    debug.load({ breakpoints: { 'a.c': [{ line: 9, enabled: true, temporary: true, was: { line: 7, enabled: false, condition: 'x' } }, { line: 3, enabled: true, temporary: true }] } })
    expect(debug.breakpoints).toEqual({ 'a.c': [{ line: 9, enabled: false, condition: 'x' }] })
  })
})

describe('the debug hover', () => {
  it("fences a value longer than any run of backticks in it, so the program's text cannot make Markdown", () => {
    const value = 'x\n```\n# Session expired\n[Sign in](https://evil.example)'
    expect(fenced(value)).toBe('````\n' + value + '\n````')
    expect(fenced('plain')).toBe('```\nplain\n```')
    expect(escapeMarkdown('[a](b) *c* #d')).toBe('\\[a\\]\\(b\\) \\*c\\* \\#d')
  })
})
