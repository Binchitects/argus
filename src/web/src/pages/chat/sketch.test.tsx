import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import { sketchAsks, sketchName } from './sketch-prompts'
import type { ChatConfig, Conversation } from './types'

vi.mock('./api', async (original) => ({
  ...(await original<typeof import('./api')>()),
  // XMLHttpRequest (for upload progress) has no server in tests.
  uploadFile: vi.fn(async (file: File) => ({ id: `att-${file.name}`, fileName: file.name, size: file.size, truncated: false, kind: 'image', contentType: file.type })),
}))

const config: ChatConfig = {
  model: 'Eyes-Model',
  models: [{ name: 'Eyes-Model', context: 32768, maxOutput: 4096, vision: true, tools: true, thinking: false, loaded: true, prices: { input: 1, cachedInput: 0.1, output: 2 } }],
  presets: [{ level: 'off', label: 'No thinking' }],
  defaultThinking: 'off',
  argus: false,
  tools: [],
  gitlabUrl: null,
  maxUploadBytes: 20 * 1024 * 1024,
  imageTypes: ['image/png', 'image/jpeg'],
}

const page = '<!doctype html><html><body><h1>Sign up</h1><button>Join</button></body></html>'
const answer = `Here it is:\n\n\`\`\`html\n${page}\n\`\`\`\n`

let saved: Conversation = {
  id: 'c1', title: 'New chat', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: null, archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '', messages: [],
}

// What the board drew: its strokes' points.
const lines: [number, number][] = []

beforeEach(() => {
  lines.length = 0
  vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: () => 'blob:sketch', revokeObjectURL: () => {} }))
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(() => ({
    fillRect: () => undefined, drawImage: () => undefined, beginPath: () => undefined, stroke: () => undefined,
    moveTo: (x: number, y: number) => lines.push([x, y]), lineTo: (x: number, y: number) => lines.push([x, y]),
  }) as never)
  vi.spyOn(HTMLCanvasElement.prototype, 'toBlob').mockImplementation((done: BlobCallback) => done(new Blob(['png'], { type: 'image/png' })))
})

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

function backend() {
  saved = { ...saved, messages: [], currentLeafId: null }
  return fakeApi(member, {
    'GET /api/chat/config': () => ({ json: config }),
    'GET /api/chat/conversations': () => ({ json: [] }),
    'POST /api/chat/conversations': () => ({ status: 201, json: saved }),
    'GET /api/chat/conversations/c1': () => ({ json: saved }),
    'POST /api/chat/conversations/c1/messages': (body) => {
      // The server keeps the question and its answer: the chat reads them back when the answer ends.
      const b = body as { content: string; attachments: string[] }
      saved = {
        ...saved,
        currentLeafId: 'a1',
        messages: [
          { id: 'q1', parentId: null, role: 'user', content: b.content, attachments: b.attachments.map((a) => ({ id: a, fileName: a.slice(4), size: 3, truncated: false, kind: 'image', contentType: 'image/png' })), createdAt: '' },
          { id: 'a1', parentId: 'q1', role: 'assistant', content: answer, attachments: [], createdAt: '', model: 'Eyes-Model' },
        ] as unknown as Conversation['messages'],
      }
      return {
        events: [
          { type: 'question', id: 'q1', parentId: null },
          { type: 'assistant', id: 'a1', parentId: 'q1', model: 'Eyes-Model' },
          { type: 'content', text: answer },
          { type: 'done' },
        ],
      }
    },
  })
}

describe('napkin to app', () => {
  it('a sketch becomes a page, which opens running', async () => {
    const calls = backend()
    renderApp('/chat')
    await userEvent.click(await screen.findByRole('button', { name: 'Sketch' }))
    const board = await screen.findByRole('dialog', { name: 'Sketch' })
    const make = within(board).getByRole('button', { name: 'A page' })
    expect(make).toBeDisabled()

    const canvas = within(board).getByLabelText('The whiteboard')
    fireEvent.pointerDown(canvas, { clientX: 0, clientY: 0, pointerId: 1 })
    fireEvent.pointerMove(canvas, { clientX: 1, clientY: 1, pointerId: 1 })
    fireEvent.pointerUp(canvas, { pointerId: 1 })
    expect(lines.length).toBeGreaterThan(0)
    await userEvent.click(within(board).getByRole('button', { name: 'Undo' }))
    expect(make).toBeDisabled()
    fireEvent.pointerDown(canvas, { clientX: 0, clientY: 0, pointerId: 1 })
    fireEvent.pointerUp(canvas, { pointerId: 1 })
    await userEvent.click(make)

    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/chat/conversations/c1/messages')).toBe(true))
    const sent = calls.find((c) => c.method === 'POST' && c.path === '/api/chat/conversations/c1/messages')!.body as { content: string; attachments: string[] }
    expect(sent.content).toBe(sketchAsks.find((a) => a.key === 'page')!.prompt)
    expect(sent.attachments).toEqual([expect.stringMatching(/^att-sketch-\d\d-\d\d-\d\d\.png$/)])
    // The page it gave, running in the panel.
    expect(await screen.findByTitle(/Preview/)).toBeInTheDocument()
  })

  it('the sketch can go with a message of one’s own instead', async () => {
    backend()
    renderApp('/chat')
    await userEvent.click(await screen.findByRole('button', { name: 'Sketch' }))
    const board = await screen.findByRole('dialog', { name: 'Sketch' })
    const canvas = within(board).getByLabelText('The whiteboard')
    fireEvent.pointerDown(canvas, { clientX: 0, clientY: 0, pointerId: 1 })
    fireEvent.pointerUp(canvas, { pointerId: 1 })
    await userEvent.click(within(board).getByRole('button', { name: 'Attach it' }))
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Sketch' })).toBeNull())
    expect(await screen.findByAltText(/^sketch-\d\d-\d\d-\d\d\.png$/)).toBeInTheDocument()
  })

  it('names a sketch by when it was drawn', () => {
    expect(sketchName(new Date(2026, 9, 21, 14, 3, 9))).toBe('sketch-14-03-09.png')
  })
})
