import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import { blank } from './live'
import type { ChatConfig, Conversation, Message } from './types'

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: false, loaded: true, prices: { input: 0.2, cachedInput: 0.02, output: 0.8 } }],
  presets: [], defaultThinking: null, argus: false, tools: [], gitlabUrl: null, maxUploadBytes: 20 * 1024 * 1024, imageTypes: [],
}

const msg = (id: string, parentId: string | null, role: Message['role'], over: Partial<Message> = {}): Message => ({ ...blank(id, role, parentId), status: 'complete', ...over })

const chat = (over: Partial<Message> = {}): Conversation => ({
  id: 'c1', title: 'Budget', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: 'a2', archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '',
  messages: [
    msg('q1', null, 'user', { content: 'My budget code is AB-1234.', ...over }),
    msg('a1', 'q1', 'assistant', { content: 'Noted.' }),
    msg('q2', 'a1', 'user', { content: 'Forget the old vendor.', leftOut: true }),
    msg('a2', 'q2', 'assistant', { content: 'Done.' }),
  ],
})

describe('what the model reads of a chat', () => {
  it('a question is pinned or left out, and says so', async () => {
    let shown = chat()
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: shown }),
      'PUT /api/chat/conversations/c1/messages/q1/context': (body) => {
        shown = chat({ pinned: (body as { pinned: boolean }).pinned })
        return { json: { id: 'q1', pinned: true, leftOut: false } }
      },
    })
    renderApp('/chat/c1')
    expect(await screen.findByText('Left out: the model does not read it')).toBeInTheDocument()
    const first = (await screen.findByText('My budget code is AB-1234.')).closest('section')!
    await userEvent.click(within(first).getByRole('button', { name: 'Pin this question' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ pinned: true }))
    expect(await within(first).findByText('Pinned')).toBeInTheDocument()
    // Pinned: it cannot be left out until it is unpinned.
    expect(within(first).queryByRole('button', { name: 'Leave this question out' })).not.toBeInTheDocument()
    expect(within(first).getByRole('button', { name: 'Unpin this question' })).toHaveAttribute('aria-pressed', 'true')
  })

  it('the gauge shows the memory and the biggest messages, and leaves one out from there', async () => {
    const context = {
      system: 2000, instructions: 0, memory: 1000, tools: 3000, summary: 0, files: 0, you: 2000, answers: 1000, toolResults: 11000,
      biggest: [
        { id: 't1', role: 'tool' as const, tool: 'web_fetch', chars: 10000, preview: '<html><head><title>Vendors</title>', pinned: false },
        { id: 'q1', role: 'user' as const, chars: 1500, preview: 'My budget code is AB-1234.', pinned: false },
        { id: 'gone', role: 'user' as const, chars: 1200, preview: 'In the chat this one was forked from', pinned: false },
      ],
    }
    let shown: Conversation = {
      ...chat(),
      currentLeafId: 'a3',
      messages: [
        msg('q1', null, 'user', { content: 'My budget code is AB-1234.' }),
        msg('a1', 'q1', 'assistant', { content: '', toolCalls: [{ id: 'call1', function: { name: 'web_fetch', arguments: '{}' } }] }),
        msg('t1', 'a1', 'tool', { content: '<html><head><title>Vendors</title>', toolCallId: 'call1', toolName: 'web_fetch' }),
        msg('a3', 't1', 'assistant', { content: 'Three vendors.', promptTokens: 20_000, completionTokens: 100, context }),
      ],
    }
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: shown }),
      'PUT /api/chat/conversations/c1/messages/t1/context': () => {
        shown = { ...shown, messages: shown.messages.map((m) => (m.id === 't1' ? { ...m, leftOut: true } : m)) }
        return { json: { id: 't1', pinned: false, leftOut: true } }
      },
    })
    renderApp('/chat/c1')
    await userEvent.click(await screen.findByRole('button', { name: /^Context: 61% full/ }))
    // 20,000 prompt tokens over 20,000 characters: the memory a thousand of them.
    const parts = await screen.findByRole('list', { name: 'What fills it' })
    expect(within(parts).getByText('Memory').parentElement).toHaveTextContent('Memory1 K3%')
    const biggest = screen.getByRole('list', { name: 'The biggest messages' })
    // Only the messages of this chat: the forked one's is not listed.
    expect(within(biggest).getAllByRole('listitem')).toHaveLength(2)
    expect(within(biggest).getByText("web_fetch's result").closest('li')).toHaveTextContent('10 K')
    // A tool's result is left out, not pinned; a question either.
    expect(within(biggest).queryByRole('button', { name: 'Pin this result' })).not.toBeInTheDocument()
    expect(within(biggest).getByRole('button', { name: 'Pin this question' })).toBeInTheDocument()
    await userEvent.click(within(biggest).getByRole('button', { name: 'Leave this result out' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ leftOut: true }))
    expect(await within(biggest).findByText('Left out: the model does not read it')).toBeInTheDocument()
  })
})
