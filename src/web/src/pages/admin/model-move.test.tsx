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

  it('moves a vLLM model to SGLang: the engine chosen, the dialog says it moves, the save names SGLang', async () => {
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
    const form = await within(dialog).findByRole('form', { name: 'The vLLM model' })
    expect(within(form).queryByText(/^Moves flash/)).not.toBeInTheDocument()
    await userEvent.click(within(form).getByRole('combobox', { name: 'Engine' }))
    await userEvent.click(await screen.findByRole('option', { name: 'SGLang · sglang' }))
    const sglang = await within(dialog).findByRole('form', { name: 'The SGLang model' })
    expect(within(sglang).getByText('Moves flash to SGLang')).toBeInTheDocument()
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
    expect(await within(dialog).findByText('Moves Qwen to vLLM or SGLang')).toBeInTheDocument()
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
