import { act, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import { sharedText } from '@/lib/shared'
import type { ChatConfig, Conversation } from './types'

vi.mock('./api', async (original) => ({
  ...(await original<typeof import('./api')>()),
  // XMLHttpRequest (for upload progress) has no server in tests.
  uploadFile: vi.fn(async (file: File) => ({ id: `att-${file.name}`, fileName: file.name, size: file.size, truncated: false, kind: 'text', contentType: file.type })),
}))

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: false, loaded: true, prices: { input: 0.2, cachedInput: 0.02, output: 0.8 } }],
  presets: [{ level: 'off', label: 'No thinking' }],
  defaultThinking: 'off',
  argus: false,
  tools: [],
  gitlabUrl: null,
  maxUploadBytes: 20 * 1024 * 1024,
  imageTypes: [],
}

const conversation: Conversation = {
  id: 'c1', title: 'New chat', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: null, archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '', messages: [],
}

let online = true
const backOnline = () =>
  act(() => {
    online = true
    window.dispatchEvent(new Event('online'))
  })

beforeEach(() => {
  online = true
  Object.defineProperty(navigator, 'onLine', { configurable: true, get: () => online })
  localStorage.removeItem('arena.outbox')
})

afterEach(() => {
  Reflect.deleteProperty(navigator, 'onLine')
  vi.unstubAllGlobals()
})

function backend() {
  return fakeApi(member, {
    'GET /api/chat/config': () => ({ json: config }),
    'GET /api/chat/conversations': () => ({ json: [] }),
    'POST /api/chat/conversations': () => ({ status: 201, json: conversation }),
    'GET /api/chat/conversations/c1': () => ({ json: conversation }),
    'POST /api/chat/conversations/c1/messages': () => ({ events: [{ type: 'question', id: 'q1', parentId: null }, { type: 'done', id: 'q1' }] }),
  })
}

describe('the outbox', () => {
  it('keeps what is sent with no connection, and sends it when the connection is back', async () => {
    const calls = backend()
    renderApp('/chat/c1')
    online = false
    const box = await screen.findByRole('textbox', { name: 'Message' })
    await userEvent.type(box, 'Book the room for Friday{Enter}')
    const waiting = await screen.findByRole('region', { name: 'Waiting to be sent' })
    expect(waiting).toHaveTextContent('No connection: what you send waits here')
    expect(within(waiting).getByText('Book the room for Friday')).toBeInTheDocument()
    expect(box).toHaveValue('')
    expect(calls.some((c) => c.path === '/api/chat/conversations/c1/messages')).toBe(false)
    // Kept in the browser: another page (or the same, opened again) still has it.
    expect(JSON.parse(localStorage.getItem('arena.outbox')!)).toHaveLength(1)

    backOnline()
    await waitFor(() => expect(calls.find((c) => c.path === '/api/chat/conversations/c1/messages')?.body).toMatchObject({ content: 'Book the room for Friday', attachments: [] }))
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Waiting to be sent' })).not.toBeInTheDocument())
    expect(localStorage.getItem('arena.outbox')).toBeNull()
  })

  it('a new chat sent with no connection is made when it is back, and what waits can be removed', async () => {
    const calls = backend()
    renderApp('/chat')
    online = false
    const box = await screen.findByRole('textbox', { name: 'Message' })
    await userEvent.type(box, 'First question{Enter}')
    await userEvent.type(box, 'Never mind{Enter}')
    const waiting = await screen.findByRole('region', { name: 'Waiting to be sent' })
    await userEvent.click(within(waiting).getByRole('button', { name: 'Remove: Never mind' }))
    expect(within(waiting).queryByText('Never mind')).not.toBeInTheDocument()
    expect(calls.some((c) => c.method === 'POST' && c.path === '/api/chat/conversations')).toBe(false)

    backOnline()
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/chat/conversations')).toBe(true))
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/chat/conversations/c1/messages').map((c) => (c.body as { content: string }).content)).toEqual(['First question']))
  })
})

/** The service worker's cache of shares, as the browser keeps it. */
class FakeCache {
  kept = new Map<string, Response>()
  match(key: string) {
    return Promise.resolve(this.kept.get(key)?.clone())
  }
  put(key: string, r: Response) {
    this.kept.set(key, r)
    return Promise.resolve()
  }
  delete(key: string) {
    return Promise.resolve(this.kept.delete(key))
  }
}

describe('sharing to Arena', () => {
  it('a share from another app opens a new chat with its files attached and its words in the box, once', async () => {
    const cache = new FakeCache()
    vi.stubGlobal('caches', { open: () => Promise.resolve(cache) })
    await cache.put('/shared/k1', new Response(JSON.stringify({ title: 'Q3 report', text: 'Have a look https://intranet.example.test/q3', url: 'https://intranet.example.test/q3', files: [{ name: 'q3.csv', type: 'text/csv' }] })))
    await cache.put('/shared/k1/0', new Response('region,total\nnorth,10\n'))
    backend()
    renderApp('/chat?shared=k1')
    // The box starts again with the share's words (found again: it is a new one).
    await waitFor(() => expect(screen.getByRole('textbox', { name: 'Message' })).toHaveValue('Q3 report\n\nHave a look https://intranet.example.test/q3'))
    expect(await screen.findByText('q3.csv')).toBeInTheDocument()
    // Taken: it is not there to take again.
    expect(cache.kept.size).toBe(0)
  })

  it('a share\'s words are its title, text and link, each once', () => {
    expect(sharedText({ title: '', text: 'Look at this', url: 'https://example.test/a' })).toBe('Look at this\n\nhttps://example.test/a')
    expect(sharedText({ title: 'A', text: 'A and more https://example.test/a', url: 'https://example.test/a' })).toBe('A and more https://example.test/a')
  })
})
