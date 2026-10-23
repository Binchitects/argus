import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import { blank } from './live'
import { pageId } from './room-call'
import type { ChatConfig, Conversation, Message } from './types'
import { tuning } from './voice'

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: true, loaded: true, prices: { input: 0.2, cachedInput: 0.02, output: 0.8 } }],
  presets: [],
  defaultThinking: null,
  argus: false,
  tools: [],
  gitlabUrl: null,
  maxUploadBytes: 20 * 1024 * 1024,
  imageTypes: [],
}

const msg = (id: string, parentId: string | null, role: Message['role'], over: Partial<Message> = {}): Message => ({ ...blank(id, role, parentId), status: 'complete', ...over })

const room: Conversation = {
  id: 'r1', title: 'Launch', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: 'q1', archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '',
  messages: [msg('q1', null, 'user', { content: 'Call at ten?', author: { id: 'o1', name: 'Olivia' }, forPeople: true })],
  room: {
    role: 'member', membersInvite: false, answeringFor: null, groups: [],
    members: [{ id: 'o1', name: 'Olivia', role: 'owner' }, { id: member.id, name: member.displayName, role: 'member' }],
  },
}

// The microphone, as loud as the test says; the connections to the others' pages.
let mic = 0.001
const peers: FakePeer[] = []

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
  ondataavailable: ((e: { data: Blob }) => void) | null = null
  onstop: (() => void) | null = null
  start() {}
  stop() {
    setTimeout(() => {
      this.ondataavailable?.({ data: new Blob(['o'.repeat(3000)], { type: this.mimeType }) })
      this.onstop?.()
    }, 0)
  }
}

class FakePeer {
  localDescription: { toJSON: () => RTCSessionDescriptionInit } | null = null
  tracks = 0
  closed = false
  ontrack: ((e: unknown) => void) | null = null
  onicecandidate: ((e: unknown) => void) | null = null
  constructor() {
    peers.push(this)
  }
  addTrack() {
    this.tracks++
  }
  createOffer() {
    return Promise.resolve({ type: 'offer', sdp: 'v=0 offer' })
  }
  createAnswer() {
    return Promise.resolve({ type: 'answer', sdp: 'v=0 answer' })
  }
  setLocalDescription(d: RTCSessionDescriptionInit) {
    this.localDescription = { toJSON: () => d }
    return Promise.resolve()
  }
  setRemoteDescription() {
    return Promise.resolve()
  }
  addIceCandidate() {
    return Promise.resolve()
  }
  close() {
    this.closed = true
  }
}

const track = { enabled: true, stop: vi.fn() }
const saved = { ...tuning }

beforeEach(() => {
  mic = 0.001
  peers.length = 0
  track.enabled = true
  Object.assign(tuning, { frameMs: 10, speechMs: 30, silenceMs: 60 })
  vi.stubGlobal('AudioContext', FakeAudioContext)
  vi.stubGlobal('MediaRecorder', FakeRecorder)
  vi.stubGlobal('RTCPeerConnection', FakePeer)
  vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue()
  Object.defineProperty(navigator, 'mediaDevices', {
    configurable: true,
    value: { getUserMedia: vi.fn(async () => ({ getTracks: () => [track], getAudioTracks: () => [track] })) },
  })
  localStorage.setItem('arena.room.readIn', 'en')
})

afterEach(() => {
  Object.assign(tuning, saved)
  vi.unstubAllGlobals()
  Reflect.deleteProperty(navigator, 'mediaDevices')
  localStorage.removeItem('arena.room.readIn')
})

describe('calls in rooms', () => {
  it('joins the room\'s call, calls the others, shows what was said in the reader\'s language, and sends what is said', async () => {
    const olivia = { page: 'p-olivia', person: 'o1', muted: false }
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [{ id: 'r1', title: 'Launch', updatedAt: new Date().toISOString(), isRoom: true, mine: false }] }),
      'GET /api/chat/conversations/r1': () => ({ json: room }),
      'GET /api/rooms/languages': () => ({ json: [{ code: 'en', name: 'English', own: 'English' }, { code: 'es', name: 'Spanish', own: 'Español' }] }),
      'POST /api/rooms/r1/translations': () => ({ json: { translations: [], error: null } }),
      'GET /api/rooms/r1/events': () => ({ events: [{ type: 'call', seats: [olivia] }], hang: true }),
      'POST /api/rooms/r1/call': (body) => ({
        json: {
          seats: [olivia, { page: pageId, person: member.id, muted: (body as { muted: boolean }).muted }],
          iceServers: [],
          captions: [{ id: 'k1', page: 'p-olivia', by: 'o1', text: 'Hola a todos', at: '' }],
        },
      }),
      'POST /api/rooms/r1/call/signal': () => ({ status: 204 }),
      'POST /api/rooms/r1/call/captions/k1/translation': () => ({ json: { id: 'k1', language: 'en', text: 'Hello everyone', same: false, error: null } }),
      'POST /api/rooms/r1/call/captions': () => ({ json: { id: 'k2', page: pageId, by: member.id, text: 'Hi Olivia', at: '' } }),
      'DELETE /api/rooms/r1/call': () => ({ status: 204 }),
    })
    renderApp('/chat/r1')
    const bar = await screen.findByRole('region', { name: 'The call' })
    expect(within(bar).getByRole('status', { name: 'In the call' })).toHaveTextContent('Olivia is in a call.')
    expect(calls.find((c) => c.path.startsWith('/api/rooms/r1/events'))?.path).toBe(`/api/rooms/r1/events?page=${pageId}`)

    // Joined: this page calls Olivia's, and reads what she said in English.
    await userEvent.click(within(bar).getByRole('button', { name: 'Join' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/rooms/r1/call')?.body).toEqual({ page: pageId, language: 'en', muted: false }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/rooms/r1/call/signal')?.body).toEqual({ page: pageId, to: 'p-olivia', signal: { description: { type: 'offer', sdp: 'v=0 offer' } } }))
    expect(peers[0]!.tracks).toBe(1)
    expect(within(bar).getByRole('status', { name: 'In the call' })).toHaveTextContent('In the call with Olivia.')
    const captions = within(bar).getByRole('list', { name: 'Captions' })
    expect(await within(captions).findByText('Hello everyone')).toBeInTheDocument()
    await userEvent.click(within(captions).getByRole('button', { name: 'Show the original' }))
    expect(within(captions).getByText('Hola a todos')).toBeInTheDocument()

    // Speaking, then a pause: what was said goes to be written down for everyone.
    mic = 0.2
    await new Promise((r) => setTimeout(r, 80))
    mic = 0.001
    await waitFor(() => expect(calls.some((c) => c.path === '/api/rooms/r1/call/captions')).toBe(true))
    const sent = calls.find((c) => c.path === '/api/rooms/r1/call/captions')!.body as FormData
    expect(sent.get('page')).toBe(pageId)
    expect((sent.get('file') as File).type).toBe('audio/webm;codecs=opus')

    // Muted: the microphone is off for the others; leaving lets it go.
    await userEvent.click(within(bar).getByRole('button', { name: 'Mute' }))
    expect(track.enabled).toBe(false)
    await waitFor(() => expect(calls.filter((c) => c.method === 'POST' && c.path === '/api/rooms/r1/call').at(-1)?.body).toEqual({ page: pageId, language: 'en', muted: true }))
    await userEvent.click(within(bar).getByRole('button', { name: 'Leave' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.path === `/api/rooms/r1/call?page=${pageId}`)).toBe(true))
    expect(peers[0]!.closed).toBe(true)
    expect(track.stop).toHaveBeenCalled()
  })
})
