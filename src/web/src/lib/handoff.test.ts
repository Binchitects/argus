import { afterEach, describe, expect, it, vi } from 'vitest'
import { clearQuote, keepHandoff, parseHandoff, quoteFile, readQuote } from './handoff'

describe('the browser extension’s hand-over', () => {
  afterEach(() => clearQuote())

  it('reads a page or a selection from the fragment, and nothing from an empty one', () => {
    expect(parseHandoff('#title=Release%20notes&url=https%3A%2F%2Fexample.com%2Fr&text=Line%201%0ALine%202&sel=1')).toEqual({
      title: 'Release notes', url: 'https://example.com/r', text: 'Line 1\nLine 2', selection: true,
    })
    expect(parseHandoff('#title=x&text=%20%20')).toBeNull()
    expect(parseHandoff('#text=only')).toEqual({ title: 'A page', url: '', text: 'only', selection: false })
  })

  it('keeps the quote for this tab and takes it out of the address, only on /ask', () => {
    const history = { replaceState: vi.fn() }
    keepHandoff({ pathname: '/chat', hash: '#text=nope' }, history)
    expect(history.replaceState).not.toHaveBeenCalled()
    expect(readQuote()).toBeNull()

    keepHandoff({ pathname: '/ask', hash: '#title=Docs&url=https%3A%2F%2Fdocs.test&text=Hello' }, history)
    expect(history.replaceState).toHaveBeenCalledWith(null, '', '/ask')
    expect(readQuote()).toEqual({ title: 'Docs', url: 'https://docs.test', text: 'Hello', selection: false })
    expect(JSON.parse(sessionStorage.getItem('ask-quote')!)).toMatchObject({ title: 'Docs' })
    clearQuote()
    expect(readQuote()).toBeNull()
  })

  it('makes a Markdown file that says where the words come from', async () => {
    const file = quoteFile({ title: 'Q3: plan / draft', url: 'https://wiki.test/q3', text: 'We grow.', selection: true })
    expect(file.name).toBe('Selection - Q3 plan draft.md')
    expect(file.type).toBe('text/markdown')
    expect(await file.text()).toBe('# Q3: plan / draft\n\nA selection of https://wiki.test/q3\n\nWe grow.\n')
  })
})
