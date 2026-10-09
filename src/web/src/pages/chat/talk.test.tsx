import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import type { ChatConfig, Conversation } from './types'
import { tuning } from './voice'

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: true, loaded: true, prices: { input: 0.2, cachedInput: 0.02, output: 0.8 } }],
  presets: [{ level: 'off', label: 'No thinking' }],
  defaultThinking: 'off',
  argus: false,
  tools: [],
  gitlabUrl: null,
  maxUploadBytes: 20 * 1024 * 1024,
  imageTypes: ['image/png'],
}

const conversation: Conversation = {
  id: 'c1', title: 'New chat', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: null, archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '', messages: [],
}

// The microphone, as loud as the test says; recordings; the sound played.
let mic = 0.001
const tracks = { stopped: 0 }
const sounds: FakeAudio[] = []

class FakeAnalyser {
  fftSize = 1024
  getFloatTimeDomainData(samples: Float32Array) {
    samples.fill(mic)
  }
}

class FakeAudioContext {
  createAnalyser() {
    return new FakeAnalyser()
  }
  createMediaStreamSource() {
    return { connect() {} }
  }
  close() {
    return Promise.resolve()
  }
}

class FakeRecorder {
  static isTypeSupported = () => true
  mimeType = 'audio/webm;codecs=opus'
  state = 'inactive'
  ondataavailable: ((e: { data: Blob }) => void) | null = null
  onstop: (() => void) | null = null
  start() {
    this.state = 'recording'
  }
  stop() {
    this.state = 'inactive'
    setTimeout(() => {
      this.ondataavailable?.({ data: new Blob(['opus'], { type: this.mimeType }) })
      this.onstop?.()
    }, 0)
  }
}

class FakeAudio {
  src: string
  paused = true
  onended: (() => void) | null = null
  onerror: (() => void) | null = null
  constructor(src: string) {
    this.src = src
    sounds.push(this)
  }
  play() {
    this.paused = false
    return Promise.resolve()
  }
  pause() {
    this.paused = true
  }
}

const saved = { ...tuning }

beforeEach(() => {
  mic = 0.001
  tracks.stopped = 0
  sounds.length = 0
  Object.assign(tuning, { frameMs: 10, speechMs: 30, silenceMs: 60, bargeInMs: 40 })
  vi.stubGlobal('AudioContext', FakeAudioContext)
  vi.stubGlobal('MediaRecorder', FakeRecorder)
  vi.stubGlobal('Audio', FakeAudio)
  Object.defineProperty(navigator, 'mediaDevices', {
    configurable: true,
    value: { getUserMedia: vi.fn(async () => ({ getTracks: () => [{ stop: () => tracks.stopped++ }] })) },
  })
  URL.createObjectURL = vi.fn(() => 'blob:sound')
  URL.revokeObjectURL = vi.fn()
})

afterEach(() => {
  Object.assign(tuning, saved)
  vi.unstubAllGlobals()
  Reflect.deleteProperty(navigator, 'mediaDevices')
})

function backend(extra: Parameters<typeof fakeApi>[1] = {}) {
  return fakeApi(member, {
    'GET /api/chat/config': () => ({ json: config }),
    'GET /api/chat/conversations': () => ({ json: [] }),
    'POST /api/chat/conversations': () => ({ status: 201, json: conversation }),
    'GET /api/chat/conversations/c1': () => ({ json: conversation }),
    'POST /api/chat/transcribe': () => ({ json: { text: 'What is the capital of France?' } }),
    // The answer's first sentence is written, the second is still being written.
    'POST /api/chat/conversations/c1/messages': () => ({
      events: [
        { type: 'question', id: 'q1', parentId: null },
        { type: 'assistant', id: 'a1', parentId: 'q1', model: 'Main-Model' },
        { type: 'content', text: 'Paris is the capital of France. ' },
        { type: 'content', text: 'It lies on the' },
      ],
      hang: true,
    }),
    'POST /api/chat/speech': () => ({ json: {} }),
    'POST /api/chat/conversations/c1/stop': () => ({ status: 202 }),
    ...extra,
  })
}

