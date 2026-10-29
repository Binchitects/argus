import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

// Admin → Models with vLLM and SGLang: a snapshot from the library added as a vLLM model (its GPUs by vendor and size, its
// copies, share, cache, fallback, the advisor's fit) or an SGLang one, a model asleep or draining, and an engine that is not running.

const everyone = { audience: 'Everyone', groups: [] }
const gpu = (index: number) => ({ index, name: 'NVIDIA RTX PRO 6000', total: 102.6e9, vendor: 'nvidia', backend: 'cuda' })
const engine = (over: object = {}) => ({
  enabled: true, error: null, checkedAt: '2026-10-16T10:00:00Z', kept: [], pinned: [], max: 2, hours: null, onRequest: true, loaded: ['Qwen'], loading: [], unloading: [],
  gpus: [gpu(0), gpu(1), gpu(2), gpu(3)],
  plan: { problems: [], gpus: [{ index: 3, name: 'AMD Instinct MI300X', vendor: 'amd', total: 206e9, budget: 200e9, need: 20e9, models: ['oss'] }] },
  instances: [
    { name: 'llamacpp', kind: 'llamacpp', managed: true, problem: null },
    { name: 'vllm', kind: 'vllm', managed: true, problem: null },
  ],
  ...over,
})
const models = [
  { name: 'Qwen', source: 'local', mode: 'chat', status: 'loaded', engineKind: 'llamacpp', file: 'q/Qwen.gguf', context: 32768, vision: false, access: everyone, kept: false },
  { name: 'flash', source: 'local', mode: 'chat', status: 'sleeping', engineKind: 'vllm', file: 'hf/org/flash@r', context: 65536, vision: false, access: everyone, kept: false },
  { name: 'coder', source: 'local', mode: 'chat', status: 'unloading', engineKind: 'vllm', file: 'hf/org/coder@r', context: 65536, vision: false, access: everyone, kept: false },
]
const snapshots = [
  { path: 'hf/openai/gpt-oss-120b@main', repo: 'openai/gpt-oss-120b', revision: 'b5c939de8f754692c1647ca79fbf85e8c1e70f8a', size: 65.2e9, complete: true, why: null, architectures: ['GptOssForCausalLM'], usedBy: [] },
]
const advice = {
  limits: { context: { min: 4096, max: 131072 }, parallel: { min: 1, max: 1024 } },
  recommended: { context: 131072, maxOutput: 32768, parallel: 41, kvType: 'fp8', devices: '1' },
  estimate: { gpuWeights: 65.2e9, gpuCache: 23.9e9, gpuTotal: 92.4e9, gpuBudget: 101.6e9, ramTotal: 65.2e9, fit: 'gpu', approximate: false },
  problems: [],
}

