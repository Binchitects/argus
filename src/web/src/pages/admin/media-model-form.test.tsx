import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

// "Load, unload, remove and configure picture, video and speech models as LLMs": added in the one Add a model dialog,
// which takes a file from the library (or the speech server's model) as what it is, with their settings, GPU, idle time
// and price; edited from their card in the same dialog.

const everyone = { audience: 'Everyone', groups: [] }
const engine = {
  enabled: true, error: null, checkedAt: '2026-10-11T10:00:00Z', kept: [], pinned: [], max: 2, hours: null, onRequest: true, loaded: [], loading: [],
  unloading: [], gpus: [{ index: 0, name: 'NVIDIA GeForce RTX 3090', total: 25.7e9 }], plan: null,
}
const flux = {
  name: 'FLUX.2-klein-4B', source: 'media', mode: 'image_generation', server: 'imagegen', status: 'unloaded', kept: true, keptNow: true, vision: false, access: everyone,
  files: [
    { role: 'diffusion', path: 'image/flux/flux-2-klein-4b-Q4_0.gguf', present: true },
    { role: 'llm', path: 'image/flux/Qwen3-4B-Q4_K_M.gguf', present: true },
    { role: 'vae', path: 'image/flux/flux2-vae.safetensors', present: true },
  ],
  settings: { steps: 4, cfgScale: 1, maxVramGb: 2, vae: 'gpu', flashAttention: true, vaeTiling: false, offloadToCpu: false, voices: [] },
  unitPrice: { unit: 'picture', amount: 0.01, own: false },
  idleMinutes: null,
}
const kokoro = {
  name: 'kokoro', source: 'media', mode: 'audio_speech', server: 'speech', status: 'unloaded', kept: false, keptNow: false, vision: false, access: everyone,
  speechId: 'speaches-ai/Kokoro-82M-v1.0-ONNX', settings: { speed: 1, defaultVoice: 'af_heart', voices: [] }, unitPrice: { unit: '1,000 characters', amount: 0.01, own: false },
  idleMinutes: null,
}
const files = [
  { path: 'image/flux/flux-2-klein-4b-Q4_0.gguf', size: 2.4e9, role: 'diffusion', usedBy: ['FLUX.2-klein-4B'] },
  { path: 'image/flux/Qwen3-4B-Q4_K_M.gguf', size: 2.5e9, role: 'llm', usedBy: ['FLUX.2-klein-4B'] },
  { path: 'image/flux/flux2-vae.safetensors', size: 0.16e9, role: 'vae', usedBy: ['FLUX.2-klein-4B'] },
  { path: 'image/sdxl/sdxl-turbo.safetensors', size: 6.9e9, role: 'diffusion', usedBy: [] },
  { path: 'video/wan/Wan2.2-TI2V-5B.safetensors', size: 10e9, role: 'diffusion', usedBy: [] },
  { path: 'video/wan/umt5-xxl-enc.safetensors', size: 6e9, role: 't5xxl', usedBy: [] },
]
const speech = [
  { id: 'speaches-ai/Kokoro-82M-v1.0-ONNX', task: 'text-to-speech', language: ['multilingual'], voices: ['af_heart', 'am_adam'] },
  { id: 'deepdml/faster-whisper-large-v3-turbo-ct2', task: 'automatic-speech-recognition', language: [], voices: [] },
]

/** Opens Add a model and chooses what matches in its File or model. */
async function choose(option: RegExp) {
  if (!screen.queryByRole('dialog')) await userEvent.click(await screen.findByRole('button', { name: 'Add a model' }))
  const dialog = await screen.findByRole('dialog', { name: 'Add a model' })
  await userEvent.click(within(dialog).getByRole('combobox', { name: 'File or model' }))
  await userEvent.click(await screen.findByRole('option', { name: option }))
  return dialog
}

