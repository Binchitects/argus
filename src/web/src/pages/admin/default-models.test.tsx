import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

const view = {
  engine: { enabled: true, error: null, checkedAt: null, kept: [], pinned: [], max: 2, hours: null, onRequest: true, loaded: [], loading: [], unloading: [], gpus: [], plan: null },
  small: null,
  models: [],
}

describe('admin models: the default models', () => {
  it('lists the default models not here yet and downloads them only when the admin says so', async () => {
    let state = 'missing'
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: view }),
      'GET /api/admin/models/defaults': () => ({
        json: [
          { server: 'chat', name: 'unsloth/Qwen3.8-27B-GGUF:UD-Q4_K_XL', source: 'unsloth/Qwen3.8-27B-GGUF:UD-Q4_K_XL', state },
          { server: 'imagegen', name: 'image/flux-2-klein-4b-Q4_0.gguf', source: 'leejet/FLUX.2-klein-4B-GGUF', state: 'present' },
        ],
      }),
      'POST /api/admin/models/defaults': () => {
        state = 'downloading'
        return { json: { started: ['chat: Qwen3.8-27B-UD-Q4_K_XL.gguf'], models: [{ server: 'chat', name: 'unsloth/Qwen3.8-27B-GGUF:UD-Q4_K_XL', source: 'x', state }] } }
      },
    })
    renderApp('/admin/models')
    const card = (await screen.findByText('Default models')).closest('section')!
    expect(within(card).getByText('Chat model (MODEL)')).toBeInTheDocument()
    // What is here already is not listed.
    expect(within(card).queryByText('Pictures')).not.toBeInTheDocument()
    expect(calls.some((c) => c.method === 'POST')).toBe(false)
    await userEvent.click(within(card).getByRole('button', { name: 'Download the default models' }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Download' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/admin/models/defaults')).toBe(true))
    expect(await within(card).findByText('Downloading…')).toBeInTheDocument()
  })

  it('says nothing when every default model is here', async () => {
    fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: view }),
      'GET /api/admin/models/defaults': () => ({ json: [{ server: 'chat', name: 'm.gguf', source: 'm.gguf', state: 'present' }] }),
    })
    renderApp('/admin/models')
    await screen.findByRole('heading', { name: 'Models' })
    await waitFor(() => expect(screen.queryByText('Default models')).not.toBeInTheDocument())
  })
})