describe('admin models: vLLM', () => {
  it('adds a snapshot as a vLLM model on the GPUs chosen by vendor and size, with its copies and fallback', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: { engine: engine(), small: null, models } }),
      'GET /api/admin/models/library': () => ({ json: [] }),
      'GET /api/admin/models/library/snapshots': () => ({ json: snapshots }),
      'GET /api/admin/models/media/files': () => ({ json: [] }),
      'POST /api/admin/models/advice': () => ({ json: advice }),
      'POST /api/admin/models': () => ({ status: 201, json: { restarting: true, warning: null } }),
    })
    renderApp('/admin/models')
    await userEvent.click(await screen.findByRole('button', { name: 'Add a model' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a model' })
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'File or model' }))
    await userEvent.click(await screen.findByRole('option', { name: /gpt-oss-120b@main · .* · vLLM/ }))
    const form = await within(dialog).findByRole('form', { name: 'The vLLM model' })
    await waitFor(() => expect(within(form).getByLabelText('Name')).toHaveValue('gpt-oss-120b'))
    // The advisor's fit and proposal show as it is filled in.
    expect(await within(form).findByText(/GPUs 1, proposed for this machine/)).toBeInTheDocument()
    expect(within(form).getByText(/Fits this machine: GPUs 1, 131,072 tokens of context, 41 answers at once/)).toBeInTheDocument()
    // The GPUs by vendor and size: a group can be ticked whole.
    expect(within(form).getByRole('checkbox', { name: /Every nvidia · NVIDIA RTX PRO 6000/ })).toBeInTheDocument()
    // Two copies on GPUs 2 and 3, by ticking them; a fallback; no sleep.
    await userEvent.click(within(form).getByRole('checkbox', { name: 'GPU 2' }))
    await userEvent.click(within(form).getByRole('checkbox', { name: 'GPU 3' }))
    await userEvent.clear(within(form).getByLabelText('Copies'))
    await userEvent.type(within(form).getByLabelText('Copies'), '2')
    await userEvent.click(within(form).getByRole('switch', { name: 'May sleep' }))
    await userEvent.click(within(form).getByRole('combobox', { name: 'Fallback' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Qwen' }))
    await userEvent.click(within(form).getByRole('button', { name: 'Add model' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/admin/models')).toBe(true))
    const sent = calls.find((c) => c.method === 'POST' && c.path === '/api/admin/models')!.body as Record<string, unknown>
    expect(sent).toMatchObject({ name: 'gpt-oss-120b', engineKind: 'vllm', file: 'hf/openai/gpt-oss-120b@main', devices: '2,3', replicas: 2, sleep: false, fallback: 'Qwen', kvType: 'fp8' })
  })

  it('adds a snapshot as an SGLang model: the engine chosen, its own flags in the hints, no LMCache; an engine whose feature is off is not offered', async () => {
    const instances = [
      { name: 'llamacpp', kind: 'llamacpp', managed: true, problem: null, on: true },
      { name: 'vllm', kind: 'vllm', managed: true, problem: null, on: true },
      { name: 'sglang', kind: 'sglang', managed: true, problem: null, on: true },
      { name: 'sglang-b', kind: 'sglang', managed: true, problem: null, on: false },
    ]
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: { engine: engine({ instances }), small: null, models } }),
      'GET /api/admin/models/library': () => ({ json: [] }),
      'GET /api/admin/models/library/snapshots': () => ({ json: snapshots }),
      'GET /api/admin/models/media/files': () => ({ json: [] }),
      'POST /api/admin/models/advice': () => ({ json: advice }),
      'POST /api/admin/models': () => ({ status: 201, json: { restarting: true, warning: null } }),
    })
    renderApp('/admin/models')
    await userEvent.click(await screen.findByRole('button', { name: 'Add a model' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a model' })
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'File or model' }))
    await userEvent.click(await screen.findByRole('option', { name: /gpt-oss-120b@main · .* · vLLM or SGLang/ }))
    await userEvent.click(await within(dialog).findByRole('combobox', { name: 'Engine' }))
    expect(screen.queryByRole('option', { name: /sglang-b/ })).not.toBeInTheDocument()
    await userEvent.click(await screen.findByRole('option', { name: 'SGLang · sglang' }))
    const form = await within(dialog).findByRole('form', { name: 'The SGLang model' })
    expect(within(form).getByText(/--mem-fraction-static/)).toBeInTheDocument()
    expect(within(form).getByText(/--max-running-requests/)).toBeInTheDocument()
    await userEvent.click(within(form).getByRole('combobox', { name: 'Cache beyond the GPU' }))
    expect(await screen.findByRole('option', { name: "SGLang's own (RAM)" })).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: /LMCache/ })).not.toBeInTheDocument()
    await userEvent.keyboard('{Escape}')
    await userEvent.click(within(form).getByRole('button', { name: 'Add model' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/admin/models')).toBe(true))
    const sent = calls.find((c) => c.method === 'POST' && c.path === '/api/admin/models')!.body as Record<string, unknown>
    // The first SGLang engine is the kind's default: none named.
    expect(sent).toMatchObject({ name: 'gpt-oss-120b', engineKind: 'sglang', file: 'hf/openai/gpt-oss-120b@main' })
    expect(sent.engine).toBeUndefined()
  })

  it('shows a model asleep, one draining, a GPU of another vendor, and an engine that is not running', async () => {
    fakeApi(admin, {
      'GET /api/admin/models': () => ({
        json: { engine: engine({ instances: [{ name: 'vllm', kind: 'vllm', managed: true, problem: 'The vLLM engine vllm is not running.' }] }), small: null, models },
      }),
    })
    renderApp('/admin/models')
    expect(await screen.findByText('Asleep')).toBeInTheDocument()
    expect(screen.getByText('Draining…')).toBeInTheDocument()
    expect(screen.getByText(/GPU 3 · AMD Instinct MI300X · amd/)).toBeInTheDocument()
    expect(screen.getByText('The vLLM engine vllm is not running.')).toBeInTheDocument()
  })
})
