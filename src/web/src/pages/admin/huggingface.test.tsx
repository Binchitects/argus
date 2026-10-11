import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

// Picture, video and speech models are found as chat models are: a picture model's files picked from its repository (its
// diffusion model, encoders, decoder), a speech model fetched by the speech server from its registry.

const view = {
  engine: {
    enabled: true, error: null, checkedAt: '2026-10-11T10:00:00Z', kept: [], pinned: [], max: 2, hours: null, onRequest: true, loaded: [], loading: [], unloading: [],
    gpus: [], plan: null,
  },
  small: null,
  models: [],
}

describe('admin models: finding picture, video and speech models', () => {
  it('searches picture models and downloads the files picked', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: view }),
      'GET /api/admin/models/hf/downloads': () => ({ json: [] }),
      'GET /api/admin/models/hf/search': (_b, _i, url) => ({
        json: url.searchParams.get('kind') === 'image' ? [{ id: 'leejet/FLUX.2-klein-4B-GGUF', downloads: 1200, likes: 40, lastModified: null, gated: false, pipelineTag: 'text-to-image', tags: [] }] : [],
      }),
      'GET /api/admin/models/hf/repo': () => ({
        json: {
          id: 'leejet/FLUX.2-klein-4B-GGUF', sha: 'abc', gated: false, license: 'apache-2.0', architecture: null, parameters: null, context: null, downloads: 1200, likes: 40,
          gpuForModels: null, ramForModels: null, models: [],
          files: [
            { path: 'flux-2-klein-4b-Q4_0.gguf', size: 2.4e9, role: 'diffusion', library: 'leejet/FLUX.2-klein-4B-GGUF/flux-2-klein-4b-Q4_0.gguf', inLibrary: false },
            { path: 'flux-2-klein-4b-Q8_0.gguf', size: 4.3e9, role: 'diffusion', library: 'leejet/FLUX.2-klein-4B-GGUF/flux-2-klein-4b-Q8_0.gguf', inLibrary: true },
          ],
        },
      }),
      'POST /api/admin/models/hf/downloads': () => ({ status: 201, json: {} }),
    })
    renderApp('/admin/models')
    await userEvent.click(await screen.findByRole('button', { name: /Find on Hugging Face/ }))
    const dialog = await screen.findByRole('dialog')
    await userEvent.click(within(dialog).getByRole('tab', { name: 'Pictures' }))
    await userEvent.type(within(dialog).getByRole('searchbox', { name: 'Search Hugging Face' }), 'flux')
    await userEvent.click(within(dialog).getByRole('button', { name: /Search/ }))
    await userEvent.click(await within(dialog).findByRole('button', { name: /leejet\/FLUX/ }))
    expect(calls.find((c) => c.path.startsWith('/api/admin/models/hf/search'))!.path).toContain('kind=image')
    const q8 = await within(dialog).findByRole('checkbox', { name: 'flux-2-klein-4b-Q8_0.gguf' })
    expect(q8).toBeDisabled()
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'flux-2-klein-4b-Q4_0.gguf' }))
    await userEvent.click(within(dialog).getByRole('button', { name: /Download/ }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Download' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/admin/models/hf/downloads')).toBe(true))
    expect(calls.find((c) => c.method === 'POST')!.body).toEqual({ repo: 'leejet/FLUX.2-klein-4B-GGUF', files: ['flux-2-klein-4b-Q4_0.gguf'] })
  })

  it("lists the speech server's models of a task and asks it to fetch one", async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: view }),
      'GET /api/admin/models/hf/downloads': () => ({ json: [] }),
      'GET /api/admin/models/hf/speech': (_b, _i, url) => ({
        json:
          url.searchParams.get('task') === 'text-to-speech'
            ? [
                { id: 'speaches-ai/Kokoro-82M-v1.0-ONNX', task: 'text-to-speech', language: ['multilingual'], downloaded: true },
                { id: 'speaches-ai/piper-de_DE-thorsten-medium', task: 'text-to-speech', language: ['de'], downloaded: false },
              ]
            : [{ id: 'Systran/faster-whisper-small', task: 'automatic-speech-recognition', language: [], downloaded: false }],
      }),
      'POST /api/admin/models/hf/speech': () => ({ status: 201, json: {} }),
    })
    renderApp('/admin/models')
    await userEvent.click(await screen.findByRole('button', { name: /Find on Hugging Face/ }))
    const dialog = await screen.findByRole('dialog')
    await userEvent.click(within(dialog).getByRole('tab', { name: 'Speech' }))
    expect(await within(dialog).findByText('speaches-ai/piper-de_DE-thorsten-medium')).toBeInTheDocument()
    expect(within(dialog).getByText('Downloaded')).toBeInTheDocument()
    await userEvent.type(within(dialog).getByRole('searchbox', { name: 'Filter the speech models' }), 'de')
    expect(within(dialog).queryByText('speaches-ai/Kokoro-82M-v1.0-ONNX')).not.toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: /Download/ }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Download' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/admin/models/hf/speech')).toBe(true))
    expect(calls.find((c) => c.method === 'POST')!.body).toEqual({ id: 'speaches-ai/piper-de_DE-thorsten-medium' })
  })
})