describe('talk', () => {
  it('a spoken question is answered aloud from its first sentence, and speaking over the answer stops it', async () => {
    const calls = backend()
    renderApp('/chat')
    await userEvent.click(await screen.findByRole('button', { name: 'Talk' }))
    // The bar moves from the new chat's page to the thread's: found again each time.
    const status = () => screen.getByRole('status', { name: 'Talk' })
    await waitFor(() => expect(status()).toHaveTextContent('Listening: speak when you are ready.'))

    // Speaking, then a pause: what was said is written down and asked, as said aloud.
    mic = 0.2
    await waitFor(() => expect(status()).toHaveTextContent('Hearing you…'))
    mic = 0.001
    await waitFor(() => expect(calls.some((c) => c.path === '/api/chat/transcribe')).toBe(true))
    const recording = (calls.find((c) => c.path === '/api/chat/transcribe')!.body as FormData).get('file') as File
    expect(recording.name).toBe('talk.webm')
    await waitFor(() =>
      expect(calls.find((c) => c.path === '/api/chat/conversations/c1/messages')?.body).toEqual({ content: 'What is the capital of France?', attachments: [], root: true, spoken: true }),
    )

    // The first sentence is read aloud while the answer is still being written.
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/chat/speech').map((c) => c.body)).toEqual([{ text: 'Paris is the capital of France.' }]))
    await waitFor(() => expect(sounds.some((s) => !s.paused)).toBe(true))
    await waitFor(() => expect(status()).toHaveTextContent('Speaking… speak over it to stop it.'))
    expect(await screen.findByText(/It lies on the/)).toBeInTheDocument()
    expect(calls.some((c) => c.path === '/api/chat/conversations/c1/stop')).toBe(false)

    // Speaking over it: the reading stops, and so does the answer.
    mic = 0.2
    await waitFor(() => expect(sounds.every((s) => s.paused)).toBe(true))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/chat/conversations/c1/stop')).toBe(true))
    await waitFor(() => expect(status()).toHaveTextContent('Hearing you…'))

    // The way out: the microphone is let go.
    mic = 0.001
    await userEvent.click(screen.getByRole('button', { name: 'End talk' }))
    expect(screen.queryByRole('status', { name: 'Talk' })).not.toBeInTheDocument()
    expect(tracks.stopped).toBe(1)
    expect(screen.getByRole('button', { name: 'Talk' })).toHaveAttribute('aria-pressed', 'false')
  })

  it('each sentence is read with the answer before it, so a short one is read in the answer’s language', async () => {
    const calls = backend({
      'POST /api/chat/conversations/c1/messages': () => ({
        events: [
          { type: 'question', id: 'q1', parentId: null },
          { type: 'assistant', id: 'a1', parentId: 'q1', model: 'Main-Model' },
          { type: 'content', text: '¿Dónde está la estación de tren? ' },
          { type: 'content', text: 'Claro que sí. Está' },
        ],
        hang: true,
      }),
    })
    renderApp('/chat')
    await userEvent.click(await screen.findByRole('button', { name: 'Talk' }))
    const status = () => screen.getByRole('status', { name: 'Talk' })
    await waitFor(() => expect(status()).toHaveTextContent('Listening: speak when you are ready.'))
    mic = 0.2
    await waitFor(() => expect(status()).toHaveTextContent('Hearing you…'))
    mic = 0.001
    await waitFor(() =>
      expect(calls.filter((c) => c.path === '/api/chat/speech').map((c) => c.body)).toEqual([
        { text: '¿Dónde está la estación de tren?' },
        { text: 'Claro que sí.', context: '¿Dónde está la estación de tren?' },
      ]),
    )
    await userEvent.click(screen.getByRole('button', { name: 'End talk' }))
  })

  it('with reading aloud turned off (Your account → Voice) the answer is only shown, and speaking still stops it', async () => {
    const voice = {
      chosen: { language: null, voices: {}, speed: null, readAloud: false },
      company: { language: 'auto', voices: {}, speed: 1, readAloud: true },
      voices: [],
      languages: [],
      hears: true,
      known: true,
    }
    const calls = backend({ 'GET /api/account/voice': () => ({ json: voice }) })
    renderApp('/chat')
    await userEvent.click(await screen.findByRole('button', { name: 'Talk' }))
    const status = () => screen.getByRole('status', { name: 'Talk' })
    await waitFor(() => expect(status()).toHaveTextContent('Listening: speak when you are ready.'))
    mic = 0.2
    await waitFor(() => expect(status()).toHaveTextContent('Hearing you…'))
    mic = 0.001
    expect(await screen.findByText(/It lies on the/)).toBeInTheDocument()
    await waitFor(() => expect(status()).toHaveTextContent('Thinking… speak to stop it.'))
    expect(calls.some((c) => c.path === '/api/chat/speech')).toBe(false)
    expect(sounds).toHaveLength(0)

    mic = 0.2
    await waitFor(() => expect(calls.some((c) => c.path === '/api/chat/conversations/c1/stop')).toBe(true))
    mic = 0.001
    await userEvent.click(screen.getByRole('button', { name: 'End talk' }))
  })

  it('a microphone that is refused says so, and Talk stays off', async () => {
    backend()
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: { getUserMedia: vi.fn(async () => Promise.reject(new DOMException('Permission denied', 'NotAllowedError'))) },
    })
    renderApp('/chat')
    await userEvent.click(await screen.findByRole('button', { name: 'Talk' }))
    expect(await screen.findByText('The microphone is not available')).toBeInTheDocument()
    expect(screen.queryByRole('status', { name: 'Talk' })).not.toBeInTheDocument()
  })

  it('without a microphone in the browser there is no Talk button', async () => {
    backend()
    Reflect.deleteProperty(navigator, 'mediaDevices')
    renderApp('/chat')
    expect(await screen.findByRole('textbox', { name: 'Message' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Talk' })).not.toBeInTheDocument()
  })
})
