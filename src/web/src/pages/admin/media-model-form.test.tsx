import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

// "Load, unload, remove and configure picture, video and speech models as LLMs": added from the page's Add menu with their
// files from the library (or the speech server's model), their settings, GPU, idle time and price; edited from their card.

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
const files = [
  { path: 'image/flux/flux-2-klein-4b-Q4_0.gguf', size: 2.4e9, role: 'diffusion', usedBy: ['FLUX.2-klein-4B'] },
  { path: 'image/flux/Qwen3-4B-Q4_K_M.gguf', size: 2.5e9, role: 'llm', usedBy: ['FLUX.2-klein-4B'] },
  { path: 'image/flux/flux2-vae.safetensors', size: 0.16e9, role: 'vae', usedBy: ['FLUX.2-klein-4B'] },
  { path: 'image/sdxl/sdxl-turbo.safetensors', size: 6.9e9, role: 'diffusion', usedBy: [] },
]

describe('admin models: picture, video and speech models', () => {
  it('adds a picture model from the library, with its steps, GPU budget, idle time and price', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: { engine, small: null, models: [flux] } }),
      'GET /api/admin/models/media/files': () => ({ json: files }),
      'POST /api/admin/models/media': () => ({ status: 201, json: { restarting: false, warning: null } }),
    })
    renderApp('/admin/models')
    await screen.findByRole('heading', { name: /FLUX/ })
    await userEvent.click(screen.getByRole('button', { name: /Add a model/ }))
    await userEvent.click(await screen.findByRole('menuitem', { name: 'A pictures model' }))
    const dialog = await screen.findByRole('dialog')
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'File 1' }))
    await userEvent.click(await screen.findByRole('option', { name: /sdxl-turbo/ }))
    // The name follows the diffusion model's file.
    expect(within(dialog).getByLabelText('Name')).toHaveValue('sdxl-turbo')
    await userEvent.type(within(dialog).getByLabelText('Steps'), '1')
    await userEvent.type(within(dialog).getByLabelText('GPU budget (GB)'), '4')
    await userEvent.type(within(dialog).getByLabelText(/Unload after/), '5')
    await userEvent.type(within(dialog).getByLabelText(/Price per picture/), '0.02')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/admin/models/media')).toBe(true))
    const sent = calls.find((c) => c.method === 'POST' && c.path === '/api/admin/models/media')!.body as Record<string, unknown>
    expect(sent).toMatchObject({
      name: 'sdxl-turbo', mode: 'image_generation', files: [{ role: 'diffusion', path: 'image/sdxl/sdxl-turbo.safetensors' }], steps: 1, maxVramGb: 4,
      idleMinutes: 5, unitPrice: 0.02, vae: 'gpu', flashAttention: true, kept: false,
    })
    expect(sent).not.toHaveProperty('clear')
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
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByLabelText('Name')).toBeDisabled()
    const steps = within(dialog).getByLabelText('Steps')
    expect(steps).toHaveValue('4')
    await userEvent.clear(steps)
    await userEvent.type(steps, '8')
    await userEvent.clear(within(dialog).getByLabelText('GPU budget (GB)'))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PATCH')).toBe(true))
    const sent = calls.find((c) => c.method === 'PATCH')!.body as { steps: number; clear: string[]; files: unknown[] }
    expect(sent.steps).toBe(8)
    expect(sent.clear).toContain('maxVramGb')
    expect(sent.files).toHaveLength(3)
  })

  it('adds a text to speech model from those the speech server has', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: { engine, small: null, models: [flux] } }),
      'GET /api/admin/models/media/speech': () => ({
        json: [
          { id: 'speaches-ai/Kokoro-82M-v1.0-ONNX', task: 'text-to-speech', language: ['multilingual'], voices: ['af_heart', 'am_adam'] },
          { id: 'deepdml/faster-whisper-large-v3-turbo-ct2', task: 'automatic-speech-recognition', language: [], voices: [] },
        ],
      }),
      'POST /api/admin/models/media': () => ({ status: 201, json: { restarting: false, warning: null } }),
    })
    renderApp('/admin/models')
    await screen.findByRole('heading', { name: /FLUX/ })
    await userEvent.click(screen.getByRole('button', { name: /Add a model/ }))
    await userEvent.click(await screen.findByRole('menuitem', { name: 'A text to speech model' }))
    const dialog = await screen.findByRole('dialog')
    await userEvent.type(within(dialog).getByLabelText('Name'), 'kokoro-heart')
    await userEvent.click(await within(dialog).findByRole('combobox', { name: 'Model' }))
    // Only text to speech models are offered.
    expect(screen.queryByRole('option', { name: /whisper/ })).not.toBeInTheDocument()
    await userEvent.click(await screen.findByRole('option', { name: /Kokoro/ }))
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'am_adam' }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST')).toBe(true))
    expect(calls.find((c) => c.method === 'POST')!.body).toMatchObject({
      name: 'kokoro-heart', mode: 'audio_speech', speechId: 'speaches-ai/Kokoro-82M-v1.0-ONNX', voices: ['af_heart'],
    })
  })
})
