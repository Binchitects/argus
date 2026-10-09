import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { voicePairs, voiceValue, type SpeechOffer } from '@/lib/voice'
import { admin, fakeApi, renderApp, type Call } from '@/test/utils'
import type { SettingsData, SettingView } from './settings-model'

const setting = (over: Partial<SettingView>): SettingView => ({
  key: 'Speech:Voices', group: 'Speech', label: 'Voice for each language', help: 'The voice that reads each language aloud.', type: 'text', scope: 'live',
  options: null, min: null, max: 1000, patternHelp: 'language:model/voice pairs', unit: null, optional: false, impact: null, dangerous: false,
  default: 'en:kokoro/af_heart,fa:piper-fa/gyro', value: 'en:kokoro/af_heart,fa:piper-fa/gyro', isSet: true, source: 'default', environmentValue: null,
  restartPending: false, ...over,
})

function speech(voices = 'en:kokoro/af_heart,fa:piper-fa/gyro', warning: string | null = null, language = 'auto'): SettingsData {
  return {
    groups: [
      {
        title: 'Speech',
        settings: [
          setting({ key: 'Speech:Language', label: 'Language people speak', value: language, default: 'auto', max: 4, patternHelp: 'auto, or the code of a language Whisper knows' }),
          setting({ value: voices, warning }),
          setting({ key: 'Speech:Speed', label: 'Reading speed', type: 'number', value: '1', default: '1', min: 0.5, max: 2, patternHelp: null }),
        ],
      },
    ],
    restartNeeded: false,
  }
}

const offered = (): SpeechOffer => ({
  voices: [
    { id: 'kokoro/af_heart', model: 'kokoro', name: 'af_heart', language: 'en', accent: 'en-us', gender: 'female' },
    { id: 'kokoro/am_adam', model: 'kokoro', name: 'am_adam', language: 'en', accent: 'en-us', gender: 'male' },
    { id: 'kokoro/ef_dora', model: 'kokoro', name: 'ef_dora', language: 'es', accent: 'es', gender: 'female' },
    { id: 'kokoro/pf_dora', model: 'kokoro', name: 'pf_dora', language: 'pt', accent: 'pt-br', gender: 'female' },
    { id: 'kokoro/pm_alex', model: 'kokoro', name: 'pm_alex', language: 'pt', accent: 'pt-br', gender: 'male' },
    { id: 'piper-fa/gyro', model: 'piper-fa', name: 'gyro', language: 'fa', accent: 'fa', gender: null },
  ],
  models: ['kokoro', 'piper-fa'],
  languages: ['en', 'fa', 'de', 'es'],
  known: true,
})

class FakeAudio {
  paused = true
  onended: (() => void) | null = null
  play() {
    this.paused = false
    return Promise.resolve()
  }
  pause() {
    this.paused = true
  }
}

beforeEach(() => {
  vi.stubGlobal('Audio', FakeAudio)
  URL.createObjectURL = vi.fn(() => 'blob:sample')
  URL.revokeObjectURL = vi.fn()
})

afterEach(() => {
  vi.unstubAllGlobals()
})

function backend(data = speech(), offer = offered()) {
  return fakeApi(admin, {
    'GET /api/admin/config': () => ({ json: data }),
    'PUT /api/admin/config': () => ({ json: data }),
    'GET /api/admin/speech/voices': () => ({ json: offer }),
    'POST /api/admin/speech/try': () => ({ json: {} }),
  })
}

const tried = (calls: Call[]) => calls.filter((c) => c.path === '/api/admin/speech/try').at(-1)?.body

