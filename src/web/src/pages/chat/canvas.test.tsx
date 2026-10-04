import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { saveBlob } from '@/lib/zip'
import { fakeApi, member, renderApp, type Handler } from '@/test/utils'
import type { Canvas, CanvasVersion } from './canvas-api'
import { blank } from './live'
import type { ChatConfig, Conversation, Message } from './types'

vi.mock('@/lib/zip', async (original) => ({ ...(await original<typeof import('@/lib/zip')>()), saveBlob: vi.fn() }))

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: false, loaded: true, prices: { input: null, cachedInput: null, output: null } }],
  presets: [],
  defaultThinking: null,
  argus: false,
  tools: [{ id: 'canvas', title: 'Canvas', description: 'Writes documents and code in a canvas beside the chat.', icon: 'file-pen', onByDefault: true, askFirst: false }],
  gitlabUrl: null,
  maxUploadBytes: 1024,
  imageTypes: [],
}

const conversation = (over: Partial<Conversation> = {}): Conversation => ({
  id: 'c1', title: 'Plans', thinking: null, tools: ['canvas'], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: null, archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '', messages: [], ...over,
})

const text1 = '# Plan\n\nShip by March.\nTwo people.\n'
const text2 = '# Plan\n\nShip by March.\nTwo people for a quarter.\n'
const canvas = (over: Partial<Canvas> = {}): Canvas => ({
  id: 'k1', conversationId: 'c1', title: 'Plan', kind: 'document', language: null, version: 1, lines: 4, createdAt: '2026-10-04T10:00:00Z', updatedAt: '2026-10-04T10:00:00Z',
  content: text1, ...over,
})
const version = (number: number, content: string, author: 'person' | 'model', summary: string): CanvasVersion => ({ number, title: 'Plan', author, summary, createdAt: '2026-10-04T10:00:00Z', content })

/** One chat with canvases; `current` is what the server holds, and changes as the routes change it. */
function backend(opts: { canvas?: Canvas; versions?: CanvasVersion[]; extra?: Record<string, Handler>; start?: Conversation } = {}) {
  const state = { canvas: opts.canvas ?? canvas() }
  const versions = opts.versions ?? [version(1, text1, 'person', 'Created')]
  const calls = fakeApi(member, {
    'GET /api/chat/config': () => ({ json: config }),
    'GET /api/chat/conversations': () => ({ json: [] }),
    'GET /api/chat/conversations/c1': () => ({ json: opts.start ?? conversation() }),
    'GET /api/chat/conversations/c1/canvases': () => ({ json: [{ ...state.canvas, content: null }] }),
    'GET /api/chat/canvases/k1': () => ({ json: state.canvas }),
    'PUT /api/chat/canvases/k1': (body) => {
      const b = body as { baseVersion: number; content: string; title: string }
      if (b.baseVersion !== state.canvas.version) return { status: 409, json: { status: 'changed', error: 'The model changed this canvas.' } }
      state.canvas = { ...state.canvas, content: b.content, title: b.title, version: state.canvas.version + 1 }
      return { json: state.canvas }
    },
    'GET /api/chat/canvases/k1/versions': () => ({ json: [...versions].reverse().map((v) => ({ number: v.number, title: v.title, author: v.author, summary: v.summary, createdAt: v.createdAt })) }),
    ...Object.fromEntries(versions.map((v) => [`GET /api/chat/canvases/k1/versions/${v.number}`, () => ({ json: v })])),
    'POST /api/chat/canvases/k1/versions/1/restore': () => {
      state.canvas = { ...state.canvas, content: text1, version: state.canvas.version + 1 }
      return { json: state.canvas }
    },
    ...opts.extra,
  })
  return { calls, state }
}

async function openCanvas() {
  await userEvent.click(await screen.findByRole('button', { name: 'Canvas (1)' }))
  const panel = await screen.findByRole('complementary', { name: 'Canvas' })
  await userEvent.click(await within(panel).findByRole('button', { name: /^Plan/ }))
  return panel
}

