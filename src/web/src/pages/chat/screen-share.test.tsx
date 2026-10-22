import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import { FrameSide, frameName } from './screen'
import type { ChatConfig, Conversation } from './types'

vi.mock('./api', async (original) => ({
  ...(await original<typeof import('./api')>()),
  // XMLHttpRequest (for upload progress) has no server in tests.
  uploadFile: vi.fn(async (file: File) => ({ id: `att-${file.name}`, fileName: file.name, size: file.size, truncated: false, kind: 'image', contentType: file.type })),
}))

const model = (name: string, vision: boolean) => ({ name, context: 32768, maxOutput: 4096, vision, tools: true, thinking: false, loaded: true, prices: { input: 1, cachedInput: 0.1, output: 2 } })

const config = (vision: boolean, others: string[] = []): ChatConfig => ({
  model: 'Eyes-Model',
  models: [model('Eyes-Model', vision), ...others.map((n) => model(n, true))],
  presets: [{ level: 'off', label: 'No thinking' }],
  defaultThinking: 'off',
  argus: false,
  tools: [],
  gitlabUrl: null,
  maxUploadBytes: 20 * 1024 * 1024,
  imageTypes: ['image/png', 'image/jpeg'],
})

const conversation: Conversation = {
  id: 'c1', title: 'New chat', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: null, archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '', messages: [],
}

// The screen the browser shares, and what was drawn from it.
const track = { label: 'Window: Sales dashboard', stopped: false, onEnded: null as null | (() => void) }
const drawn: { width: number; height: number }[] = []

beforeEach(() => {
  track.stopped = false
  drawn.length = 0
  Object.defineProperty(navigator, 'mediaDevices', {
    configurable: true,
    value: {
      getDisplayMedia: vi.fn(async () => {
        const t = {
          kind: 'video', label: track.label, getSettings: () => ({ width: 3200, height: 1800 }),
          stop: () => { track.stopped = true },
          addEventListener: (_: string, f: () => void) => { track.onEnded = f },
        }
        return { getVideoTracks: () => [t], getTracks: () => [t] }
      }),
    },
  })
  vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue(undefined)
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(function (this: HTMLCanvasElement) {
    return { drawImage: (_: unknown, _x: number, _y: number, width: number, height: number) => drawn.push({ width, height }) } as never
  })
  vi.spyOn(HTMLCanvasElement.prototype, 'toBlob').mockImplementation((done: BlobCallback) => done(new Blob(['jpeg'], { type: 'image/jpeg' })))
})

afterEach(() => {
  vi.restoreAllMocks()
  Reflect.deleteProperty(navigator, 'mediaDevices')
})

function backend(vision = true, others: string[] = []) {
  return fakeApi(member, {
    'GET /api/chat/config': () => ({ json: config(vision, others) }),
    'GET /api/chat/conversations': () => ({ json: [] }),
    'POST /api/chat/conversations': () => ({ status: 201, json: conversation }),
    'GET /api/chat/conversations/c1': () => ({ json: conversation }),
    'POST /api/chat/conversations/c1/messages': () => ({
      events: [
        { type: 'question', id: 'q1', parentId: null },
        { type: 'assistant', id: 'a1', parentId: 'q1', model: 'Eyes-Model' },
        { type: 'content', text: 'The March bar is missing its label.' },
        { type: 'done' },
      ],
    }),
  })
}

describe('show your screen', () => {
  it('each question takes a picture of the shared screen, at most 1600 pixels a side', async () => {
    const calls = backend()
    renderApp('/chat')
    await userEvent.click(await screen.findByRole('button', { name: /Share your screen/ }))
    const bar = await screen.findByRole('region', { name: 'Sharing your screen' })
    expect(bar).toHaveTextContent('Sharing Window: Sales dashboard')
    expect(bar).toHaveTextContent('Each question takes a picture of it now.')

    await userEvent.type(screen.getByRole('textbox', { name: /Message/ }), 'What is wrong with this chart?{Enter}')
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/chat/conversations/c1/messages')).toBe(true))
    const sent = calls.find((c) => c.method === 'POST' && c.path === '/api/chat/conversations/c1/messages')!.body as { content: string; attachments: string[] }
    expect(sent.content).toBe('What is wrong with this chart?')
    expect(sent.attachments).toHaveLength(1)
    expect(sent.attachments[0]).toMatch(/^att-screen-\d\d-\d\d-\d\d\.jpg$/)
    expect(drawn).toEqual([{ width: FrameSide, height: 900 }])

    // Stopped from the browser's own bar: the chat stops sharing too.
    track.onEnded?.()
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Sharing your screen' })).toBeNull())
    expect(track.stopped).toBe(true)
  })

  it('says when the chat’s model cannot see the pictures', async () => {
    backend(false)
    renderApp('/chat')
    await userEvent.click(await screen.findByRole('button', { name: /Share your screen/ }))
    expect(await screen.findByRole('region', { name: 'Sharing your screen' })).toHaveTextContent('cannot see pictures')
    await userEvent.click(screen.getByRole('button', { name: 'Stop sharing your screen' }))
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Sharing your screen' })).toBeNull())
  })

  it('offers a model that can see, and switches to it in one click', async () => {
    backend(false, ['Seer-VL'])
    renderApp('/chat')
    await userEvent.click(await screen.findByRole('button', { name: /Share your screen/ }))
    const bar = await screen.findByRole('region', { name: 'Sharing your screen' })
    expect(bar).toHaveTextContent('switch to one that can')
    await userEvent.click(screen.getByRole('button', { name: 'Use Seer-VL' }))
    await waitFor(() => expect(bar).toHaveTextContent('Each question takes a picture of it now.'))
    expect(screen.queryByRole('button', { name: 'Use Seer-VL' })).toBeNull()
  })

  it('names a frame by when it was taken', () => {
    expect(frameName(new Date(2026, 9, 21, 9, 5, 7))).toBe('screen-09-05-07.jpg')
  })
})
