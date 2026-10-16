import { describe, expect, it } from 'vitest'
import { blank } from './live'
import { markdownElement } from './markdown-blocks'
import { findQuoted, plainOf, QuoteChars, quoteForDisplay, quoteOf, quoteOfRange } from './quote'
import type { Message } from './types'

/** An answer's message as the page draws it, marked as a source of quotes. */
function answer(id: string, md: string) {
  const host = document.createElement('div')
  host.dataset.quoteSource = id
  host.append(...markdownElement(md).childNodes)
  document.body.append(host)
  return host
}

/** A range from the start of `from` to the end of `to`, each the first text node holding it. */
function rangeOf(root: Node, from: string, to: string) {
  const texts: Text[] = []
  const walk = document.createTreeWalker(root, NodeFilter.SHOW_TEXT)
  for (let n = walk.nextNode(); n; n = walk.nextNode()) texts.push(n as Text)
  const start = texts.find((t) => t.data.includes(from))!
  const end = texts.find((t, i) => i >= texts.indexOf(start) && t.data.includes(to))!
  const r = document.createRange()
  r.setStart(start, start.data.indexOf(from))
  r.setEnd(end, end.data.indexOf(to) + to.length)
  return r
}

describe('quotes', () => {
  it('the words leave out hidden parts, buttons and the screen-reader copy of formulas, and a formula becomes its TeX', () => {
    const box = document.createElement('div')
    box.innerHTML = '<p>Area is $A$ <span aria-hidden="true">(hidden)</span><button>Copy</button><span hidden>gone</span> here.</p><p>Next</p>'
    const katex = markdownElement('Area is $\\pi r^2$ exactly.')
    expect(plainOf(katex).trim()).toBe('Area is $\\pi r^2$ exactly.')
    expect(plainOf(box).trim()).toBe('Area is $A$  here.\nNext')
  })

  it('a selection is quoted as the Markdown it came from when found, else as the words shown', () => {
    const md = 'Run it:\n\n```sh\nmake all\n```\n\nThen **push** it to `main`.'
    const host = answer('a1', md)
    // One paragraph's words: found in the Markdown as they are.
    expect(quoteOfRange(rangeOf(host, 'Run', 'it:'), () => md)?.text).toBe('Run it:')
    // From the first line to the last: the Markdown between them, its marks kept.
    const both = quoteOfRange(rangeOf(host, 'Run', 'Then'), () => 'Run it: then.\n\nThen go')
    expect(both?.text).toBe('Run it: then.\n\nThen')
    // Formatting the words do not have: the words as shown.
    const shown = quoteOfRange(rangeOf(host, 'push', 'main'), () => md)
    expect(shown?.text).toBe('push it to main')
    expect(shown?.words).toBe('push it to main')
    // Not in an answer: nothing to quote.
    const outside = document.createElement('p')
    outside.textContent = 'a question'
    document.body.append(outside)
    expect(quoteOfRange(rangeOf(outside, 'a', 'question'), () => md)).toBeNull()
  })

  it('a match much longer than the selection is not taken: the words are', () => {
    const host = answer('a2', 'Alpha one.\n\nOmega.')
    const source = 'Alpha one.' + '\n\nfiller'.repeat(200) + '\n\nOmega.'
    const q = quoteOfRange(rangeOf(host, 'Alpha', 'Omega'), () => source)
    expect(q?.text).toBe('Alpha one.\n\nOmega')
  })

  it('a quote is cut at 4,000 characters with an ellipsis, its line endings tidied', () => {
    const long = quoteOf('x'.repeat(QuoteChars + 10))!
    expect(long.cut).toBe(true)
    expect(long.text).toHaveLength(QuoteChars)
    expect(long.text.endsWith('…')).toBe(true)
    expect(quoteOf('a  \r\nb\r\n\r\n\r\n\r\nc')!.text).toBe('a\nb\n\nc')
    expect(quoteOf('  \n ')).toBeNull()
  })

  it('a quote shows as plain text, and is found in the nearest answer before its question', () => {
    expect(quoteForDisplay('Use **git** `rebase`')).toBe('Use git rebase')
    const m = (id: string, role: Message['role'], content: string): Message => ({ ...blank(id, role, null), content })
    const path = [m('q1', 'user', 'a'), m('a1', 'assistant', 'Use **git** here'), m('q2', 'user', 'b'), m('a2', 'assistant', 'Use **git** here too'), m('q3', 'user', 'c')]
    expect(findQuoted(path, 'q3', 'Use **git** here')).toBe('a2')
    expect(findQuoted(path, 'q2', 'Use git here')).toBe('a1')
    expect(findQuoted(path, 'q3', 'never said')).toBeNull()
  })
})
