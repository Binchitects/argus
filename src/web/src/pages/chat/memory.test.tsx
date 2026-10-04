import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import type { MemoryView } from '@/lib/memory'
import { fakeApi, member, renderApp } from '@/test/utils'
import { blank } from './live'
import type { ChatConfig, Conversation, Message } from './types'

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: true, loaded: true, prices: { input: null, cachedInput: null, output: null } }],
  presets: [],
  defaultThinking: null,
  argus: false,
  tools: [{ id: 'memory', title: 'Memory', description: 'Remembers what you ask it to.', icon: 'brain', onByDefault: true, askFirst: false }],
  gitlabUrl: null,
  maxUploadBytes: 1024 * 1024,
  imageTypes: [],
}

const msg = (id: string, parentId: string | null, role: Message['role'], over: Partial<Message> = {}): Message => ({ ...blank(id, role, parentId), ...over })

/** A chat where the model called remember: offered (it waits for the person) or kept (asked for). */
const chat = (state: 'offered' | 'kept'): Conversation => ({
  id: 'c1', title: 'Podman', thinking: null, tools: ['memory'], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: 'a2', archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '',
  messages: [
    msg('q1', null, 'user', { content: 'I deploy with Podman. How do I restart a pod?' }),
    msg('a1', 'q1', 'assistant', { toolCalls: [{ id: 'call_1', function: { name: 'remember', arguments: '{"memory":"Deploys with Podman."}' } }] }),
    msg('t1', 'a1', 'tool', { toolCallId: 'call_1', toolName: 'remember', content: 'Offered to the person', details: { memory: { id: state === 'kept' ? 'm1' : null, text: 'Deploys with Podman.', state } } }),
    msg('a2', 't1', 'assistant', { content: 'Run podman pod restart.' }),
  ],
})

const memories = (over: Partial<MemoryView> = {}): MemoryView => ({ enabled: true, on: true, max: 100, maxChars: 300, memories: [], ...over })

describe('memory in the chat', () => {
  it('what the model offers is kept only when the person says Remember, in their own words if they change them', async () => {
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: chat('offered') }),
      'GET /api/account/memories': () => ({ json: memories() }),
      'POST /api/account/memories/offers/t1': (body) => {
        const b = body as { keep: boolean; text?: string }
        return { json: b.keep ? { id: 'm1', text: b.text ?? 'Deploys with Podman.', state: 'kept' } : { id: null, text: 'Deploys with Podman.', state: 'forgotten' } }
      },
    })
    renderApp('/chat/c1')
    const card = await screen.findByRole('region', { name: 'Remember this?' })
    expect(within(card).getByText('Deploys with Podman.')).toBeInTheDocument()
    await userEvent.click(within(card).getByRole('button', { name: 'Edit' }))
    const words = within(card).getByRole('textbox', { name: 'What to remember' })
    await userEvent.clear(words)
    await userEvent.type(words, 'Deploys with Podman 5.')
    await userEvent.click(within(card).getByRole('button', { name: 'Remember' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/account/memories/offers/t1')?.body).toEqual({ keep: true, text: 'Deploys with Podman 5.' }))
    const kept = await screen.findByRole('region', { name: 'Remembered' })
    expect(within(kept).getByText('Deploys with Podman 5.')).toBeInTheDocument()

    // Undo takes it back.
    await userEvent.click(within(kept).getByRole('button', { name: 'Undo' }))
    expect(await screen.findByRole('region', { name: 'Forgotten' })).toBeInTheDocument()
    expect(calls.filter((c) => c.path === '/api/account/memories/offers/t1').at(-1)?.body).toEqual({ keep: false })
  })

  it('a memory the person asked for shows as remembered, and the Memory button lists and deletes theirs', async () => {
    let list = [{ id: 'm1', text: 'Deploys with Podman.', createdAt: '2026-10-01T10:00:00Z', updatedAt: '2026-10-01T10:00:00Z' }]
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: chat('kept') }),
      'GET /api/account/memories': () => ({ json: memories({ memories: list }) }),
      'DELETE /api/account/memories/m1': () => {
        list = []
        return { status: 204 }
      },
    })
    renderApp('/chat/c1')
    expect(await screen.findByRole('region', { name: 'Remembered' })).toBeInTheDocument()
    await userEvent.click(await screen.findByRole('button', { name: 'Memory (1)' }))
    const dialog = await screen.findByRole('dialog', { name: 'Memory' })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Delete: Deploys with Podman.' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.path === '/api/account/memories/m1')).toBe(true))
    expect(await within(dialog).findByText(/Nothing remembered yet/)).toBeInTheDocument()
  })

  it('no Memory button where memory is off for everyone', async () => {
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: chat('kept') }),
      'GET /api/account/memories': () => ({ json: memories({ enabled: false }) }),
    })
    renderApp('/chat/c1')
    await screen.findByRole('region', { name: 'Remembered' })
    expect(screen.queryByRole('button', { name: /^Memory/ })).toBeNull()
  })
})
