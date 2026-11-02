import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

// Admin → Models: a model moved to another engine at runtime from its own Edit dialog (llama.cpp to vLLM or SGLang and back,
// vLLM to SGLang), and its card saying how far the move is, or why it failed.

const everyone = { audience: 'Everyone', groups: [] }
const gpu = (index: number) => ({ index, name: 'NVIDIA RTX PRO 6000', total: 102.6e9, vendor: 'nvidia', backend: 'cuda' })
const instances = [
  { name: 'llamacpp', kind: 'llamacpp', managed: true, problem: null, on: true },
  { name: 'vllm', kind: 'vllm', managed: true, problem: null, on: true },
  { name: 'sglang', kind: 'sglang', managed: true, problem: null, on: true },
]
const engine = {
  enabled: true, error: null, checkedAt: '2026-11-02T10:00:00Z', kept: [], pinned: [], max: 2, hours: null, onRequest: true, loaded: ['flash'], loading: [], unloading: [],
  gpus: [gpu(0), gpu(1)], plan: { problems: [], gpus: [] }, instances,
}
const flash = {
  name: 'flash', source: 'local', mode: 'chat', status: 'loaded', engineKind: 'vllm', engine: null, file: 'hf/org/flash@r', context: 65536, parallel: 16, kvType: 'fp8',
  devices: '0', replicas: 1, gpuMemoryUtilization: 0.85, kvCpuGb: 8, kvTier: null, sleep: true, fallback: null, vision: false, access: everyone, kept: false,
  price: { input: 0.2, cachedInput: 0.02, output: 0.9, own: { input: false, cachedInput: false, output: true } }, outputPerMtok: 0.9, move: null,
}
const qwen = {
  name: 'Qwen', source: 'local', mode: 'chat', status: 'loaded', engineKind: 'llamacpp', file: 'q/Qwen.gguf', context: 32768, parallel: 2, kvType: 'q8_0', devices: '0,1',
  mtp: true, draftHead: 'q/Qwen-draft.gguf', draftMax: 3, vision: false, access: everyone, kept: false, move: null,
}
const profile = {
  kind: 'language', why: null, note: null, architecture: 'qwen3', name: 'Qwen 8B', sizeLabel: '8B', quant: 'Q4_K_M', structure: 'dense', attention: 'full',
  parameters: 8e9, activeParameters: null, bitsPerWeight: 5.1, weightBytes: 5e9, layers: 36, attentionLayers: 36, experts: null, trainedContext: 131072,
  slidingWindow: null, embeddingLength: 4096, vocabSize: 151936, kvBytesPerToken: { q8_0: 34816, f16: 65536 }, recurrentBytesPerSlot: null, mtpLayers: 0,
  thinking: true, tools: true, sampling: null, ropeScaling: null, canStretch: true, projector: null, approximate: false,
}
const library = [{ path: 'q/Qwen.gguf', size: 5e9, parts: 1, profile, usedBy: ['Qwen'] }]
const snapshots = [
  { path: 'hf/org/flash@r', repo: 'org/flash', revision: 'b5c939de8f754692c1647ca79fbf85e8c1e70f8a', size: 16e9, complete: true, why: null, architectures: ['Qwen3ForCausalLM'], usedBy: ['flash'] },
]
const llamaAdvice = {
  profile,
  limits: {
    context: { min: 4096, max: 131072 }, maxOutput: { min: 256, max: 130048 }, parallel: { min: 1, max: 32 }, gpuLayers: { min: 0, max: 37 }, cpuMoe: null, draftMax: { min: 1, max: 8 },
    cacheTypes: ['q8_0', 'f16'], ubatch: [256, 512, 1024], mtp: false, yarn: true, projectors: [], draftHeads: [],
  },
  recommended: { context: 65536, maxOutput: 32768, parallel: 2, kvType: 'q8_0', ubatch: null, mtp: false, draftHead: null, draftMax: 3, projector: null, thinking: true, tools: true },
  estimate: {
    gpuWeights: 5e9, gpuCache: 2e9, gpuCompute: 0.5e9, gpuTotal: 7.5e9, gpuBudget: 200e9, ramWeights: 0, ramCache: 0, ramTotal: 0, ramBudget: 57e9, gpuLayers: 37, layers: 36,
    expertLayersInRam: 0, moeLayers: 0, fit: 'gpu', approximate: false,
  },
  problems: [],
  hardware: { gpuName: 'NVIDIA RTX PRO 6000', gpus: 2, gpuTotal: 205e9, reserves: [], ramTotal: 66e9, gpuForModels: 200e9, ramForModels: 57e9 },
}
const vllmAdvice = {
  limits: { context: { min: 4096, max: 131072 }, maxOutput: { min: 256, max: 130048 }, parallel: { min: 1, max: 1024 } },
  recommended: { context: 65536, maxOutput: 32768, parallel: 16, kvType: 'fp8', devices: '1' },
  estimate: { gpuWeights: 16e9, gpuCache: 20e9, gpuTotal: 36e9, gpuBudget: 101e9, ramTotal: 0, fit: 'gpu', approximate: false },
  problems: [],
}