describe('settings → speech → voice for each language', () => {
  it('each language’s voice is chosen from those offered, tried at the speed on the page, and saved as the setting’s pairs', async () => {
    const calls = backend()
    renderApp('/admin/settings#speech')
    const portuguese = await screen.findByRole('combobox', { name: 'Voice for Portuguese' })
    // A row for each language a voice reads, by name; one the value does not name has the first offered.
    expect(screen.getAllByRole('combobox', { name: /^Voice for / }).map((c) => c.getAttribute('aria-label'))).toEqual([
      'Voice for English',
      'Voice for Persian',
      'Voice for Portuguese',
      'Voice for Spanish',
    ])
    expect(screen.getByRole('combobox', { name: 'Voice for English' })).toHaveTextContent('Heart (woman, American English) kokoro/af_heart')
    expect(portuguese).toHaveTextContent('The first offered: Dora (woman, Brazilian Portuguese)')
    await userEvent.click(portuguese)
    // Each voice by its name, with its id.
    expect((await screen.findAllByRole('option')).map((o) => o.textContent)).toEqual([
      'The first offered: Dora (woman, Brazilian Portuguese)',
      'Dora (woman, Brazilian Portuguese) kokoro/pf_dora',
      'Alex (man, Brazilian Portuguese) kokoro/pm_alex',
    ])
    await userEvent.click(screen.getByRole('option', { name: /Alex/ }))

    // Tried before it is saved, at the speed typed on the page.
    const speed = screen.getByLabelText('Reading speed')
    await userEvent.clear(speed)
    await userEvent.type(speed, '1.5')
    await userEvent.click(screen.getByRole('button', { name: 'Try it: Portuguese' }))
    await waitFor(() => expect(tried(calls)).toEqual({ voice: 'kokoro/pm_alex', language: 'pt', speed: 1.5 }))
    // Left to the first offered: that one is tried; a speed out of range is left to the company's.
    await userEvent.clear(speed)
    await userEvent.type(speed, '7')
    await userEvent.click(screen.getByRole('button', { name: 'Try it: Spanish' }))
    await waitFor(() => expect(tried(calls)).toEqual({ voice: 'kokoro/ef_dora', language: 'es' }))
    await userEvent.clear(speed)
    await userEvent.type(speed, '1.5')

    // English back to the first offered: its pair goes.
    await userEvent.click(screen.getByRole('combobox', { name: 'Voice for English' }))
    await userEvent.click(await screen.findByRole('option', { name: /^The first offered/ }))
    const bar = screen.getByRole('region', { name: 'Unsaved changes' })
    await userEvent.click(within(bar).getByRole('button', { name: 'Save changes' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({
        changes: [
          { key: 'Speech:Voices', value: 'fa:piper-fa/gyro,pt:kokoro/pm_alex' },
          { key: 'Speech:Speed', value: '1.5' },
        ],
      }),
    )
  })

  it('the last language named, left to the first offered, names that voice', async () => {
    const calls = backend(speech('en:kokoro/am_adam'))
    renderApp('/admin/settings#speech')
    await userEvent.click(await screen.findByRole('combobox', { name: 'Voice for English' }))
    await userEvent.click(await screen.findByRole('option', { name: /^The first offered/ }))
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ changes: [{ key: 'Speech:Voices', value: 'en:kokoro/af_heart' }] }))
  })

  it('a voice not offered is said, one of a model not listed yet is tried as named, and the value can be edited as text', async () => {
    const offer = offered()
    offer.voices = offer.voices.filter((v) => v.model === 'kokoro')
    const calls = backend(speech('en:kokoro/emma,fa:piper-fa/amir', 'en:kokoro/emma is not a voice offered for en, so kokoro/af_heart reads it.'), offer)
    renderApp('/admin/settings#speech')
    const english = await screen.findByRole('combobox', { name: 'Voice for English' })
    expect(english).toHaveTextContent('kokoro/emma (not offered)')
    expect(screen.getByText('kokoro/emma is not offered: kokoro/af_heart reads English')).toBeInTheDocument()
    expect(screen.getByText(/so kokoro\/af_heart reads it/)).toBeInTheDocument()
    // Persian's model is still downloading: its voice is used as named, and tried so.
    expect(screen.getByRole('combobox', { name: 'Voice for Persian' })).toHaveTextContent('piper-fa/amir (its model is not listed yet)')
    await userEvent.click(screen.getByRole('button', { name: 'Try it: Persian' }))
    await waitFor(() => expect(tried(calls)).toEqual({ voice: 'piper-fa/amir', language: 'fa', speed: 1 }))
    await userEvent.click(screen.getByRole('button', { name: 'Try it: English' }))
    await waitFor(() => expect(tried(calls)).toEqual({ voice: 'kokoro/af_heart', language: 'en', speed: 1 }))

    await userEvent.click(screen.getByRole('button', { name: 'Edit as text' }))
    const text = screen.getByLabelText('Voice for each language')
    expect(text).toHaveValue('en:kokoro/emma,fa:piper-fa/amir')
    await userEvent.clear(text)
    await userEvent.type(text, 'en:kokoro/am_adam')
    await userEvent.click(screen.getByRole('button', { name: 'Choose from the voices offered' }))
    expect(await screen.findByRole('combobox', { name: 'Voice for English' })).toHaveTextContent('Adam (man, American English)')
    expect(screen.queryByRole('combobox', { name: 'Voice for Persian' })).not.toBeInTheDocument()
  })

  it('while the speech server cannot be asked, the value is typed as text', async () => {
    const calls = backend(speech(), { voices: [], models: ['kokoro', 'piper-fa'], languages: ['en', 'fa'], known: false })
    renderApp('/admin/settings#speech')
    const text = await screen.findByLabelText('Voice for each language')
    expect(text).toHaveValue('en:kokoro/af_heart,fa:piper-fa/gyro')
    expect(screen.getByText(/The speech server cannot be asked right now/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Choose from the voices offered' })).not.toBeInTheDocument()
    await userEvent.type(text, ',es:kokoro/ef_dora')
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ changes: [{ key: 'Speech:Voices', value: 'en:kokoro/af_heart,fa:piper-fa/gyro,es:kokoro/ef_dora' }] }),
    )
  })

  it('the language people speak is auto or one speech to text knows, by name with its code', async () => {
    const calls = backend(speech(undefined, null, 'it'))
    renderApp('/admin/settings#speech')
    const language = await screen.findByRole('combobox', { name: 'Language people speak' })
    // A code speech to text does not list stays shown as such.
    expect(language).toHaveTextContent('it (not known here)')
    await userEvent.click(language)
    expect((await screen.findAllByRole('option')).map((o) => o.textContent)).toEqual([
      'Detect it (auto)',
      'it (not known here)',
      'English en',
      'German de',
      'Persian fa',
      'Spanish es',
    ])
    await userEvent.click(screen.getByRole('option', { name: 'Persian fa' }))
    await userEvent.click(screen.getByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ changes: [{ key: 'Speech:Language', value: 'fa' }] }))
  })

  it('reads the value as the app does: pairs in order, the first for a language kept, the rest left out', () => {
    expect(voicePairs(' EN:kokoro/af_heart , fa:piper-fa/gyro,en:kokoro/am_adam,kokoro,de:x, es:kokoro/ef_dora ')).toEqual([
      ['en', 'kokoro/af_heart'],
      ['fa', 'piper-fa/gyro'],
      ['es', 'kokoro/ef_dora'],
    ])
    expect(voicePairs('')).toEqual([])
    expect(voiceValue([['en', 'kokoro/af_heart'], ['pt', 'kokoro/pm_alex']])).toEqual('en:kokoro/af_heart,pt:kokoro/pm_alex')
  })
})
