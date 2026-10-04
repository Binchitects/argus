import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { clearQuote } from '@/lib/handoff'
import { fakeApi, member, renderApp } from '@/test/utils'

const uploaded = vi.hoisted(() => ({ files: [] as File[] }))
vi.mock('@/pages/chat/api', async (original) => ({
  ...(await original<typeof import('@/pages/chat/api')>()),
  // XMLHttpRequest uploads are not fetch: the file is kept here instead.
  uploadFile: async (file: File) => {
    uploaded.files.push(file)
    return { id: 'att-1', fileName: file.name, contentType: file.type, size: file.size, kind: 'text', truncated: false }
  },
}))

describe('asking about a page from the browser extension', () => {
  afterEach(() => clearQuote())

  it('starts a chat with the page attached and the question asked, then opens it', async () => {
    sessionStorage.setItem('ask-quote', JSON.stringify({ title: 'Release notes', url: 'https://example.test/notes', text: 'Version 2 adds bots.', selection: false }))
    const calls = fakeApi(member, {
      'POST /api/chat/conversations': () => ({ json: { id: 'c-new', title: 'New chat' } }),
      'POST /api/chat/conversations/c-new/messages': () => ({ events: [{ type: 'question', id: 'q1', parentId: null }], hang: true }),
    })
    const { router } = renderApp('/ask')
    expect(await screen.findByRole('heading', { name: 'Ask about this page' })).toBeInTheDocument()
    expect(screen.getByLabelText('What you are asking about')).toHaveTextContent('Version 2 adds bots.')
    await userEvent.click(screen.getByRole('button', { name: 'What are the key points?' }))
    await userEvent.click(screen.getByRole('button', { name: 'Ask' }))

    await waitFor(() => expect(router.state.location.pathname).toBe('/chat/c-new'))
    expect(calls.find((c) => c.path === '/api/chat/conversations/c-new/messages')?.body).toEqual({ content: 'What are the key points?', attachments: ['att-1'] })
    const file = uploaded.files.at(-1)!
    expect(file.name).toBe('Page - Release notes.md')
    expect(await file.text()).toContain('The text of https://example.test/notes')
    expect(sessionStorage.getItem('ask-quote')).toBeNull()
  })

  it('says how to get here when nothing was handed over', async () => {
    fakeApi(member)
    renderApp('/ask')
    expect(await screen.findByText('Nothing to ask about yet')).toBeInTheDocument()
  })
})