async function edit(name: string) {
  const card = (await screen.findByRole('heading', { name: new RegExp(name) })).closest('section')!
  await userEvent.click(within(card).getByRole('button', { name: /Edit/ }))
  return screen.findByRole('dialog', { name: `Edit ${name}` })
}

describe('admin models: moving a model to another engine', () => {
  it('moves a vLLM model to llama.cpp: a GGUF file chosen in its own dialog opens the llama.cpp form, saved under its name as llama.cpp\'s', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: { engine, small: null, models: [flash] } }),
      'GET /api/admin/models/library': () => ({ json: library }),
      'GET /api/admin/models/library/snapshots': () => ({ json: snapshots }),
      'GET /api/admin/models/media/files': () => ({ json: [] }),
      'POST /api/admin/models/advice': (b) => ({ json: (b as { engineKind?: string }).engineKind === 'llamacpp' || !(b as { engineKind?: string }).engineKind ? llamaAdvice : vllmAdvice }),
      'PATCH /api/admin/models/flash': () => ({ json: { restarting: true, warning: null } }),
    })
    renderApp('/admin/models')
    const dialog = await edit('flash')
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'File or model' }))
    await userEvent.click(await screen.findByRole('option', { name: /q\/Qwen\.gguf/ }))
    expect(await within(dialog).findByText('Moves flash to llama.cpp')).toBeInTheDocument()
    expect(within(dialog).getByText(/Its answers in flight end on vLLM; new requests wait for it on llama.cpp/)).toBeInTheDocument()
    // The llama.cpp form, under its name, from what fits the file: llama.cpp's cache type, on every GPU.
    expect(within(dialog).queryByRole('form', { name: 'The vLLM model' })).not.toBeInTheDocument()
    expect(within(dialog).getByLabelText('Name')).toHaveValue('flash')
    await waitFor(() => expect(within(dialog).getByLabelText('Context (tokens)')).toHaveValue('65536'))
    // The advice is asked for the model as llama.cpp's.
    await waitFor(() => expect(calls.some((c) => c.path === '/api/admin/models/advice' && (c.body as { engineKind?: string }).engineKind === 'llamacpp')).toBe(true))
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Save' })).toBeEnabled())
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PATCH' && c.path === '/api/admin/models/flash')).toBe(true))
    const sent = calls.find((c) => c.method === 'PATCH')!.body as Record<string, unknown> & { clear: string[] }
    expect(sent).toMatchObject({ engineKind: 'llamacpp', file: 'q/Qwen.gguf', kvType: 'q8_0', parallel: 2 })
    expect(sent.clear).toContain('devices')
    expect(sent.draftHead).toBe('')
  })

  it('moves a vLLM model to SGLang: the engine chosen in the Engine field, the dialog says it moves, the save names SGLang', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: { engine, small: null, models: [flash] } }),
      'GET /api/admin/models/library': () => ({ json: library }),
      'GET /api/admin/models/library/snapshots': () => ({ json: snapshots }),
      'GET /api/admin/models/media/files': () => ({ json: [] }),
      'POST /api/admin/models/advice': () => ({ json: vllmAdvice }),
      'PATCH /api/admin/models/flash': () => ({ json: { restarting: true, warning: null } }),
    })
    renderApp('/admin/models')
    const dialog = await edit('flash')
    await within(dialog).findByRole('form', { name: 'The vLLM model' })
    expect(within(dialog).queryByText(/^Moves flash/)).not.toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'Engine' }))
    await userEvent.click(await screen.findByRole('option', { name: 'SGLang' }))
    const sglang = await within(dialog).findByRole('form', { name: 'The SGLang model' })
    expect(within(dialog).getByText('Moves flash to SGLang')).toBeInTheDocument()
    await userEvent.click(within(sglang).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PATCH' && c.path === '/api/admin/models/flash')).toBe(true))
    expect(calls.find((c) => c.method === 'PATCH')!.body).toMatchObject({ engineKind: 'sglang', file: 'hf/org/flash@r', devices: '0' })
  })

  it("moves a llama.cpp model to vLLM without llama.cpp's own draft head or its GPUs: those proposed for vLLM", async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: { engine, small: null, models: [qwen] } }),
      'GET /api/admin/models/library': () => ({ json: library }),
      'GET /api/admin/models/library/snapshots': () => ({ json: snapshots }),
      'GET /api/admin/models/media/files': () => ({ json: [] }),
      'POST /api/admin/models/advice': (b) => ({ json: (b as { engineKind?: string }).engineKind === 'vllm' ? vllmAdvice : llamaAdvice }),
      'PATCH /api/admin/models/Qwen': () => ({ json: { restarting: false, warning: null } }),
    })
    renderApp('/admin/models')
    const dialog = await edit('Qwen')
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'File or model' }))
    await userEvent.click(await screen.findByRole('option', { name: /flash@r · .* · vLLM or SGLang/ }))
    // The Engine field goes with it, to the first of vLLM and SGLang.
    expect(await within(dialog).findByText('Moves Qwen to vLLM')).toBeInTheDocument()
    expect(within(dialog).getByRole('combobox', { name: 'Engine' })).toHaveTextContent('vLLM')
    const form = await within(dialog).findByRole('form', { name: 'The vLLM model' })
    expect(within(form).getByText(/None ticked: GPUs 1, proposed for this machine/)).toBeInTheDocument()
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PATCH' && c.path === '/api/admin/models/Qwen')).toBe(true))
    const sent = calls.find((c) => c.method === 'PATCH')!.body as Record<string, unknown> & { clear: string[] }
    expect(sent).toMatchObject({ engineKind: 'vllm', file: 'hf/org/flash@r', parallel: 16 })
    expect(sent.draftHead).toBeUndefined()
    expect(sent.clear).toEqual(expect.arrayContaining(['devices', 'draftHead', 'specDecode']))
  })

  it('shows a move under way on its card, Edit and Unload held until it is there, and a failed one with why, dismissed', async () => {
    const at = '2026-11-02T10:00:00Z'
    const move = { from: 'vllm', to: 'llamacpp', fromEngine: 'vllm', toEngine: 'llamacpp', since: at, at, by: 'admin', error: null }
    let models: Record<string, unknown>[] = [
      { ...flash, move: { ...move, phase: 'draining', old: 'serving' } },
      { ...qwen, engineKind: 'sglang', status: 'loading', move: { ...move, from: 'llamacpp', to: 'sglang', fromEngine: 'llamacpp', toEngine: 'sglang', phase: 'loading', old: 'gone' } },
      { ...flash, name: 'coder', move: { ...move, phase: 'failed', old: 'gone', error: 'llama.cpp could not start it: out of memory.' } },
    ]
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: { engine, small: null, models } }),
      'DELETE /api/admin/models/coder/move': () => {
        models = models.filter((m) => m.name !== 'coder').concat({ ...flash, name: 'coder', move: null })
        return { status: 204 }
      },
    })
    renderApp('/admin/models')
    const card = (await screen.findByRole('heading', { name: /flash/ })).closest('section')!
    expect(within(card).getByText('Moving to llama.cpp')).toBeInTheDocument()
    expect(within(card).getByText(/Its answers on vLLM end first, on the same GPUs; then it loads on llama.cpp/)).toBeInTheDocument()
    expect(within(card).getByRole('button', { name: /Edit/ })).toBeDisabled()
    expect(within(card).getByRole('button', { name: /Unload/ })).toBeDisabled()
    const loading = screen.getByRole('heading', { name: /Qwen/ }).closest('section')!
    expect(within(loading).getByText(/Loading on SGLang: requests for it wait until it is ready there/)).toBeInTheDocument()
    const failed = screen.getByRole('heading', { name: /coder/ }).closest('section')!
    expect(within(failed).getByText('coder could not move to llama.cpp')).toBeInTheDocument()
    expect(within(failed).getByText(/llama.cpp could not start it: out of memory. It stays on vLLM, as it was./)).toBeInTheDocument()
    expect(within(failed).getByRole('button', { name: /Edit/ })).toBeEnabled()
    await userEvent.click(within(failed).getByRole('button', { name: 'Dismiss' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.path === '/api/admin/models/coder/move')).toBe(true))
    await waitFor(() => expect(screen.queryByText('coder could not move to llama.cpp')).not.toBeInTheDocument())
  })
})