describe('canvas', () => {
  it('opens beside the chat from its header, and a save by the person is a version', async () => {
    const { calls } = backend()
    renderApp('/chat/c1')
    const panel = await openCanvas()
    const box = await within(panel).findByRole('textbox', { name: 'Plan: text' })
    expect(box).toHaveValue(text1)
    expect(within(panel).getByText(/^Version 1 · saved/)).toBeInTheDocument()
    await userEvent.type(box, 'Budget: small.')
    expect(within(panel).getByText('Unsaved changes')).toBeInTheDocument()
    await userEvent.click(within(panel).getByRole('button', { name: /Save/ }))
    await waitFor(() => expect(within(panel).getByText(/^Version 2 · saved/)).toBeInTheDocument())
    expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ baseVersion: 1, content: text1 + 'Budget: small.', title: 'Plan' })
    // The Markdown shows formatted under Preview.
    await userEvent.click(within(panel).getByRole('tab', { name: 'Preview' }))
    expect(within(panel).getByRole('heading', { name: 'Plan' })).toBeInTheDocument()
    // Text selected there (its paragraph: three lines of the source) is found in the source, with its lines.
    window.getSelection()!.selectAllChildren(within(panel).getByText('Budget: small.', { exact: false }))
    fireEvent(document, new Event('selectionchange'))
    expect(await within(panel).findByRole('toolbar', { name: 'Selection' })).toHaveTextContent('Lines 3–5 selected')
  })

  it('shows what each version changed, and restores one as a new version', async () => {
    const { calls } = backend({
      canvas: canvas({ content: text2, version: 2 }),
      versions: [version(1, text1, 'person', 'Created'), version(2, text2, 'model', 'Named the team size')],
    })
    renderApp('/chat/c1')
    const panel = await openCanvas()
    await userEvent.click(await within(panel).findByRole('button', { name: 'Versions' }))
    const list = await within(panel).findByRole('list', { name: 'Versions' })
    expect(within(list).getAllByRole('button').map((b) => b.textContent)).toEqual([expect.stringContaining('v2Named the team sizeThe model'), expect.stringContaining('v1CreatedYou')])
    // The newest is chosen: one line out, one in, and it is the current version.
    const changes = await within(panel).findByRole('table', { name: 'Changes' })
    expect(within(changes).getAllByText('Removed', { exact: true })).toHaveLength(1)
    expect(changes.querySelector('[data-kind="removed"]')).toHaveTextContent('Two people.')
    expect(changes.querySelector('[data-kind="added"]')).toHaveTextContent('Two people for a quarter.')
    expect(within(panel).getByRole('button', { name: /The current version/ })).toBeDisabled()

    await userEvent.click(within(list).getByRole('button', { name: /v1/ }))
    await userEvent.click(await within(panel).findByRole('button', { name: /Restore this version/ }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/chat/canvases/k1/versions/1/restore')).toBe(true))
    // Back at the text, which is version 1's again, as version 3.
    expect(await within(panel).findByRole('textbox', { name: 'Plan: text' })).toHaveValue(text1)
    expect(within(panel).getByText(/^Version 3 · saved/)).toBeInTheDocument()
  })

  it('sends a selection to the chat, quoted with the canvas id and lines', async () => {
    const { calls } = backend({
      extra: { 'POST /api/chat/conversations/c1/messages': () => ({ events: [{ type: 'question', id: 'q1', parentId: null }, { type: 'done', id: 'q1' }] }) },
    })
    renderApp('/chat/c1')
    const panel = await openCanvas()
    const box = (await within(panel).findByRole('textbox', { name: 'Plan: text' })) as HTMLTextAreaElement
    box.focus()
    box.setSelectionRange(text1.indexOf('Ship'), text1.length)
    fireEvent.keyUp(box)
    const bar = await within(panel).findByRole('toolbar', { name: 'Selection' })
    expect(bar).toHaveTextContent('Lines 3–4 selected')
    await userEvent.click(within(bar).getByRole('button', { name: /Make this shorter/ }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/chat/conversations/c1/messages')).toBeDefined())
    const sent = calls.find((c) => c.path === '/api/chat/conversations/c1/messages')!.body as { content: string; attachments: string[] }
    expect(sent.content).toBe('About lines 3–4 of the canvas “Plan” (id k1):\n\n> Ship by March.\n> Two people.\n\nMake this shorter. If that needs a change, change only this part, with canvas_edit.')
    expect(sent.attachments).toEqual([])

    // Ask about this: the person's own question, about the same lines.
    box.focus()
    box.setSelectionRange(0, 6)
    fireEvent.keyUp(box)
    await userEvent.click(await within(panel).findByRole('button', { name: /Ask about this/ }))
    await userEvent.type(within(panel).getByRole('textbox', { name: 'Your question about the selection' }), 'Is this a good title?{Enter}')
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/chat/conversations/c1/messages')).toHaveLength(2))
    expect((calls.filter((c) => c.path === '/api/chat/conversations/c1/messages')[1]!.body as { content: string }).content).toMatch(/^About line 1 of the canvas “Plan” \(id k1\):\n\n> # Plan\n\nIs this a good title\?/)
  })

  it("the model's change shows in the open canvas, and its card opens the canvas", async () => {
    const call = { id: 'call_1', function: { name: 'canvas_edit', arguments: JSON.stringify({ id: 'k1', edits: [{ find: 'Two people.', replace: 'Two people for a quarter.' }] }) } }
    const messages: Message[] = [
      { ...blank('q1', 'user', null), content: 'Name the team size' },
      { ...blank('a1', 'assistant', 'q1'), toolCalls: [call] },
      { ...blank('t1', 'tool', 'a1', '{"edits":1}'), toolCallId: 'call_1', toolName: 'canvas_edit', status: 'complete', details: { canvas: { id: 'k1', title: 'Plan', version: 2 } } },
      { ...blank('a2', 'assistant', 't1'), content: 'Done.' },
    ]
    backend({ canvas: canvas({ content: text2, version: 2 }), start: conversation({ messages, currentLeafId: 'a2' }) })
    renderApp('/chat/c1')
    const a = await screen.findByRole('region', { name: 'Answer' })
    expect(within(a).getByText('Canvas edit')).toBeInTheDocument()
    await userEvent.click(within(a).getByRole('button', { name: 'Open Plan in the canvas' }))
    const panel = await screen.findByRole('complementary', { name: 'Canvas' })
    expect(await within(panel).findByRole('textbox', { name: 'Plan: text' })).toHaveValue(text2)
  })

  it('on a wide screen, a canvas the model changes opens beside the answer by itself', async () => {
    const media = vi.spyOn(window, 'matchMedia').mockImplementation(
      (query: string) => ({ matches: query.includes('min-width'), media: query, addEventListener() {}, removeEventListener() {} }) as unknown as MediaQueryList,
    )
    const { state } = backend({
      extra: {
        'POST /api/chat/conversations/c1/messages': () => {
          state.canvas = canvas({ content: text2, version: 2 })
          return {
            events: [
              { type: 'question', id: 'q1', parentId: null },
              { type: 'assistant', id: 'a1', parentId: 'q1', model: 'Main-Model' },
              { type: 'tool_call', id: 'call_1', name: 'canvas_edit', arguments: '{"id":"k1"}' },
              { type: 'tool_result', id: 'call_1', messageId: 't1', name: 'canvas_edit', text: '{"edits":1}', isError: false, noAccess: false, durationMs: 5, details: { canvas: { id: 'k1', title: 'Plan', version: 2 } } },
            ],
            hang: true,
          }
        },
      },
    })
    try {
      renderApp('/chat/c1')
      await userEvent.type(await screen.findByRole('textbox', { name: 'Message' }), 'Name the team size{Enter}')
      const panel = await screen.findByRole('complementary', { name: 'Canvas' })
      expect(await within(panel).findByRole('textbox', { name: 'Plan: text' })).toHaveValue(text2)
      expect(screen.getByRole('button', { name: 'Canvas (1)' })).toHaveAttribute('aria-pressed', 'true')
    } finally {
      media.mockRestore()
    }
  })

  it("a save over the model's newer version waits for the person's choice", async () => {
    const { state } = backend()
    renderApp('/chat/c1')
    const panel = await openCanvas()
    const box = await within(panel).findByRole('textbox', { name: 'Plan: text' })
    await userEvent.type(box, 'Mine.')
    // Meanwhile the model changed it.
    state.canvas = canvas({ content: text2, version: 2 })
    await userEvent.click(within(panel).getByRole('button', { name: /Save/ }))
    expect(await within(panel).findByText('The model changed this canvas while you were editing it.')).toBeInTheDocument()
    expect(box).toHaveValue(text1 + 'Mine.')
    await userEvent.click(within(panel).getByRole('button', { name: 'Load its version' }))
    expect(box).toHaveValue(text2)
    expect(within(panel).queryByText('The model changed this canvas while you were editing it.')).not.toBeInTheDocument()
  })

  it('exports a document as Word, and code as its file', async () => {
    const { calls } = backend({ extra: { 'GET /api/chat/canvases/k1/export': () => ({ json: {} }) } })
    renderApp('/chat/c1')
    const panel = await openCanvas()
    await within(panel).findByRole('textbox', { name: 'Plan: text' })
    await userEvent.click(within(panel).getByRole('button', { name: 'Export' }))
    expect(screen.getByRole('menuitem', { name: 'PDF (.pdf)' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('menuitem', { name: 'Word (.docx)' }))
    await waitFor(() => expect(saveBlob).toHaveBeenCalledWith(expect.any(Blob), 'Plan.docx'))
    expect(calls.some((c) => c.path === '/api/chat/canvases/k1/export?format=docx')).toBe(true)
  })

  it('starts a code canvas from the panel, with line numbers', async () => {
    const made = canvas({ id: 'k2', title: 'rename.py', kind: 'code', language: 'python', content: '', lines: 0 })
    const { calls } = backend({
      extra: {
        'POST /api/chat/conversations/c1/canvases': () => ({ status: 201, json: made }),
        'GET /api/chat/canvases/k2': () => ({ json: made }),
      },
    })
    renderApp('/chat/c1')
    await userEvent.click(await screen.findByRole('button', { name: 'Canvas (1)' }))
    const panel = await screen.findByRole('complementary', { name: 'Canvas' })
    await userEvent.click(within(panel).getByRole('button', { name: /New code/ }))
    const form = within(panel).getByRole('form', { name: 'New code' })
    await userEvent.type(within(form).getByRole('textbox', { name: 'Title' }), 'rename.py')
    await userEvent.type(within(form).getByRole('textbox', { name: 'Language' }), 'python')
    await userEvent.click(within(form).getByRole('button', { name: /Create/ }))
    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/chat/conversations/c1/canvases')?.body).toEqual({ title: 'rename.py', kind: 'code', language: 'python', content: '' })
    const box = await within(panel).findByRole('textbox', { name: 'rename.py: text' })
    expect(box).toHaveAttribute('wrap', 'off')
    await userEvent.type(box, 'import os{Enter}{Enter}print(1)')
    // A number for each line, beside the text.
    expect(box.previousElementSibling).toHaveTextContent('123')
  })
})
