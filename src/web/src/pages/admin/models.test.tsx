import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

const everyone = { audience: 'Everyone', groups: [] }

const view = (over: object = {}) => ({
  engine: {
    enabled: true, error: null, checkedAt: '2026-09-25T10:00:00Z', kept: ['Big-Model'], max: 2, onRequest: true, loaded: ['Big-Model'], loading: [],
    gpus: [{ index: 0, name: 'NVIDIA GeForce RTX 3090', total: 25.7e9 }],
    plan: { problems: [], gpus: [{ index: 0, name: 'NVIDIA GeForce RTX 3090', budget: 22.5e9, need: 18e9, models: ['Big-Model'] }] },
    ...over,
  },
  models: [
    { name: 'Big-Model', source: 'local', mode: 'chat', status: 'loaded', file: 'big/Big-Q4.gguf', context: 131072, vision: false, access: everyone, kept: true },
    {
      name: 'Small-Model', source: 'local', mode: 'chat', status: 'unloaded', file: 'small/Small-Q8.gguf', projector: null, context: 32768, maxOutput: null,
      gpuLayers: 99, cpuMoe: 0, kvType: 'q8_0', parallel: 1, extraPreset: null, thinking: true, tools: true, inputPerMtok: null, outputPerMtok: null,
      vision: false, atGateway: true, access: { audience: 'Admins', groups: [] }, kept: false, devices: null,
    },
    { name: 'flux-image', source: 'gateway', mode: 'image_generation', status: null, context: null, vision: false, access: everyone },
  ],
})

const profile = (over: object = {}) => ({
  kind: 'language', why: null, note: null, architecture: 'qwen35', name: 'Qwen 27B', sizeLabel: '27B', quant: 'Q4_K_M', structure: 'dense', attention: 'hybrid',
  parameters: 26.9e9, activeParameters: null, bitsPerWeight: 5.1, weightBytes: 17e9, layers: 64, attentionLayers: 16, experts: null, trainedContext: 262144,
  slidingWindow: null, embeddingLength: 5120, vocabSize: 248320, kvBytesPerToken: { q8_0: 34816, f16: 65536 }, recurrentBytesPerSlot: 157e6, mtpLayers: 0,
  thinking: true, tools: true, sampling: { temperature: 1, topP: 0.95, topK: 20, minP: null }, ropeScaling: null, canStretch: true, projector: null, approximate: false,
  ...over,
})

const library = [
  { path: 'qwen/Qwen-27B-Q4_K_M.gguf', size: 17e9, parts: 1, profile: profile(), usedBy: [] },
  {
    path: 'big/Big-MoE-00001-of-00002.gguf', size: 90e9, parts: 2, usedBy: [],
    profile: profile({ structure: 'moe', parameters: 177e9, activeParameters: 6.7e9, experts: { count: 512, used: 10, shared: 1, layers: 48, bytes: 60e9 }, layers: 48 }),
  },
  { path: 'image/flux-2-klein-4b-Q4_0.gguf', size: 2e9, parts: 1, usedBy: [], profile: profile({ kind: 'image', why: 'An image model: pictures are made by the image server.' }) },
]