// The Engine field: always there for a chat model, every engine listed (those that do not run here disabled, with why), the
// file following the engine (its other form preselected from the library) and the engine following the file.

const qwen3Gguf = 'Qwen/Qwen3-8B-GGUF/Qwen3-8B-Q4_K_M.gguf'
const qwen3Snapshot = 'hf/Qwen/Qwen3-8B@9c925d64'
const both = {
  library: [
    { path: 'meta/Llama-3.1-8B-Instruct-Q4_K_M.gguf', size: 5e9, parts: 1, profile: { ...profile, architecture: 'llama', name: 'Llama 3.1 8B Instruct', sizeLabel: '8B' }, usedBy: [] },
    { path: qwen3Gguf, size: 5e9, parts: 1, profile: { ...profile, architecture: 'qwen3', name: 'Qwen3 8B', sizeLabel: '8B' }, usedBy: ['Qwen3'] },
  ],
  snapshots: [
    snapshots[0],
    { path: qwen3Snapshot, repo: 'Qwen/Qwen3-8B', revision: '9c925d64d72725edaf899c6cb9c377fd0709d9c5', size: 16.4e9, complete: true, why: null, architectures: ['Qwen3ForCausalLM'], usedBy: [] },
  ],
}
const qwen3 = { ...qwen, name: 'Qwen3', file: qwen3Gguf, mtp: false, draftHead: null }
const onServer = (b: unknown) => ['vllm', 'sglang'].includes((b as { engineKind?: string }).engineKind ?? '')

