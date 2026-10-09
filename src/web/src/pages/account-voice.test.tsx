import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { VoiceChange, VoiceSettings } from '@/lib/voice'
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

/** The person's voice as the server keeps it: each PATCH changes the choices it names, and only those. */
function backend(settings = offered()) {
  let state = settings
  const calls = fakeApi(member, {
    'GET /api/account/voice': () => ({ json: state }),
    'PATCH /api/account/voice': (body) => {
      const change = body as VoiceChange
      const chosen = { ...state.chosen }
      if ('language' in change) chosen.language = change.language ?? null
      if ('speed' in change) chosen.speed = change.speed ?? null
      if ('readAloud' in change) chosen.readAloud = change.readAloud ?? null
      if ('voices' in change) {
        const voices = change.voices ? { ...chosen.voices } : {}
        for (const [language, id] of Object.entries(change.voices ?? {})) {
          if (id) voices[language] = id
          else delete voices[language]
        }
        chosen.voices = voices
      }
      state = { ...state, chosen }
      return { json: state }
    },
    'POST /api/account/voice/try': () => ({ json: {} }),
  })
  return { calls, now: () => state }
}

const lastSaved = (calls: Call[]) => calls.filter((c) => c.method === 'PATCH' && c.path === '/api/account/voice').at(-1)?.body

describe('your voice', () => {
  it('the language spoken, a voice per language, the speed and reading aloud are saved one by one, and all go back to the company’s', async () => {
    const { calls, now } = backend()
    renderApp('/account')
    const language = await screen.findByRole('combobox', { name: 'The language you speak' })
    expect(language).toHaveTextContent("The company's: Detect it")
    await userEvent.click(language)
    await userEvent.click(await screen.findByRole('option', { name: 'Persian' }))
    await waitFor(() => expect(lastSaved(calls)).toEqual({ language: 'fa' }))

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
    await waitFor(() => expect(lastSaved(calls)).toEqual({ voices: { en: 'kokoro/am_adam' } }))
    expect(screen.getByRole('combobox', { name: 'Voice for Persian' })).toHaveTextContent("The company's: Gyro")
    // Each voice's id, as Settings → Speech names it.
    expect(screen.getByText('kokoro/am_adam')).toBeInTheDocument()
    expect(screen.getByText('piper-fa/gyro')).toBeInTheDocument()

    // The speed is saved once the slider rests; the switch flipped meanwhile is kept too.
    fireEvent.change(screen.getByRole('slider', { name: 'Speed' }), { target: { value: '1.25' } })
    expect(screen.getByText('1.25×')).toBeInTheDocument()
    const aloud = screen.getByRole('switch', { name: 'Read answers aloud in Talk' })
    expect(aloud).toBeChecked()
    await userEvent.click(aloud)
    await waitFor(() => expect(calls.filter((c) => c.method === 'PATCH').map((c) => c.body)).toContainEqual({ speed: 1.25 }))
    expect(calls.filter((c) => c.method === 'PATCH').map((c) => c.body)).toContainEqual({ readAloud: false })
    expect(now().chosen).toEqual({ language: 'fa', voices: { en: 'kokoro/am_adam' }, speed: 1.25, readAloud: false })
    await waitFor(() => expect(screen.getByRole('switch', { name: 'Read answers aloud in Talk' })).not.toBeChecked())
    expect(screen.getByText('1.25×')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: "Use the company's" }))
    await waitFor(() => expect(lastSaved(calls)).toEqual({ language: null, voices: null, speed: null, readAloud: null }))
    await waitFor(() => expect(screen.queryByRole('button', { name: "Use the company's" })).not.toBeInTheDocument())
    expect(screen.getByRole('switch', { name: 'Read answers aloud in Talk' })).toBeChecked()
  })

  it('Try it reads a sample in the voice shown for its language, at the person’s speed, and again stops it', async () => {
    const settings = offered()
    settings.chosen = { language: null, voices: { en: 'kokoro/bf_emma' }, speed: 1.5, readAloud: null }
    const { calls } = backend(settings)
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

  it('a voice of theirs no longer offered is shown as such, the company’s reads meanwhile, and it alone goes back to the company’s', async () => {
    const settings = offered()
    settings.chosen = { language: null, voices: { en: 'kokoro/af_bella', pt: 'kokoro/pf_dora' }, speed: null, readAloud: null }
    const { calls, now } = backend(settings)
    renderApp('/account')
    const english = await screen.findByRole('combobox', { name: 'Voice for English' })
    expect(english).toHaveTextContent('kokoro/af_bella (not offered now)')
    // Said in full across the row, wrapped, and not cut short on a phone (the select's own value is).
    const said = screen.getByText('kokoro/af_bella is not offered now: kokoro/af_heart reads English')
    expect(said).toHaveClass('col-span-full', 'break-words')
    expect(said).not.toHaveClass('truncate')
    expect(said).not.toHaveAttribute('title')
    // A language no voice reads now still has its row, to put it back.
    expect(screen.getByRole('combobox', { name: 'Voice for Portuguese' })).toBeInTheDocument()
    expect(screen.getByText('kokoro/pf_dora is not offered now: no voice reads Portuguese')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Try it: Portuguese' })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Try it: English' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/account/voice/try')?.body).toEqual({ voice: 'kokoro/af_heart', language: 'en', speed: 1 }))

    // Other changes save as usual.
    await userEvent.click(screen.getByRole('switch', { name: 'Read answers aloud in Talk' }))
    await waitFor(() => expect(lastSaved(calls)).toEqual({ readAloud: false }))
    await userEvent.click(screen.getByRole('combobox', { name: 'Voice for Portuguese' }))
    await userEvent.click(await screen.findByRole('option', { name: "The company's: none" }))
    await waitFor(() => expect(lastSaved(calls)).toEqual({ voices: { pt: null } }))
    await waitFor(() => expect(screen.queryByRole('combobox', { name: 'Voice for Portuguese' })).not.toBeInTheDocument())
    expect(now().chosen).toEqual({ language: null, voices: { en: 'kokoro/af_bella' }, speed: null, readAloud: false })
  })

  it('the language the company says people speak comes first while the person leaves it to the company', async () => {
    const settings = offered()
    settings.company.language = 'fa'
    backend(settings)
    renderApp('/account')
    await screen.findByRole('combobox', { name: 'Voice for Persian' })
    const rows = screen.getAllByRole('combobox', { name: /^Voice for / }).map((c) => c.getAttribute('aria-label'))
    expect(rows).toEqual(['Voice for Persian', 'Voice for English', 'Voice for Spanish'])
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
