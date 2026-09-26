import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

const everyone = { audience: 'Everyone', groups: [] }

const view = (over: object = {}) => ({
  engine: { enabled: true, error: null, checkedAt: '2026-09-25T10:00:00Z', active: 'Big-Model', loaded: ['Big-Model'], loading: [], ...over },
  models: [
    { name: 'Big-Model', source: 'env', mode: 'chat', status: 'loaded', file: 'big/Big-Q4.gguf', context: 131072, vision: false, access: everyone },
    {
      name: 'Small-Model', source: 'local', mode: 'chat', status: 'unloaded', file: 'small/Small-Q8.gguf', projector: null, context: 32768, maxOutput: null,
      gpuLayers: 99, cpuMoe: 0, kvType: 'q8_0', parallel: 1, extraPreset: null, thinking: true, tools: true, inputPerMtok: null, outputPerMtok: null,
      vision: false, atGateway: true, access: { audience: 'Admins', groups: [] },
    },
    { name: 'flux-image', source: 'gateway', mode: 'image_generation', status: null, context: null, vision: false, access: everyone },
  ],
})

describe('admin models', () => {
  it('a model is loaded, and given to a group', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: view() }),
      'POST /api/admin/models/Small-Model/load': () => ({ status: 202 }),
      'PUT /api/admin/models/Big-Model/access': () => ({ status: 204 }),
      'GET /api/admin/groups': () => ({ json: [{ id: 'g1', name: 'Research', description: null, directory: null, members: 2, createdAt: '' }] }),
    })
    renderApp('/admin/models')
    const big = (await screen.findByRole('heading', { name: /Big-Model/ })).closest('section')!
    const small = screen.getByRole('heading', { name: /Small-Model/ }).closest('section')!
    const image = screen.getByRole('heading', { name: /flux-image/ }).closest('section')!
    expect(within(big).getByText('Loaded')).toBeInTheDocument()
    expect(within(small).getByText('Not loaded')).toBeInTheDocument()
    // Only the engine's models load; only the ones added here are edited.
    expect(within(big).queryByRole('button', { name: /Edit/ })).not.toBeInTheDocument()
    expect(within(image).queryByRole('button', { name: /Load/ })).not.toBeInTheDocument()

    await userEvent.click(within(small).getByRole('button', { name: /Load/ }))
    await userEvent.click(await screen.findByRole('button', { name: 'Load' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/admin/models/Small-Model/load')).toBe(true))

    await userEvent.click(within(big).getByRole('combobox', { name: 'Who may use it' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Chosen groups' }))
    await userEvent.click(within(big).getByRole('button', { name: 'Choose groups' }))
    await userEvent.click(await screen.findByRole('checkbox', { name: /Research/ }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toMatchObject({ audience: 'Groups', groups: ['g1'] }))
  })

  it('a model is added from the library', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: view() }),
      'GET /api/admin/models/library': () => ({
        json: [
          { path: 'qwen/Qwen-27B-Q4_K_M.gguf', size: 17e9, parts: 1, role: 'model', architecture: 'qwen3', name: 'Qwen 27B', sizeLabel: '27B', trainedContext: 262144, usedBy: [] },
          { path: 'qwen/mmproj-F16.gguf', size: 9e8, parts: 1, role: 'projector', architecture: 'clip', name: null, sizeLabel: null, trainedContext: null, usedBy: [] },
        ],
      }),
      'POST /api/admin/models': () => ({ status: 201, json: { restarting: true, warning: null } }),
    })
    renderApp('/admin/models')
    await userEvent.click(await screen.findByRole('button', { name: 'Add a model' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a model' })
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'Model file' }))
    await userEvent.click(await screen.findByRole('option', { name: /Qwen-27B-Q4_K_M\.gguf/ }))
    // The name comes from the file, the context from what it was trained on, capped.
    expect(within(dialog).getByLabelText('Name')).toHaveValue('Qwen-27B-Q4_K_M')
    expect(within(dialog).getByLabelText('Context (tokens)')).toHaveValue('32768')
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'Vision projector' }))
    await userEvent.click(await screen.findByRole('option', { name: 'qwen/mmproj-F16.gguf' }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add model' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'POST' && c.path === '/api/admin/models')?.body).toMatchObject({
        name: 'Qwen-27B-Q4_K_M', file: 'qwen/Qwen-27B-Q4_K_M.gguf', projector: 'qwen/mmproj-F16.gguf', context: 32768, kvType: 'q8_0',
      }),
    )
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('a model that could not load says so, and where to find why', async () => {
    const v = view()
    fakeApi(admin, { 'GET /api/admin/models': () => ({ json: { ...v, models: v.models.map((m) => (m.name === 'Small-Model' ? { ...m, status: 'failed' } : m)) } }) })
    renderApp('/admin/models')
    const small = (await screen.findByRole('heading', { name: /Small-Model/ })).closest('section')!
    expect(within(small).getByText('Could not load')).toBeInTheDocument()
    expect(within(small).getByRole('alert')).toHaveTextContent(/docker compose logs llamacpp/)
    expect(within(small).getByRole('button', { name: /Load/ })).toBeEnabled()
  })

  it('without the llama.cpp engine, only who may use each model is set', async () => {
    fakeApi(admin, { 'GET /api/admin/models': () => ({ json: { ...view({ enabled: false, active: null, loaded: [] }), models: view().models.slice(2) } }) })
    renderApp('/admin/models')
    expect(await screen.findByText(/needs the llama.cpp engine/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Add a model' })).not.toBeInTheDocument()
    expect(screen.getByRole('combobox', { name: 'Who may use it' })).toBeInTheDocument()
  })
})