/** A stack with these engines, models, library and downloads; `until`: the library and its snapshots are read once it settles. */
function stack(over: { instances?: object[]; models?: object[]; library?: object[]; snapshots?: object[]; downloads?: object[]; until?: Promise<unknown> } = {}) {
  let models = over.models ?? [qwen3]
  const calls = fakeApi(admin, {
    'GET /api/admin/models': () => ({ json: { engine: { ...engine, instances: over.instances ?? instances }, small: null, models } }),
    'GET /api/admin/models/library': () => ({ json: over.library ?? both.library, until: over.until }),
    'GET /api/admin/models/library/snapshots': () => ({ json: over.snapshots ?? both.snapshots, until: over.until }),
    'GET /api/admin/models/media/files': () => ({ json: [] }),
    'GET /api/admin/models/hf/downloads': () => ({ json: over.downloads ?? [] }),
    'POST /api/admin/models/advice': (b) => ({ json: onServer(b) ? vllmAdvice : llamaAdvice }),
    'PATCH /api/admin/models/Qwen3': () => ({ json: { restarting: false, warning: null } }),
    'PATCH /api/admin/models/flash': () => ({ json: { restarting: false, warning: null } }),
  })
  return { calls, set: (next: object[]) => (models = next) }
}

async function chooseEngine(dialog: HTMLElement, name: string) {
  await userEvent.click(within(dialog).getByRole('combobox', { name: 'Engine' }))
  await userEvent.click(await screen.findByRole('option', { name }))
}