/** The API's advice: limits of the file, a start that fits, and whether these settings fit. */
function advice(body: { file: string; context?: number }) {
  const moe = body.file.startsWith('big/')
  const over = (body.context ?? 0) > 262144
  return {
    profile: library.find((f) => f.path === body.file)!.profile,
    limits: {
      context: { min: 4096, max: 262144 }, maxOutput: { min: 256, max: (body.context ?? 131072) - 1024 }, parallel: { min: 1, max: 32 }, gpuLayers: { min: 0, max: moe ? 49 : 65 },
      cpuMoe: moe ? { min: 0, max: 48 } : null, draftMax: { min: 1, max: 8 }, cacheTypes: ['q8_0', 'f16'], ubatch: [256, 512, 1024, 2048, 4096], mtp: false, yarn: true, projectors: [], draftHeads: [],
    },
    recommended: { context: 131072, maxOutput: 32768, parallel: 2, kvType: 'q8_0', ubatch: moe ? 1024 : null, mtp: false, draftHead: null, draftMax: 3, projector: null, thinking: true, tools: true },
    estimate: {
      gpuWeights: 16.5e9, gpuCache: 4.7e9, gpuCompute: 0.8e9, gpuTotal: 22e9, gpuBudget: 22.5e9, ramWeights: 0.7e9, ramCache: 0.1e9, ramTotal: 0.8e9, ramBudget: 57e9,
      gpuLayers: 65, layers: 64, expertLayersInRam: 0, moeLayers: 0, fit: 'gpu', approximate: false,
    },
    problems: over ? [{ field: 'context', message: 'It was trained for 262,144 tokens of context. Stretch it with YaRN (up to 1,048,576), or choose at most 262,144.', error: true }] : [],
    hardware: { gpuName: 'NVIDIA GeForce RTX 3090', gpus: 1, gpuTotal: 25.7e9, imageReserve: 2.1e9, ramTotal: 66e9, gpuForModels: 22.5e9, ramForModels: 57e9 },
  }
}

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
    // Only the engine's models load and are edited.
    expect(within(big).getByRole('button', { name: /Edit/ })).toBeInTheDocument()
    expect(within(image).queryByRole('button', { name: /Load/ })).not.toBeInTheDocument()
    expect(within(image).queryByRole('button', { name: /Edit/ })).not.toBeInTheDocument()

    await userEvent.click(within(small).getByRole('button', { name: /Load/ }))
    await userEvent.click(await screen.findByRole('button', { name: 'Load' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/admin/models/Small-Model/load')).toBe(true))

    await userEvent.click(within(big).getByRole('combobox', { name: 'Who may use it' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Chosen groups' }))
    await userEvent.click(within(big).getByRole('button', { name: 'Choose groups' }))
    await userEvent.click(await screen.findByRole('checkbox', { name: /Research/ }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toMatchObject({ audience: 'Groups', groups: ['g1'] }))
  })

  it('a model is added from the library, starting from what fits its kind and the machine', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: view() }),
      'GET /api/admin/models/library': () => ({ json: library }),
      'POST /api/admin/models/advice': (body) => ({ json: advice(body as { file: string; context?: number }) }),
      'POST /api/admin/models': () => ({ status: 201, json: { restarting: true, warning: null } }),
    })
    renderApp('/admin/models')
    await userEvent.click(await screen.findByRole('button', { name: 'Add a model' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a model' })
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'Model file' }))
    // Files that are not language models are there, for what they are, and cannot be chosen.
    expect(await screen.findByRole('option', { name: /flux-2-klein-4b-Q4_0\.gguf · Image model/ })).toHaveAttribute('aria-disabled', 'true')
    await userEvent.click(screen.getByRole('option', { name: /Qwen-27B-Q4_K_M\.gguf/ }))
    // What the file is, and a start that fits: the name from the file, the context and answer from the advice.
    expect(within(dialog).getByRole('region', { name: 'What the file is' })).toHaveTextContent(/Dense.*Hybrid attention/)
    expect(within(dialog).getByRole('region', { name: 'What the file is' })).toHaveTextContent('Trained for 262,144 tokens of context')
    expect(within(dialog).getByLabelText('Name')).toHaveValue('Qwen-27B-Q4_K_M')
    await waitFor(() => expect(within(dialog).getByLabelText('Context (tokens)')).toHaveValue('131072'))
    expect(within(dialog).getByLabelText('Longest answer (tokens)')).toHaveValue('32768')
    expect(await within(dialog).findByText(/4,096 to 262,144 \(trained for 262,144\)/)).toBeInTheDocument()
    // A dense model is not asked about experts, even placed by hand.
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'Placement' }))
    await userEvent.click(await screen.findByRole('option', { name: 'By hand' }))
    expect(within(dialog).getByLabelText('Layers on the GPU')).toBeInTheDocument()
    expect(within(dialog).queryByLabelText('Layers with experts in RAM')).not.toBeInTheDocument()
    // The memory, as the form changes.
    expect(await within(dialog).findByRole('region', { name: 'Memory' })).toHaveTextContent(/All of it on the GPU/)

    // Past its training: the field says why, and it cannot be added.
    await userEvent.clear(within(dialog).getByLabelText('Context (tokens)'))
    await userEvent.type(within(dialog).getByLabelText('Context (tokens)'), '500000')
    expect(await within(dialog).findByText(/trained for 262,144 tokens of context/)).toBeInTheDocument()
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Add model' })).toBeDisabled())
    await userEvent.clear(within(dialog).getByLabelText('Context (tokens)'))
    await userEvent.type(within(dialog).getByLabelText('Context (tokens)'), '65536')
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Add model' })).toBeEnabled())

    await userEvent.click(within(dialog).getByRole('button', { name: 'Add model' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'POST' && c.path === '/api/admin/models')?.body).toMatchObject({
        name: 'Qwen-27B-Q4_K_M', file: 'qwen/Qwen-27B-Q4_K_M.gguf', context: 65536, maxOutput: 32768, placement: 'manual', kvType: 'q8_0', parallel: 2,
      }),
    )
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('the name follows the chosen file until it is typed', async () => {
    fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: view() }),
      'GET /api/admin/models/library': () => ({ json: library }),
      'POST /api/admin/models/advice': (body) => ({ json: advice(body as { file: string }) }),
    })
    renderApp('/admin/models')
    await userEvent.click(await screen.findByRole('button', { name: 'Add a model' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a model' })
    const choose = async (name: RegExp) => {
      await userEvent.click(within(dialog).getByRole('combobox', { name: 'Model file' }))
      await userEvent.click(await screen.findByRole('option', { name }))
    }
    await choose(/Qwen-27B-Q4_K_M\.gguf/)
    expect(within(dialog).getByLabelText('Name')).toHaveValue('Qwen-27B-Q4_K_M')
    await choose(/Big-MoE-00001-of-00002\.gguf/)
    expect(within(dialog).getByLabelText('Name')).toHaveValue('Big-MoE')
    // A mixture of experts is asked where its experts go.
    expect(within(dialog).getByRole('region', { name: 'What the file is' })).toHaveTextContent('Mixture of experts')
    await userEvent.clear(within(dialog).getByLabelText('Name'))
    await userEvent.type(within(dialog).getByLabelText('Name'), 'mine')
    await choose(/Qwen-27B-Q4_K_M\.gguf/)
    expect(within(dialog).getByLabelText('Name')).toHaveValue('mine')
  })

  it('a model is kept loaded; with every place kept, no other loads', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: view() }),
      'PUT /api/admin/models/Small-Model/keep': () => ({ json: { kept: ['Big-Model', 'Small-Model'], warning: null } }),
    })
    renderApp('/admin/models')
    const engine = await screen.findByText(/Up to 2 models loaded at once, 1 kept loaded \(Big-Model\)/)
    expect(engine).toHaveTextContent(/Any other loads when someone asks for it/)
    expect(screen.getByRole('meter', { name: 'GPU 0: the models kept loaded' })).toHaveAttribute('aria-valuenow', String(18e9))
    const small = screen.getByRole('heading', { name: /Small-Model/ }).closest('section')!
    const big = screen.getByRole('heading', { name: /Big-Model/ }).closest('section')!
    expect(within(big).getByText('Kept loaded')).toBeInTheDocument()
    expect(within(big).getByRole('switch', { name: /Keep loaded/ })).toBeChecked()
    await userEvent.click(within(small).getByRole('switch', { name: /Keep loaded/ }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT' && c.path.endsWith('/keep'))?.body).toEqual({ keep: true }))
  })

  it('with every place kept, a model that is not kept cannot load', async () => {
    fakeApi(admin, { 'GET /api/admin/models': () => ({ json: view({ max: 1, onRequest: false }) }) })
    renderApp('/admin/models')
    expect(await screen.findByText(/Every place is kept, so no other model loads on request/)).toBeInTheDocument()
    const small = screen.getByRole('heading', { name: /Small-Model/ }).closest('section')!
    expect(within(small).getByRole('button', { name: /Load/ })).toBeDisabled()
    expect(within(small).getByText('Otherwise it loads only when an admin loads it.')).toBeInTheDocument()
  })

  it('with two GPUs, a model runs on the ones chosen', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({
        json: view({ gpus: [{ index: 0, name: 'RTX 3090', total: 25.7e9 }, { index: 1, name: 'RTX 4090', total: 25.7e9 }] }),
      }),
      'GET /api/admin/models/library': () => ({ json: library }),
      'POST /api/admin/models/advice': (body) => ({ json: advice(body as { file: string }) }),
      'POST /api/admin/models': () => ({ status: 201, json: { restarting: true, warning: null } }),
    })
    renderApp('/admin/models')
    await userEvent.click(await screen.findByRole('button', { name: 'Add a model' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a model' })
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'Model file' }))
    await userEvent.click(await screen.findByRole('option', { name: /Qwen-27B-Q4_K_M\.gguf/ }))
    const gpus = within(dialog).getByRole('group', { name: 'GPUs' })
    expect(within(gpus).getByRole('checkbox', { name: /GPU 0 · RTX 3090/ })).toBeChecked()
    expect(within(gpus).getByText(/split across the GPUs/)).toBeInTheDocument()
    await userEvent.click(within(gpus).getByRole('checkbox', { name: /GPU 0 · RTX 3090/ }))
    expect(within(gpus).getByRole('checkbox', { name: /GPU 0/ })).not.toBeChecked()
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Add model' })).toBeEnabled())
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add model' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/admin/models')?.body).toMatchObject({ devices: '1' }))
  })

  it('another GPU server is added: its models found, chosen and named, its key sent once', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: view() }),
      'GET /api/admin/servers': () => ({ json: [] }),
      'POST /api/admin/servers/probe': () => ({ json: { models: [{ id: 'org/Big-Remote', context: 65536 }, { id: 'small', context: null }] } }),
      'POST /api/admin/servers': () => ({ status: 201, json: { id: 's1', warning: null } }),
    })
    renderApp('/admin/models')
    await userEvent.click(await screen.findByRole('button', { name: 'Add a server' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a server' })
    await userEvent.type(within(dialog).getByLabelText('Name'), 'GPU box')
    await userEvent.type(within(dialog).getByLabelText('Address'), 'http://10.0.0.5:8000/v1')
    await userEvent.type(within(dialog).getByLabelText('Its API key'), 'secret')
    await userEvent.click(within(dialog).getByRole('button', { name: /Find its models/ }))
    // Nothing is chosen until the admin says so; the name at the gateway starts from its id.
    const big = await within(dialog).findByRole('checkbox', { name: 'Serve org/Big-Remote' })
    expect(within(dialog).getByRole('button', { name: 'Add server' })).toBeDisabled()
    await userEvent.click(big)
    expect(within(dialog).getByLabelText('Name at the gateway for org/Big-Remote')).toHaveValue('Big-Remote')
    expect(within(dialog).getByLabelText('Context for org/Big-Remote')).toHaveValue('65536')
    await userEvent.clear(within(dialog).getByLabelText('Name at the gateway for org/Big-Remote'))
    await userEvent.type(within(dialog).getByLabelText('Name at the gateway for org/Big-Remote'), 'big')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add server' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'POST' && c.path === '/api/admin/servers')?.body).toEqual({
        name: 'GPU box', baseUrl: 'http://10.0.0.5:8000/v1', verifyTls: true, apiKey: 'secret',
        models: [{ remote: 'org/Big-Remote', name: 'big', context: 65536, maxOutput: null, vision: false, tools: true, thinking: false }],
      }),
    )
  })

  it('a server that does not answer says why; its models are on the page with it', async () => {
    const v = view()
    fakeApi(admin, {
      'GET /api/admin/models': () => ({
        json: { ...v, models: [...v.models, { name: 'big', source: 'remote', server: 'GPU box', remote: 'org/Big-Remote', mode: 'chat', status: null, context: 65536, vision: false, access: everyone }] },
      }),
      'GET /api/admin/servers': () => ({
        json: [{
          id: 's1', name: 'GPU box', baseUrl: 'http://10.0.0.5:8000/v1', keySet: true, verifyTls: true,
          status: { up: false, error: 'It cannot be reached: Connection refused', checkedAt: '', offers: [] },
          models: [{ remote: 'org/Big-Remote', name: 'big', context: 65536, maxOutput: null, vision: false, tools: true, thinking: false, listed: true }],
        }],
      }),
    })
    renderApp('/admin/models')
    const server = await screen.findByRole('region', { name: 'GPU box' })
    expect(within(server).getByText('Does not answer')).toBeInTheDocument()
    expect(within(server).getByText(/Connection refused/)).toBeInTheDocument()
    expect(within(server).getByText(/big ← org\/Big-Remote/)).toBeInTheDocument()
    const card = screen.getByRole('heading', { name: /^big/ }).closest('section')!
    expect(within(card).getByText('On GPU box')).toBeInTheDocument()
    expect(within(card).getByText(/org\/Big-Remote on GPU box/)).toBeInTheDocument()
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

  it('a model the engine does not list says so, and cannot be loaded', async () => {
    const v = view()
    fakeApi(admin, { 'GET /api/admin/models': () => ({ json: { ...v, models: v.models.map((m) => (m.name === 'Small-Model' ? { ...m, status: 'missing' } : m)) } }) })
    renderApp('/admin/models')
    const small = (await screen.findByRole('heading', { name: /Small-Model/ })).closest('section')!
    expect(within(small).getByText('Not in the engine')).toBeInTheDocument()
    expect(within(small).getByText(/serves the \.env model alone/)).toBeInTheDocument()
    expect(within(small).queryByRole('button', { name: /Load/ })).not.toBeInTheDocument()
  })

  it('without the llama.cpp engine, only who may use each model is set', async () => {
    fakeApi(admin, { 'GET /api/admin/models': () => ({ json: { ...view({ enabled: false, kept: [], loaded: [], plan: null }), models: view().models.slice(2) } }) })
    renderApp('/admin/models')
    expect(await screen.findByText(/needs the llama.cpp engine/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Add a model' })).not.toBeInTheDocument()
    expect(screen.getByRole('combobox', { name: 'Who may use it' })).toBeInTheDocument()
  })
})
