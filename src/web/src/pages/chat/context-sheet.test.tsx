import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { ThemeProvider } from '@/lib/theme'
import { fakeApi, member, renderApp } from '@/test/utils'
import { ContextWindowView, type ContextWindowData } from './context-window'
import type { ChatConfig, Conversation, Message } from './types'

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: false, loaded: true, prices: { input: null, cachedInput: null, output: null } }],
  presets: [],
  defaultThinking: null,
  argus: false,
  tools: [
    { id: 'calculator', title: 'Calculator', description: 'Exact arithmetic.', icon: 'calculator', onByDefault: true, askFirst: false },
    { id: 'time', title: 'Time', description: 'The date and time.', icon: 'clock', onByDefault: true, askFirst: false },
  ],
  gitlabUrl: null,
  maxUploadBytes: 1024 * 1024,
  imageTypes: [],
}

const messages = [
  { id: 'q1', parentId: null, role: 'user', content: 'Summarize the notes', attachments: [{ id: 'f1', fileName: 'notes.txt', size: 4000, truncated: false, kind: 'text', contentType: 'text/plain' }], createdAt: '' },
  { id: 'a1', parentId: 'q1', role: 'assistant', content: 'They say Thursday.', attachments: [], createdAt: '', model: 'Main-Model', promptTokens: 2400, completionTokens: 10 },
  { id: 'q2', parentId: 'a1', role: 'user', content: 'Thanks', attachments: [], createdAt: '' },
  { id: 'a2', parentId: 'q2', role: 'assistant', content: 'You are welcome.', attachments: [], createdAt: '', model: 'Main-Model', promptTokens: 2500, completionTokens: 5 },
] as unknown as Message[]

const conversation: Conversation = {
  id: 'c1', title: 'Notes', thinking: null, tools: ['calculator', 'time'], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: 'a2', archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '', messages,
}

const window: ContextWindowData = {
  total: 2500,
  limit: 32768,
  reserve: 4096,
  free: 26172,
  counted: 'tokenizer',
  by: 'llama.cpp',
  parts: [
    { key: 'system', label: 'System prompt and tool notes', tokens: 600, count: 2, items: [{ name: "Arena's own words", tokens: 400 }, { name: 'Notes: calculator', tokens: 200, tool: 'calculator' }], more: 0, moreTokens: 0 },
    {
      key: 'tools', label: 'Tool definitions', tokens: 700, count: 3,
      items: [{ name: 'calculate', tokens: 400, tool: 'calculator' }, { name: 'now', tokens: 200, tool: 'time' }],
      more: 1, moreTokens: 100,
    },
    { key: 'files', label: 'Files', tokens: 1000, count: 1, items: [{ name: 'notes.txt', tokens: 1000, ref: 'q1' }], more: 0, moreTokens: 0 },
    { key: 'you', label: 'Your messages', tokens: 100, count: 2, items: [{ name: 'Your message', preview: 'Summarize the notes', tokens: 60, ref: 'q1' }], more: 0, moreTokens: 0 },
    { key: 'format', label: 'Chat format', tokens: 100, count: 0, items: [], more: 0, moreTokens: 0 },
  ],
  freeable: [
    { kind: 'tool', label: 'calculator: not called in this chat', tokens: 600, ref: 'calculator', action: 'tool-off', detail: 'calculate' },
    { kind: 'file', label: 'notes.txt, with an earlier message', tokens: 1000, ref: 'q1', action: 'drop-file', detail: 'notes.txt', file: 'f1' },
  ],
}

function backend() {
  return fakeApi(member, {
    'GET /api/chat/config': () => ({ json: config }),
    'GET /api/chat/conversations': () => ({ json: [] }),
    'GET /api/chat/conversations/c1': () => ({ json: conversation }),
    'GET /api/chat/conversations/c1/context': () => ({ json: { window, at: 'a2', model: 'Main-Model', compactedSince: false } }),
    'PATCH /api/chat/conversations/c1': () => ({ json: {} }),
    'PUT /api/chat/conversations/c1/messages/q1/file-context': () => ({ json: { id: 'q1', leftOutFiles: ['f1'] } }),
  })
}

describe('the Context panel', () => {
  it('/context opens what fills the window, piece by piece, counted by the tokenizer, with what could be freed', async () => {
    const calls = backend()
    renderApp('/chat/c1')
    const box = await screen.findByRole('textbox', { name: 'Message' })
    await userEvent.type(box, '/context{Enter}')
    const panel = await screen.findByRole('dialog', { name: 'Context' })
    expect(await within(panel).findByText(/Counted with the model's own tokenizer \(llama\.cpp\)/)).toBeInTheDocument()
    expect(within(panel).getByText(/of 32\.77 K tokens \(8%\)/)).toBeInTheDocument()
    // Nothing was sent: /context is a command.
    expect(calls.some((c) => c.method === 'POST' && c.path === '/api/chat/conversations/c1/messages')).toBe(false)

    // Each tool's definition by name, opened from its part, with what is not listed summed.
    const parts = within(panel).getByRole('list', { name: 'What fills the window' })
    await userEvent.click(within(parts).getByRole('button', { name: /Tool definitions/ }))
    const tools = within(panel).getByRole('list', { name: 'Tool definitions' })
    expect(within(tools).getByText('calculate')).toBeInTheDocument()
    expect(within(tools).getByText('and 1 more (100)')).toBeInTheDocument()

    // A tool never called, turned off for this chat; a file of an earlier message, left out.
    await userEvent.click(within(panel).getByRole('button', { name: 'Turn off: calculator: not called in this chat' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PATCH' && c.path === '/api/chat/conversations/c1')?.body).toEqual({ tools: ['time'] }))
    await userEvent.click(within(panel).getByRole('button', { name: 'Leave file out: notes.txt, with an earlier message' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT' && c.path === '/api/chat/conversations/c1/messages/q1/file-context')?.body).toEqual({ file: 'f1', leftOut: true }))
  })

  it('opens from the gauge', async () => {
    backend()
    renderApp('/chat/c1')
    await userEvent.click(await screen.findByRole('button', { name: /^Context: \d+% full/ }))
    await userEvent.click(await screen.findByRole('button', { name: 'What fills it' }))
    expect(await screen.findByRole('dialog', { name: 'Context' })).toBeInTheDocument()
  })
})

describe('ContextWindowView', () => {
  it('says an estimate is one, strikes what was left out since, and offers no action it cannot take', async () => {
    const onFree = vi.fn()
    render(
      <ThemeProvider>
        <ContextWindowView
          window={{
            ...window,
            counted: 'estimate',
            by: 'the model is not loaded on an engine of this Arena',
            parts: [{ key: 'files', label: 'Files', tokens: 1000, count: 1, items: [{ name: 'notes.txt', tokens: 1000, ref: 'q1', leftOut: true }], more: 0, moreTokens: 0 }],
            freeable: [{ kind: 'compact', label: 'Older messages: compacting makes them a summary', tokens: 900, action: null }],
          }}
          onFree={onFree}
        />
      </ThemeProvider>,
    )
    expect(screen.getByText('Estimated from characters: the model is not loaded on an engine of this Arena.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: /Files/ }))
    expect(screen.getByText('notes.txt').closest('li')).toHaveClass('line-through')
    expect(screen.getByText('left out')).toBeInTheDocument()
    // Kept for the answer and Free close the list: together with the parts they make the window.
    expect(screen.getByText('Kept for the answer')).toBeInTheDocument()
    expect(screen.getByText('Free')).toBeInTheDocument()
    expect(within(screen.getByRole('list', { name: 'Could be freed' })).queryByRole('button')).toBeNull()
  })
})
