import { describe, expect, it } from 'vitest'
import { regexOf, toLanguageConfiguration } from './language-config'

describe("VS Code's language-configuration.json as Monaco's", () => {
  it('takes comments, brackets and pairs as Monaco does, arrays or objects', () => {
    const c = toLanguageConfiguration({
      comments: { lineComment: '#', blockComment: ['/*', '*/'] },
      brackets: [['{', '}'], ['[', ']'], ['bad']],
      colorizedBracketPairs: [['(', ')']],
      autoClosingPairs: [['{', '}'], { open: '"', close: '"', notIn: ['string', 'comment'] }, { open: "'", close: "'", notIn: 'string' }, 'x'],
      surroundingPairs: [['(', ')'], { open: '<', close: '>', notIn: ['string'] }],
      autoCloseBefore: ';:.,=}])> \n\t',
    })
    expect(c.comments).toEqual({ lineComment: '#', blockComment: ['/*', '*/'] })
    expect(c.brackets).toEqual([['{', '}'], ['[', ']']])
    expect(c.colorizedBracketPairs).toEqual([['(', ')']])
    expect(c.autoClosingPairs).toEqual([{ open: '{', close: '}' }, { open: '"', close: '"', notIn: ['string', 'comment'] }, { open: "'", close: "'", notIn: ['string'] }])
    // Surrounding pairs have no notIn in Monaco.
    expect(c.surroundingPairs).toEqual([{ open: '(', close: ')' }, { open: '<', close: '>' }])
    expect(c.autoCloseBefore).toBe(';:.,=}])> \n\t')
    // VS Code's newer form of a line comment: {comment, noIndent}.
    expect(toLanguageConfiguration({ comments: { lineComment: { comment: '//', noIndent: true } } }).comments).toEqual({ lineComment: '//' })
  })

  it('turns its patterns into regular expressions: texts, {pattern, flags}; one JavaScript refuses is left out', () => {
    const c = toLanguageConfiguration({
      wordPattern: '(-?\\d*\\.\\d\\w*)|([^\\s]+)',
      folding: { offSide: true, markers: { start: '^\\s*#region\\b', end: { pattern: '^\\s*#endregion\\b', flags: 'i' } } },
      indentationRules: { increaseIndentPattern: '^.*:\\s*$', decreaseIndentPattern: { pattern: '^\\s*(else|elif)\\b', flags: 'u' }, indentNextLinePattern: '(?<bad' },
    })
    expect(c.wordPattern).toEqual(/(-?\d*\.\d\w*)|([^\s]+)/)
    expect(c.folding).toEqual({ offSide: true, markers: { start: /^\s*#region\b/, end: /^\s*#endregion\b/i } })
    expect(c.indentationRules).toEqual({ increaseIndentPattern: /^.*:\s*$/, decreaseIndentPattern: /^\s*(else|elif)\b/u })
    expect(regexOf('(?i)oniguruma')).toBeNull()
    expect(regexOf({ pattern: 'a', flags: 'gx' })).toEqual(/a/g)
    expect(regexOf(5)).toBeNull()
  })

  it('takes Enter rules with their indent by name, and leaves out one it cannot read', () => {
    const c = toLanguageConfiguration({
      onEnterRules: [
        { beforeText: '^\\s*/\\*\\*(?!/)([^*]|\\*(?!/))*$', afterText: '^\\s*\\*/$', action: { indent: 'indentOutdent', appendText: ' * ' } },
        { beforeText: '^\\s*\\*', previousLineText: '^\\s*/\\*\\*', action: { indent: 'none', appendText: '* ', removeText: 1 } },
        { beforeText: ':\\s*$', action: { indent: 'indent' } },
        { beforeText: 'x', action: { indent: 'sideways' } },
        { action: { indent: 'none' } },
      ],
    })
    expect(c.onEnterRules).toEqual([
      { beforeText: /^\s*\/\*\*(?!\/)([^*]|\*(?!\/))*$/, afterText: /^\s*\*\/$/, action: { indentAction: 2, appendText: ' * ' } },
      { beforeText: /^\s*\*/, previousLineText: /^\s*\/\*\*/, action: { indentAction: 0, appendText: '* ', removeText: 1 } },
      { beforeText: /:\s*$/, action: { indentAction: 1 } },
    ])
    expect(toLanguageConfiguration(null)).toEqual({})
    expect(toLanguageConfiguration([1])).toEqual({})
  })
})