describe('admin models: the Engine field', () => {
  it("shows a llama.cpp model's engine on its card and in its dialog, with vLLM and SGLang offered beside it", async () => {
    stack()
    renderApp('/admin/models')
    const card = (await screen.findByRole('heading', { name: /Qwen3/ })).closest('section')!
    expect(within(card).getByText('Engine: llama.cpp')).toBeInTheDocument()
    const dialog = await edit('Qwen3')
    const field = within(dialog).getByRole('combobox', { name: 'Engine' })
    expect(field).toHaveTextContent('llama.cpp')
    await userEvent.click(field)
    for (const name of ['llama.cpp', 'vLLM', 'SGLang']) expect(await screen.findByRole('option', { name })).not.toHaveAttribute('aria-disabled', 'true')
  })

  it('lists vLLM and SGLang disabled, with why and how to turn them on, when their features are off', async () => {
    stack({ instances: [instances[0], { ...instances[1], on: false }, { ...instances[2], on: false }] })
    renderApp('/admin/models')
    const dialog = await edit('Qwen3')
    expect(within(dialog).getByText(/vLLM is off here: the installer turns it on with \.\/arenactl repair --with vllm/)).toBeInTheDocument()
    expect(within(dialog).getByText(/SGLang is off here: the installer turns it on with \.\/arenactl repair --with sglang/)).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'Engine' }))
    expect(await screen.findByRole('option', { name: 'vLLM (off here)' })).toHaveAttribute('aria-disabled', 'true')
    expect(screen.getByRole('option', { name: 'SGLang (off here)' })).toHaveAttribute('aria-disabled', 'true')
    expect(screen.getByRole('option', { name: 'llama.cpp' })).not.toHaveAttribute('aria-disabled', 'true')
  })

  it("moves a llama.cpp model to vLLM from the field: the library's snapshots listed first, its own preselected and shown, saved as vLLM's", async () => {
    const { calls } = stack()
    renderApp('/admin/models')
    const dialog = await edit('Qwen3')
    await chooseEngine(dialog, 'vLLM')
    expect(await within(dialog).findByRole('form', { name: 'The vLLM model' })).toBeInTheDocument()
    expect(within(dialog).getByRole('combobox', { name: 'File or model' })).toHaveTextContent(qwen3Snapshot)
    expect(within(dialog).getByText(/Its snapshot in the library, by its name and architecture/)).toBeInTheDocument()
    expect(within(dialog).getByText('Moves Qwen3 to vLLM')).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'File or model' }))
    const groups = await screen.findAllByRole('group')
    expect(groups[0]).toHaveTextContent(/Hugging Face snapshots/)
    expect(within(groups[0]).getByRole('option', { name: /flash@r/ })).toBeInTheDocument()
    await userEvent.keyboard('{Escape}')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PATCH' && c.path === '/api/admin/models/Qwen3')).toBe(true))
    expect(calls.find((c) => c.method === 'PATCH')!.body).toMatchObject({ engineKind: 'vllm', file: qwen3Snapshot })
  })

  it('says so plainly when the library holds no snapshot of the model, how to get one, and holds Save', async () => {
    stack({ snapshots: [] })
    renderApp('/admin/models')
    const dialog = await edit('Qwen3')
    await chooseEngine(dialog, 'vLLM')
    expect(await within(dialog).findByText('No Hugging Face snapshot of Qwen3 in the library')).toBeInTheDocument()
    expect(within(dialog).getByText(/Find on Hugging Face/)).toBeInTheDocument()
    expect(within(dialog).getByText('Snapshots')).toBeInTheDocument()
    // With no network here: downloaded on a machine that has one, into the folder the library reads, and carried over.
    expect(within(dialog).getByText('hf download <owner>/<repository> --revision <revision> --local-dir <library>/hf/<owner>/<repository>@<revision>')).toBeInTheDocument()
    expect(within(dialog).queryByRole('form', { name: 'The chat model' })).not.toBeInTheDocument()
    const save = within(dialog).getByRole('button', { name: 'Save' })
    expect(save).toBeDisabled()
    expect(save).toHaveAccessibleDescription(/No Hugging Face snapshot of Qwen3 in the library/)
  })

  it("moves a vLLM model to SGLang on the same snapshot with one engine of each, without what is vLLM's alone", async () => {
    const { calls } = stack({ models: [{ ...flash, kvTier: 'lmcache', kvDiskGb: 50 }] })
    renderApp('/admin/models')
    const dialog = await edit('flash')
    expect(within(dialog).getByRole('combobox', { name: 'Engine' })).toHaveTextContent('vLLM')
    expect(within(dialog).queryByText(/^Moves flash/)).not.toBeInTheDocument()
    await chooseEngine(dialog, 'SGLang')
    const form = await within(dialog).findByRole('form', { name: 'The SGLang model' })
    expect(within(dialog).getByText('Moves flash to SGLang')).toBeInTheDocument()
    expect(within(dialog).getByRole('combobox', { name: 'File or model' })).toHaveTextContent('hf/org/flash@r')
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PATCH' && c.path === '/api/admin/models/flash')).toBe(true))
    const sent = calls.find((c) => c.method === 'PATCH')!.body as Record<string, unknown> & { clear: string[] }
    expect(sent).toMatchObject({ engineKind: 'sglang', file: 'hf/org/flash@r' })
    expect(sent.kvTier).toBeUndefined()
    expect(sent.clear).toEqual(expect.arrayContaining(['engine', 'kvTier']))
  })

  it("moves a vLLM model back to llama.cpp from the field: GGUF files listed first, its own preselected, saved as llama.cpp's", async () => {
    const { calls } = stack({ models: [{ ...flash, file: qwen3Snapshot }] })
    renderApp('/admin/models')
    const dialog = await edit('flash')
    await chooseEngine(dialog, 'llama.cpp')
    expect(await within(dialog).findByRole('form', { name: 'The chat model' })).toBeInTheDocument()
    expect(within(dialog).getByRole('combobox', { name: 'File or model' })).toHaveTextContent(qwen3Gguf)
    expect(within(dialog).getByText(/Its GGUF file in the library, by its name and architecture/)).toBeInTheDocument()
    expect(within(dialog).getByText('Moves flash to llama.cpp')).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'File or model' }))
    expect((await screen.findAllByRole('group'))[0]).toHaveTextContent(/GGUF files/)
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Save' })).toBeEnabled())
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PATCH' && c.path === '/api/admin/models/flash')).toBe(true))
    expect(calls.find((c) => c.method === 'PATCH')!.body).toMatchObject({ engineKind: 'llamacpp', file: qwen3Gguf })
  })

  it('moves the Engine field with a file of the other form chosen under File or model, and back', async () => {
    stack()
    renderApp('/admin/models')
    const dialog = await edit('Qwen3')
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'File or model' }))
    await userEvent.click(await screen.findByRole('option', { name: /flash@r/ }))
    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: 'Engine' })).toHaveTextContent('vLLM'))
    expect(within(dialog).getByText('Moves Qwen3 to vLLM')).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'File or model' }))
    await userEvent.click(await screen.findByRole('option', { name: /Llama-3\.1-8B/ }))
    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: 'Engine' })).toHaveTextContent('llama.cpp'))
    expect(within(dialog).queryByText(/^Moves Qwen3/)).not.toBeInTheDocument()
  })

  it('holds the Engine field, saying why, once the model is moving', async () => {
    const { set } = stack()
    const { client } = renderApp('/admin/models')
    const dialog = await edit('Qwen3')
    expect(within(dialog).getByRole('combobox', { name: 'Engine' })).toBeEnabled()
    const at = '2026-11-02T10:00:00Z'
    set([{ ...qwen3, move: { phase: 'draining', from: 'llamacpp', to: 'vllm', fromEngine: 'llamacpp', toEngine: 'vllm', old: 'serving', since: at, at, by: 'ada', error: null } }])
    await client.invalidateQueries({ queryKey: ['admin', 'models'] })
    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: 'Engine' })).toBeDisabled())
    expect(within(dialog).getByRole('combobox', { name: 'Engine' })).toHaveAccessibleDescription(/It is moving from llama\.cpp to vLLM/)
  })

  it('opens Edit with the Engine field focused from Change engine… on the card, and names each engine of a kind that has several', async () => {
    stack({ instances: [...instances, { name: 'vllm-b', kind: 'vllm', managed: true, problem: null, on: true }], models: [qwen3, { ...flash, engine: 'vllm-b' }] })
    renderApp('/admin/models')
    const card = (await screen.findByRole('heading', { name: /flash/ })).closest('section')!
    expect(within(card).getByText('Engine: vLLM · vllm-b')).toBeInTheDocument()
    const qwenCard = screen.getByRole('heading', { name: /Qwen3/ }).closest('section')!
    await userEvent.click(within(qwenCard).getByRole('button', { name: 'Change engine…' }))
    const dialog = await screen.findByRole('dialog', { name: 'Edit Qwen3' })
    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: 'Engine' })).toHaveFocus())
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'Engine' }))
    expect(await screen.findByRole('option', { name: 'vLLM · vllm' })).toBeInTheDocument()
    expect(screen.getByRole('option', { name: 'vLLM · vllm-b' })).toBeInTheDocument()
  })
})

