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
  limits: { context: { min: 4096, max: 131072 }, maxOutput: { min: 256, max: 31744 }, parallel: { min: 1, max: 1024 } },
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
    expect(within(form).getByText(/Fits this machine: GPUs 1, 131,072 tokens a person, 41 at once/)).toBeInTheDocument()
    // Each person holds the whole context, its answer included: the longest answer leaves its prompt room in it, and is half of it when empty.
    expect(within(form).getByText("4,096 to 131,072 tokens: what each person's conversation holds, its prompt and its answer together, however many are at once.")).toBeInTheDocument()
    expect(within(form).getByText('256 to 31,744: the context less room for a prompt. Empty: 16,384.')).toBeInTheDocument()
    await userEvent.type(within(form).getByLabelText('Longest answer'), '8192')
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
    expect(sent).toMatchObject({ name: 'gpt-oss-120b', engineKind: 'vllm', file: 'hf/openai/gpt-oss-120b@main', devices: '2,3', replicas: 2, sleep: false, fallback: 'Qwen', kvType: 'fp8', maxOutput: 8192 })
  })

  it("says how an FP4 snapshot's weights run on its GPUs and what memory they take", async () => {
    const fp4 = [{ ...snapshots[0], path: 'hf/nvidia/flash-nvfp4@r', repo: 'nvidia/Qwen3.8-Flash-Next-NVFP4', precision: 'NVFP4 + FP8' }]
    const precision = {
      weights: 'NVFP4 + FP8', runs: 'W4A16', kernel: 'FP4 Marlin', native: false, gpu: 'NVIDIA H200 NVL (Hopper, sm_90)',
      summary: 'NVFP4 + FP8 weights on NVIDIA H200 NVL (Hopper, sm_90), which has no FP4 tensor cores: vLLM keeps them 4-bit in memory and widens them as it reads them.',
      refused: false, weightBytes: 116e9, bf16Bytes: 412e9,
    }
    fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: { engine: engine(), small: null, models } }),
      'GET /api/admin/models/library': () => ({ json: [] }),
      'GET /api/admin/models/library/snapshots': () => ({ json: fp4 }),
      'GET /api/admin/models/media/files': () => ({ json: [] }),
      'POST /api/admin/models/advice': () => ({ json: { ...advice, precision } }),
    })
    renderApp('/admin/models')
    await userEvent.click(await screen.findByRole('button', { name: 'Add a model' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a model' })
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'File or model' }))
    await userEvent.click(await screen.findByRole('option', { name: /flash-nvfp4@r · .* · vLLM/ }))
    const form = await within(dialog).findByRole('form', { name: 'The vLLM model' })
    expect(await within(form).findByText(/^NVFP4 \+ FP8 · W4A16, weight-only on NVIDIA H200 NVL \(Hopper, sm_90\) · weights 108\.0 GiB, about 383\.7 GiB at BF16\.$/)).toBeInTheDocument()
    expect(within(form).getByText(/which has no FP4 tensor cores/)).toBeInTheDocument()
    expect(within(form).getByText(/· NVFP4 \+ FP8 ·/)).toBeInTheDocument()
  })

  it("moves a llama.cpp model to vLLM: its snapshot chosen in its own dialog, saved under its name with vLLM's defaults", async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: { engine: engine(), small: null, models } }),
      'GET /api/admin/models/library': () => ({ json: [] }),
      'GET /api/admin/models/library/snapshots': () => ({ json: snapshots }),
      'GET /api/admin/models/media/files': () => ({ json: [] }),
      'POST /api/admin/models/advice': () => ({ json: advice }),
      'PATCH /api/admin/models/Qwen': () => ({ json: { restarting: false, warning: null } }),
    })
    renderApp('/admin/models')
    const card = (await screen.findByRole('heading', { name: /Qwen/ })).closest('section')!
    await userEvent.click(within(card).getByRole('button', { name: /Edit/ }))
    const dialog = await screen.findByRole('dialog', { name: 'Edit Qwen' })
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'File or model' }))
    await userEvent.click(await screen.findByRole('option', { name: /gpt-oss-120b@main · .* · vLLM/ }))
    expect(await within(dialog).findByText('Moves Qwen to vLLM')).toBeInTheDocument()
    const form = await within(dialog).findByRole('form', { name: 'The vLLM model' })
    expect(within(form).getByLabelText('Name')).toHaveValue('Qwen')
    expect(within(form).getByLabelText('Name')).toBeDisabled()
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PATCH' && c.path === '/api/admin/models/Qwen')).toBe(true))
    const sent = calls.find((c) => c.method === 'PATCH' && c.path === '/api/admin/models/Qwen')!.body as Record<string, unknown>
    expect(sent).toMatchObject({ engineKind: 'vllm', file: 'hf/openai/gpt-oss-120b@main', kvType: 'fp8', parallel: 16 })
    // It set no longest answer: half of what each person's conversation holds.
    expect(sent.clear).toContain('maxOutput')
  })

  it('adds a snapshot as an SGLang model: the engine chosen, its own flags in the hints, no LMCache; an engine whose feature is off is disabled, saying why', async () => {
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
    expect(within(dialog).getByText(/SGLang · sglang-b is off here: the installer turns it on with \.\/arenactl repair --with sglang/)).toBeInTheDocument()
    await userEvent.click(await within(dialog).findByRole('combobox', { name: 'Engine' }))
    expect(await screen.findByRole('option', { name: 'SGLang · sglang-b (off here)' })).toHaveAttribute('aria-disabled', 'true')
    await userEvent.click(screen.getByRole('option', { name: 'SGLang · sglang' }))
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

  it("adds a snapshot as an LMDeploy model with only LMDeploy's fields, and a GGUF file of Strata's family as a Strata model", async () => {
    const instances = [
      { name: 'llamacpp', kind: 'llamacpp', managed: true, problem: null, on: true },
      { name: 'vllm', kind: 'vllm', managed: true, problem: null, on: true },
      { name: 'lmdeploy', kind: 'lmdeploy', managed: true, problem: null, on: true },
      { name: 'strata', kind: 'strata', managed: true, problem: null, on: true },
    ]
    const flash = {
      path: 'unsloth/Qwen3.8-Flash-Next-UD-IQ4_XS-00001-of-00003.gguf', size: 94e9, parts: 3, usedBy: [],
      profile: {
        kind: 'language', why: null, note: null, architecture: 'qwen4exp', name: 'Qwen3.8-Flash-Next', sizeLabel: '400B', quant: 'IQ4_XS', structure: 'moe', attention: 'hybrid',
        parameters: 400e9, activeParameters: 20e9, bitsPerWeight: 1.9, weightBytes: 94e9, layers: 48, attentionLayers: 12, experts: null, trainedContext: 262144,
        slidingWindow: null, embeddingLength: 4096, vocabSize: 248320, kvBytesPerToken: { q8_0: 12288, f16: 24576 }, recurrentBytesPerSlot: 67e6, mtpLayers: 0,
        thinking: true, tools: true, sampling: null, ropeScaling: null, canStretch: false, projector: null, approximate: false,
      },
    }
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: { engine: engine({ instances }), small: null, models } }),
      'GET /api/admin/models/library': () => ({ json: [flash] }),
      'GET /api/admin/models/library/snapshots': () => ({ json: snapshots }),
      'GET /api/admin/models/media/files': () => ({ json: [] }),
      'POST /api/admin/models/advice': () => ({ json: advice }),
      'POST /api/admin/models': () => ({ status: 201, json: { restarting: true, warning: null } }),
    })
    renderApp('/admin/models')
    await userEvent.click(await screen.findByRole('button', { name: 'Add a model' }))
    let dialog = await screen.findByRole('dialog', { name: 'Add a model' })
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'File or model' }))
    // Snapshots are for vLLM and LMDeploy here; Strata runs none.
    await userEvent.click(await screen.findByRole('option', { name: /gpt-oss-120b@main · .* · vLLM or LMDeploy/ }))
    await userEvent.click(await within(dialog).findByRole('combobox', { name: 'Engine' }))
    await userEvent.click(await screen.findByRole('option', { name: 'LMDeploy' }))
    let form = await within(dialog).findByRole('form', { name: 'The LMDeploy model' })
    expect(within(form).getByText(/--cache-max-entry-count/)).toBeInTheDocument()
    expect(within(form).getByText(/--max-batch-size/)).toBeInTheDocument()
    // What LMDeploy has none of is not offered.
    for (const gone of ['Speculative decoding', 'Cache beyond the GPU', 'Quantisation']) expect(within(form).queryByLabelText(gone)).not.toBeInTheDocument()
    expect(within(form).queryByRole('switch', { name: 'May sleep' })).not.toBeInTheDocument()
    expect(within(form).getByText(/LMDeploy does not sleep/)).toBeInTheDocument()
    await userEvent.click(within(form).getByRole('button', { name: 'Add model' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/admin/models')).toBe(true))
    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/admin/models')!.body).toMatchObject({ engineKind: 'lmdeploy', sleep: false, file: 'hf/openai/gpt-oss-120b@main' })

    await userEvent.click(await screen.findByRole('button', { name: 'Add a model' }))
    dialog = await screen.findByRole('dialog', { name: 'Add a model' })
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'File or model' }))
    await userEvent.click(await screen.findByRole('option', { name: /Qwen3\.8-Flash-Next-UD-IQ4_XS/ }))
    await userEvent.click(await within(dialog).findByRole('combobox', { name: 'Engine' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Strata' }))
    form = await within(dialog).findByRole('form', { name: 'The Strata model' })
    // One copy, 1 to 8 at once: no Copies field, and its own batch in the hint.
    expect(within(form).queryByLabelText('Copies')).not.toBeInTheDocument()
    expect(within(form).getByText(/--parallel, 1 to 8/)).toBeInTheDocument()
    expect(within(form).getByLabelText('People at once')).toHaveValue('8')
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
