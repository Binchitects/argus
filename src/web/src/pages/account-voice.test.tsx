import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { VoiceChoices, VoiceSettings } from '@/lib/voice'
import { fakeApi, member, renderApp, type Call } from '@/test/utils'

const offered = (): VoiceSettings => ({
  chosen: { language: null, voices: {}, speed: null, readAloud: null },
  company: { language: 'auto', voices: { en: 'kokoro/af_heart', es: 'kokoro/ef_dora', fa: 'piper-fa/gyro' }, speed: 1, readAloud: true },
  voices: [
    { id: 'kokoro/af_heart', model: 'kokoro', name: 'af_heart', language: 'en', accent: 'en-us', gender: 'female' },
    { id: 'kokoro/am_adam', model: 'kokoro', name: 'am_adam', language: 'en', accent: 'en-us', gender: 'male' },
    { id: 'kokoro/bf_emma', model: 'kokoro', name: 'bf_emma', language: 'en', accent: 'en-gb', gender: 'female' },
    { id: 'kokoro/ef_dora', model: 'kokoro', name: 'ef_dora', language: 'es', accent: 'es', gender: 'female' },
    { id: 'piper-fa/gyro', model: 'piper-fa', name: 'gyro', language: 'fa', accent: 'fa', gender: null },
  ],
  languages: ['en', 'fa', 'de', 'es'],
  hears: true,
  known: true,
})

/** The speech server's sound, played: what was played, and whether it is playing now. */
const played: { src: string; paused: boolean; onended: (() => void) | null }[] = []

class FakeAudio {
  src: string
  paused = true
  onended: (() => void) | null = null
  constructor(src: string) {
    this.src = src
    played.push(this)
  }
  play() {
    this.paused = false
    return Promise.resolve()
  }
  pause() {
    this.paused = true
  }
}

beforeEach(() => {
  played.length = 0
  vi.stubGlobal('Audio', FakeAudio)
  URL.createObjectURL = vi.fn(() => 'blob:sample')
  URL.revokeObjectURL = vi.fn()
})

afterEach(() => {
  vi.unstubAllGlobals()
})

/** The person's voice as the server keeps it: each PUT replaces their choices. */
function backend(settings = offered()) {
  let state = settings
  const calls = fakeApi(member, {
    'GET /api/account/voice': () => ({ json: state }),
    'PUT /api/account/voice': (body) => {
      state = { ...state, chosen: body as VoiceChoices }
      return { json: state }
    },
    'POST /api/account/voice/try': () => ({ json: {} }),
  })
  return calls
}

const lastSaved = (calls: Call[]) => calls.filter((c) => c.method === 'PUT' && c.path === '/api/account/voice').at(-1)?.body