// The Engine field with the library still being read, files chosen and brought back, an engine that is off, and a model
// another admin moved while its dialog was open.

const qwen3Q8 = 'Qwen/Qwen3-8B-GGUF/Qwen3-8B-Q8_0.gguf'

async function add() {
  await userEvent.click(await screen.findByRole('button', { name: 'Add a model' }))
  return screen.findByRole('dialog', { name: 'Add a model' })
}

async function chooseFile(dialog: HTMLElement, name: RegExp) {
  await userEvent.click(within(dialog).getByRole('combobox', { name: 'File or model' }))
  await userEvent.click(await screen.findByRole('option', { name }))
}

describe('admin models: the Engine field as the library and the page change', () => {
  it('holds the Engine field while the library is read, so a model moved at once keeps its own snapshot', async () => {
    let read = () => {}
    stack({ models: [flash], until: new Promise<void>((resolve) => (read = resolve)) })
    renderApp('/admin/models')
    const card = (await screen.findByRole('heading', { name: /flash/ })).closest('section')!
    await userEvent.click(within(card).getByRole('button', { name: 'Change engine…' }))
    const dialog = await screen.findByRole('dialog', { name: 'Edit flash' })
    const field = within(dialog).getByRole('combobox', { name: 'Engine' })
    expect(field).toBeDisabled()
    expect(field).toHaveAccessibleDescription(/Reading the library…/)
    read()
    await waitFor(() => expect(field).toBeEnabled())
    await waitFor(() => expect(field).toHaveFocus())
    await chooseEngine(dialog, 'SGLang')
    expect(await within(dialog).findByRole('form', { name: 'The SGLang model' })).toBeInTheDocument()
    expect(within(dialog).getByRole('combobox', { name: 'File or model' })).toHaveTextContent('hf/org/flash@r')
    expect(within(dialog).queryByText(/No Hugging Face snapshot/)).not.toBeInTheDocument()
  })

  it('saves a vLLM model pinned to the first engine of its kind with that engine, as it was', async () => {
    const { calls } = stack({ models: [{ ...flash, engine: 'vllm' }] })
    renderApp('/admin/models')
    const dialog = await edit('flash')
    const form = await within(dialog).findByRole('form', { name: 'The vLLM model' })
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PATCH' && c.path === '/api/admin/models/flash')).toBe(true))
    const sent = calls.find((c) => c.method === 'PATCH')!.body as { engine?: string; clear: string[] }
    expect(sent.engine).toBe('vllm')
    expect(sent.clear).not.toContain('engine')
  })

  it('saves a vLLM model whose kind this stack does not list without naming an engine, as it was', async () => {
    const { calls } = stack({ instances: [instances[0], instances[2]], models: [flash] })
    renderApp('/admin/models')
    const dialog = await edit('flash')
    const form = await within(dialog).findByRole('form', { name: 'The vLLM model' })
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PATCH' && c.path === '/api/admin/models/flash')).toBe(true))
    const sent = calls.find((c) => c.method === 'PATCH')!.body as { engineKind: string; engine?: string; clear: string[] }
    expect(sent.engineKind).toBe('vllm')
    expect(sent.engine).toBeUndefined()
    expect(sent.clear).toContain('engine')
  })

  it('brings back the file chosen when the engine goes to the other form and back', async () => {
    const q8 = { path: qwen3Q8, size: 9e9, parts: 1, profile: { ...profile, architecture: 'qwen3', name: 'Qwen3 8B', sizeLabel: '8B', quant: 'Q8_0' }, usedBy: [] }
    stack({ library: [...both.library, q8] })
    renderApp('/admin/models')
    const dialog = await add()
    await chooseFile(dialog, /Qwen3-8B-Q8_0\.gguf/)
    await within(dialog).findByRole('form', { name: 'The chat model' })
    await chooseEngine(dialog, 'vLLM')
    await within(dialog).findByRole('form', { name: 'The vLLM model' })
    expect(within(dialog).getByRole('combobox', { name: 'File or model' })).toHaveTextContent(qwen3Snapshot)
    await chooseEngine(dialog, 'llama.cpp')
    await within(dialog).findByRole('form', { name: 'The chat model' })
    expect(within(dialog).getByRole('combobox', { name: 'File or model' })).toHaveTextContent(qwen3Q8)
    expect(within(dialog).queryByText(/Its GGUF file in the library, by its/)).not.toBeInTheDocument()
  })

  it('says why Add model is held, and shows File or model as the dialog has it when the other engine finds no match', async () => {
    stack({ snapshots: [] })
    renderApp('/admin/models')
    const dialog = await add()
    const held = await within(dialog).findByRole('button', { name: 'Add model' })
    expect(held).toBeDisabled()
    expect(held).toHaveAccessibleDescription(/Choose its GGUF file under File or model/)
    await chooseFile(dialog, /Qwen3-8B-Q4_K_M\.gguf/)
    await within(dialog).findByRole('form', { name: 'The chat model' })
    // vLLM finds no snapshot of it: the dialog has no file, and File or model says so.
    await chooseEngine(dialog, 'vLLM')
    expect(await within(dialog).findByText('No Hugging Face snapshot of Qwen3-8B-Q4_K_M in the library')).toBeInTheDocument()
    const file = within(dialog).getByRole('combobox', { name: 'File or model' })
    expect(file).toHaveTextContent('Choose a file or model')
    expect(within(dialog).getByRole('button', { name: 'Add model' })).toHaveAccessibleDescription(/No Hugging Face snapshot of Qwen3-8B-Q4_K_M in the library/)
    // The same file chosen again takes llama.cpp back.
    await chooseFile(dialog, /Qwen3-8B-Q4_K_M\.gguf/)
    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: 'Engine' })).toHaveTextContent('llama.cpp'))
    await within(dialog).findByRole('form', { name: 'The chat model' })
    // Away and back with the Engine field: its file comes back with llama.cpp.
    await chooseEngine(dialog, 'vLLM')
    await within(dialog).findByText('No Hugging Face snapshot of Qwen3-8B-Q4_K_M in the library')
    await chooseEngine(dialog, 'llama.cpp')
    await within(dialog).findByRole('form', { name: 'The chat model' })
    expect(within(dialog).getByRole('combobox', { name: 'File or model' })).toHaveTextContent(qwen3Gguf)
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Add model' })).toBeEnabled())
  })

  it('keeps a vLLM model on vLLM when a GGUF file is chosen while llama.cpp is off, saying what turns it on', async () => {
    stack({ instances: [{ ...instances[0], on: false }, instances[1], instances[2]], models: [flash] })
    renderApp('/admin/models')
    const dialog = await edit('flash')
    await within(dialog).findByRole('form', { name: 'The vLLM model' })
    await chooseFile(dialog, /Qwen3-8B-Q4_K_M\.gguf/)
    await within(dialog).findByText('llama.cpp does not run here')
    expect(within(dialog).getByRole('combobox', { name: 'Engine' })).toHaveTextContent('vLLM')
    expect(within(dialog).queryByText(/^Moves flash/)).not.toBeInTheDocument()
    expect(within(dialog).getByRole('combobox', { name: 'File or model' })).toHaveAccessibleDescription(
      /A GGUF file needs llama\.cpp: choose a snapshot for vLLM\. llama\.cpp is off here: the installer turns it on with .*repair --with chat-models/,
    )
    const save = within(dialog).getByRole('button', { name: 'Save' })
    expect(save).toBeDisabled()
    expect(save).toHaveAccessibleDescription(/llama\.cpp does not run here/)
  })

  it('holds an open dialog once another admin moved its model elsewhere, so Save cannot move it back unasked', async () => {
    const { set, calls } = stack({ models: [flash] })
    const { client } = renderApp('/admin/models')
    const dialog = await edit('flash')
    await within(dialog).findByRole('form', { name: 'The vLLM model' })
    set([{ ...flash, engineKind: 'sglang' }])
    await client.invalidateQueries({ queryKey: ['admin', 'models'] })
    expect(await within(dialog).findByText('It moved to SGLang while this was open: close and edit it again.')).toBeInTheDocument()
    expect(within(dialog).queryByRole('form', { name: 'The vLLM model' })).not.toBeInTheDocument()
    expect(within(dialog).getByRole('combobox', { name: 'Engine' })).toBeDisabled()
    const save = within(dialog).getByRole('button', { name: 'Save' })
    expect(save).toBeDisabled()
    expect(save).toHaveAccessibleDescription(/It moved to SGLang while this was open/)
    expect(calls.some((c) => c.method === 'PATCH')).toBe(false)
  })

  it('says how to turn an engine on with the offline installer as well as from the checkout', async () => {
    stack({ instances: [instances[0], { ...instances[1], on: false }, instances[2]] })
    renderApp('/admin/models')
    const dialog = await edit('Qwen3')
    expect(
      within(dialog).getByText(/vLLM is off here: the installer turns it on with \.\/arenactl repair --with vllm from the checkout, or sh argus-arena-VERSION-offline\.run repair --with vllm with the offline installer/),
    ).toBeInTheDocument()
  })

  it('adds a downloaded snapshot as a vLLM or SGLang model: its folder chosen, the Engine field held until the library is read', async () => {
    let read = () => {}
    const at = '2026-11-02T10:00:00Z'
    const files = ['config.json', 'model.safetensors', 'tokenizer.json'].map((f) => ({ path: f, size: 1e9, library: `${qwen3Snapshot}/${f}` }))
    stack({
      downloads: [{ id: 'd1', kind: 'files', repo: 'Qwen/Qwen3-8B', state: 'done', bytes: 3e9, total: 3e9, speed: null, error: null, createdAt: at, snapshot: qwen3Snapshot, files }],
      until: new Promise<void>((resolve) => (read = resolve)),
    })
    renderApp('/admin/models')
    const downloads = await screen.findByRole('region', { name: 'Downloads' })
    await userEvent.click(within(downloads).getByRole('button', { name: /Add as a model/ }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a model' })
    expect(within(dialog).getByRole('combobox', { name: 'Engine' })).toBeDisabled()
    read()
    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: 'Engine' })).toHaveTextContent('vLLM'))
    expect(within(dialog).getByRole('combobox', { name: 'File or model' })).toHaveTextContent(qwen3Snapshot)
    expect(await within(dialog).findByRole('form', { name: 'The vLLM model' })).toBeInTheDocument()
  })
})