describe('admin models: picture, video and speech models', () => {
  it('adds a picture model from the library, with its steps, GPU budget, idle time and price', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: { engine, small: null, models: [flux] } }),
      'GET /api/admin/models/media/files': () => ({ json: files }),
      'POST /api/admin/models/media': () => ({ status: 201, json: { restarting: false, warning: null } }),
    })
    renderApp('/admin/models')
    await screen.findByRole('heading', { name: /FLUX/ })
    const dialog = await choose(/sdxl-turbo/)
    // Its name says it is a diffusion model: a picture model, and the name follows its file.
    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: 'Add it as' })).toHaveTextContent('A picture model (what it reads as)'))
    await waitFor(() => expect(within(dialog).getByLabelText('Name')).toHaveValue('sdxl-turbo'))
    await userEvent.type(within(dialog).getByLabelText('Steps'), '1')
    await userEvent.type(within(dialog).getByLabelText('GPU budget (GB)'), '4')
    await userEvent.type(within(dialog).getByLabelText(/Unload after/), '5')
    await userEvent.type(within(dialog).getByLabelText(/Price per picture/), '0.02')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add model' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/admin/models/media')).toBe(true))
    const sent = calls.find((c) => c.method === 'POST' && c.path === '/api/admin/models/media')!.body as Record<string, unknown>
    expect(sent).toMatchObject({
      name: 'sdxl-turbo', mode: 'image_generation', files: [{ role: 'diffusion', path: 'image/sdxl/sdxl-turbo.safetensors' }], steps: 1, maxVramGb: 4,
      idleMinutes: 5, unitPrice: 0.02, vae: 'gpu', flashAttention: true, kept: false,
    })
    expect(sent).not.toHaveProperty('clear')
  })

  it('adds a video model: its diffusion model read as video, and its text encoder put in by its role', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: { engine, small: null, models: [flux] } }),
      'GET /api/admin/models/media/files': () => ({ json: files }),
      'POST /api/admin/models/media': () => ({ status: 201, json: { restarting: false, warning: null } }),
    })
    renderApp('/admin/models')
    await screen.findByRole('heading', { name: /FLUX/ })
    const dialog = await choose(/Wan2\.2-TI2V-5B/)
    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: 'Add it as' })).toHaveTextContent('A video model (what it reads as)'))
    expect(within(dialog).getByLabelText('Frames a second')).toBeInTheDocument()
    // A text encoder chosen next is one of its files, beside the diffusion model, in its role.
    await choose(/umt5-xxl-enc/)
    expect(within(dialog).getByText(/one of a picture or video model's files/)).toBeInTheDocument()
    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: 'File 2' })).toHaveTextContent('video/wan/umt5-xxl-enc.safetensors'))
    expect(within(dialog).getByRole('combobox', { name: 'File 2: its role' })).toHaveTextContent('T5 text encoder')
    expect(within(dialog).getByRole('combobox', { name: 'Add it as' })).toHaveTextContent('A video model')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add model' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST')).toBe(true))
    expect(calls.find((c) => c.method === 'POST')!.body).toMatchObject({
      name: 'Wan2.2-TI2V-5B', mode: 'video_generation',
      files: [{ role: 'diffusion', path: 'video/wan/Wan2.2-TI2V-5B.safetensors' }, { role: 't5xxl', path: 'video/wan/umt5-xxl-enc.safetensors' }],
    })
  })

  it('edits a picture model from its card: what is emptied is cleared', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: { engine, small: null, models: [flux] } }),
      'GET /api/admin/models/media/files': () => ({ json: files }),
      'PATCH /api/admin/models/media/FLUX.2-klein-4B': () => ({ json: { restarting: false, warning: null } }),
    })
    renderApp('/admin/models')
    const card = (await screen.findByRole('heading', { name: /FLUX/ })).closest('section')!
    await userEvent.click(within(card).getByRole('button', { name: /Edit/ }))
    const dialog = await screen.findByRole('dialog', { name: 'Edit FLUX.2-klein-4B' })
    expect(within(dialog).getByRole('combobox', { name: 'Add it as' })).toBeDisabled()
    expect(within(dialog).getByRole('combobox', { name: 'Add it as' })).toHaveTextContent('A picture model')
    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: 'File or model' })).toHaveTextContent('image/flux/flux-2-klein-4b-Q4_0.gguf'))
    expect(within(dialog).getByLabelText('Name')).toBeDisabled()
    // Another diffusion model in its place: one that reads as video says so, and the model stays a picture model.
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'File or model' }))
    expect(screen.queryByRole('option', { name: /Kokoro/ })).not.toBeInTheDocument()
    await userEvent.click(await screen.findByRole('option', { name: /Wan2\.2-TI2V-5B/ }))
    expect(within(dialog).getByText('It reads as a video model')).toBeInTheDocument()
    expect(within(dialog).queryByRole('button', { name: 'Add it as a video model' })).not.toBeInTheDocument()
    expect(within(dialog).getByRole('combobox', { name: 'Add it as' })).toHaveTextContent('A picture model')
    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: 'File 1' })).toHaveTextContent('video/wan/Wan2.2-TI2V-5B.safetensors'))
    const steps = within(dialog).getByLabelText('Steps')
    expect(steps).toHaveValue('4')
    await userEvent.clear(steps)
    await userEvent.type(steps, '8')
    await userEvent.clear(within(dialog).getByLabelText('GPU budget (GB)'))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PATCH')).toBe(true))
    const sent = calls.find((c) => c.method === 'PATCH')!.body as { steps: number; clear: string[]; files: { role: string; path: string }[] }
    expect(sent.steps).toBe(8)
    expect(sent.clear).toContain('maxVramGb')
    expect(sent.files).toHaveLength(3)
    expect(sent.files[0]).toEqual({ role: 'diffusion', path: 'video/wan/Wan2.2-TI2V-5B.safetensors' })
  })

  it('adds a text to speech model from those the speech server has, read as what it does', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: { engine, small: null, models: [flux] } }),
      'GET /api/admin/models/media/speech': () => ({ json: speech }),
      'POST /api/admin/models/media': () => ({ status: 201, json: { restarting: false, warning: null } }),
    })
    renderApp('/admin/models')
    await screen.findByRole('heading', { name: /FLUX/ })
    const dialog = await choose(/faster-whisper/)
    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: 'Add it as' })).toHaveTextContent('A speech to text model (what it reads as)'))
    expect(within(dialog).getByLabelText('Name')).toHaveValue('faster-whisper-large-v3-turbo-ct2')
    await choose(/Kokoro/)
    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: 'Add it as' })).toHaveTextContent('A text to speech model (what it reads as)'))
    // The name follows the model chosen until it is typed.
    expect(within(dialog).getByLabelText('Name')).toHaveValue('Kokoro-82M-v1.0-ONNX')
    await userEvent.clear(within(dialog).getByLabelText('Name'))
    await userEvent.type(within(dialog).getByLabelText('Name'), 'kokoro-heart')
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'am_adam' }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add model' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST')).toBe(true))
    expect(calls.find((c) => c.method === 'POST')!.body).toMatchObject({
      name: 'kokoro-heart', mode: 'audio_speech', speechId: 'speaches-ai/Kokoro-82M-v1.0-ONNX', voices: ['af_heart'],
    })
  })

  it('edits a speech model from its card in the same dialog', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: { engine, small: null, models: [flux, kokoro] } }),
      'GET /api/admin/models/media/speech': () => ({ json: speech }),
      'PATCH /api/admin/models/media/kokoro': () => ({ json: { restarting: false, warning: null } }),
    })
    renderApp('/admin/models')
    const card = (await screen.findByRole('heading', { name: /^kokoro/ })).closest('section')!
    await userEvent.click(within(card).getByRole('button', { name: /Edit/ }))
    const dialog = await screen.findByRole('dialog', { name: 'Edit kokoro' })
    expect(within(dialog).getByRole('combobox', { name: 'Add it as' })).toHaveTextContent('A text to speech model')
    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: 'File or model' })).toHaveTextContent('speaches-ai/Kokoro-82M-v1.0-ONNX'))
    const speed = within(dialog).getByLabelText('Speed')
    expect(speed).toHaveValue('1')
    await userEvent.clear(speed)
    await userEvent.type(speed, '1.5')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PATCH')).toBe(true))
    expect(calls.find((c) => c.method === 'PATCH')!.body).toMatchObject({ speechId: 'speaches-ai/Kokoro-82M-v1.0-ONNX', speed: 1.5, defaultVoice: 'af_heart' })
  })

  it("a speech model the speech server fetched is added from Downloads, as what it does", async () => {
    fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: { engine, small: null, models: [flux] } }),
      'GET /api/admin/models/media/speech': () => ({ json: speech }),
      'GET /api/admin/models/hf/downloads': () => ({
        json: [{ id: 'd1', kind: 'speech', repo: 'deepdml/faster-whisper-large-v3-turbo-ct2', state: 'done', bytes: 0, total: 0, speed: null, error: null, createdAt: '', files: [] }],
      }),
    })
    renderApp('/admin/models')
    const downloads = await screen.findByRole('region', { name: 'Downloads' })
    await userEvent.click(within(downloads).getByRole('button', { name: 'Add as a model' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a model' })
    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: 'Add it as' })).toHaveTextContent('A speech to text model (what it reads as)'))
    expect(within(dialog).getByLabelText('Name')).toHaveValue('faster-whisper-large-v3-turbo-ct2')
  })
})