describe('your voice', () => {
  it('the language spoken, a voice per language, the speed and reading aloud are saved, and all go back to the company’s', async () => {
    const calls = backend()
    renderApp('/account')
    const language = await screen.findByRole('combobox', { name: 'The language you speak' })
    expect(language).toHaveTextContent("The company's: Detect it")
    await userEvent.click(language)
    await userEvent.click(await screen.findByRole('option', { name: 'Persian' }))
    await waitFor(() => expect(lastSaved(calls)).toEqual({ language: 'fa', voices: {}, speed: null, readAloud: null }))

    // A row per language a voice reads, the language spoken first; each starts as the company's.
    const rows = screen.getAllByRole('combobox', { name: /^Voice for / }).map((c) => c.getAttribute('aria-label'))
    expect(rows).toEqual(['Voice for Persian', 'Voice for English', 'Voice for Spanish'])
    const english = screen.getByRole('combobox', { name: 'Voice for English' })
    expect(english).toHaveTextContent("The company's: Heart (woman, American English)")
    await userEvent.click(english)
    const options = await screen.findAllByRole('option')
    expect(options.map((o) => o.textContent)).toEqual([
      "The company's: Heart (woman, American English)",
      'Heart (woman, American English)',
      'Adam (man, American English)',
      'Emma (woman, British English)',
    ])
    await userEvent.click(screen.getByRole('option', { name: 'Adam (man, American English)' }))
    await waitFor(() => expect(lastSaved(calls)).toEqual({ language: 'fa', voices: { en: 'kokoro/am_adam' }, speed: null, readAloud: null }))
    expect(screen.getByRole('combobox', { name: 'Voice for Persian' })).toHaveTextContent("The company's: Gyro")

    // The speed is saved once the slider rests.
    fireEvent.change(screen.getByRole('slider', { name: 'Speed' }), { target: { value: '1.25' } })
    expect(screen.getByText('1.25×')).toBeInTheDocument()
    await waitFor(() => expect(lastSaved(calls)).toEqual({ language: 'fa', voices: { en: 'kokoro/am_adam' }, speed: 1.25, readAloud: null }))

    const aloud = screen.getByRole('switch', { name: 'Read answers aloud in Talk' })
    expect(aloud).toBeChecked()
    await userEvent.click(aloud)
    await waitFor(() => expect(lastSaved(calls)).toEqual({ language: 'fa', voices: { en: 'kokoro/am_adam' }, speed: 1.25, readAloud: false }))
    await waitFor(() => expect(screen.getByRole('switch', { name: 'Read answers aloud in Talk' })).not.toBeChecked())

    await userEvent.click(screen.getByRole('button', { name: "Use the company's" }))
    await waitFor(() => expect(lastSaved(calls)).toEqual({ language: null, voices: {}, speed: null, readAloud: null }))
    await waitFor(() => expect(screen.queryByRole('button', { name: "Use the company's" })).not.toBeInTheDocument())
    expect(screen.getByRole('switch', { name: 'Read answers aloud in Talk' })).toBeChecked()
  })

  it('Try it reads a sample in the voice shown for its language, at the person’s speed, and again stops it', async () => {
    const settings = offered()
    settings.chosen = { language: null, voices: { en: 'kokoro/bf_emma' }, speed: 1.5, readAloud: null }
    const calls = backend(settings)
    renderApp('/account')
    const english = await screen.findByRole('button', { name: 'Try it: English' })
    await userEvent.click(english)
    await waitFor(() => expect(calls.find((c) => c.path === '/api/account/voice/try')?.body).toEqual({ voice: 'kokoro/bf_emma', language: 'en', speed: 1.5 }))
    await waitFor(() => expect(played.some((p) => !p.paused)).toBe(true))
    // Playing: the same button stops it.
    await userEvent.click(await screen.findByRole('button', { name: 'Stop: English' }))
    await waitFor(() => expect(played.every((p) => p.paused)).toBe(true))
    expect(screen.getByRole('button', { name: 'Try it: English' })).toBeInTheDocument()

    // A language left to the company: its voice.
    await userEvent.click(screen.getByRole('button', { name: 'Try it: Persian' }))
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/account/voice/try').at(-1)?.body).toEqual({ voice: 'piper-fa/gyro', language: 'fa', speed: 1.5 }))
  })

  it('says when reading aloud or speech to text is not set up, and when only the voices chosen can be listed', async () => {
    const none = offered()
    none.voices = []
    none.company.voices = {}
    none.hears = false
    backend(none)
    const { unmount } = renderApp('/account')
    expect(await screen.findByText(/Reading aloud is not set up here/)).toBeInTheDocument()
    expect(screen.getByText('Speech to text is not set up here (the audio module).')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /^Try it/ })).not.toBeInTheDocument()
    unmount()

    const unknown = offered()
    unknown.known = false
    unknown.voices = unknown.voices.filter((v) => v.id === 'kokoro/af_heart' || v.id === 'piper-fa/gyro')
    backend(unknown)
    renderApp('/account')
    expect(await screen.findByText(/The speech server cannot be asked right now/)).toBeInTheDocument()
    const card = screen.getByRole('heading', { name: 'Voice' }).closest('section')!
    expect(within(card).getAllByRole('combobox', { name: /^Voice for / })).toHaveLength(2)
  })
})
