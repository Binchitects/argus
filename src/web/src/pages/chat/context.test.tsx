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
})
