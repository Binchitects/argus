import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, member, renderApp } from '@/test/utils'
import { consequence, pollEvery, type Runtime } from './runtimes-api'

const row = (over: Partial<Runtime>): Runtime => ({
  id: 'llamacpp', kind: 'engine', label: 'llama.cpp', state: 'up', since: null, detail: '1 model loaded: Big-Model.', busy: 2, waiting: 1,
  actions: ['reload', 'restart'], last: null, ...over,
})

const runtimes: Runtime[] = [
  row({ last: { action: 'restart', by: 'ada', at: new Date().toISOString() } }),
  row({ id: 'imagegen', kind: 'media', label: 'Pictures (imagegen)', detail: 'Running FLUX.2-klein-4B.', busy: 0, waiting: 0, actions: ['restart'] }),
  row({ id: 'sandbox', kind: 'sandbox', label: 'Python sandbox', detail: '1 of 2 slots busy.', busy: 1, waiting: 2, actions: ['reset'] }),
  row({ id: 'lean', kind: 'sandbox', label: 'Proof checker (Lean)', state: 'down', detail: 'The proof checker (Lean) is not running.', busy: 0, waiting: 0, actions: ['reset'] }),
]

const item = (label: string) => screen.getByText(label, { selector: 'p > *, p' }).closest('li')!

describe('engines and sandboxes', () => {
  it('lists each with its state and the buttons its kind has, and asks before an engine restart says what it cuts', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/services': () => ({ json: [] }),
      'GET /api/admin/runtimes': () => ({ json: runtimes }),
      'POST /api/admin/runtimes/llamacpp/restart': () => ({ status: 202, json: { status: 'restarting' } }),
    })
    renderApp('/admin/monitoring')
    await screen.findByText('Engines and sandboxes')
    const llama = item('llama.cpp')
    expect(within(llama).getByText('Up')).toBeInTheDocument()
    expect(within(llama).getByText(/2 answers being written, 1 waiting · Restart by ada just now/)).toBeInTheDocument()
    expect(within(llama).getAllByRole('button').map((b) => b.getAttribute('aria-label'))).toEqual(['Reload llama.cpp', 'Restart llama.cpp'])
    expect(within(item('Pictures (imagegen)')).getAllByRole('button').map((b) => b.textContent?.trim())).toEqual(['Restart'])
    expect(within(item('Python sandbox')).getByText('1 job running, 2 waiting')).toBeInTheDocument()
    expect(within(item('Python sandbox')).getAllByRole('button').map((b) => b.textContent?.trim())).toEqual(['Reset'])
    expect(within(item('Proof checker (Lean)')).getByText('Down')).toBeInTheDocument()

    await userEvent.click(within(llama).getByRole('button', { name: 'Restart llama.cpp' }))
    const dialog = await screen.findByRole('alertdialog')
    expect(within(dialog).getByText('Restart llama.cpp?')).toBeInTheDocument()
    expect(within(dialog).getByText('Its process starts again, then the models kept loaded load again. 2 answers being written are cut off; 1 waiting wait until it is back.')).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Restart' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/admin/runtimes/llamacpp/restart')).toBe(true))
  })

  it('says what a reset stops, and sends nothing when it is cancelled', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/services': () => ({ json: [] }),
      'GET /api/admin/runtimes': () => ({ json: runtimes }),
    })
    renderApp('/admin/monitoring')
    await screen.findByText('Engines and sandboxes')
    await userEvent.click(within(item('Python sandbox')).getByRole('button', { name: 'Reset Python sandbox' }))
    const dialog = await screen.findByRole('alertdialog')
    expect(within(dialog).getByText(/Stops 1 running job and gives back 2 waiting jobs, each caller told why/)).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument())
    expect(calls.some((c) => c.method === 'POST')).toBe(false)
  })

  it('shows each person only the buttons their permissions give, and a pending one has its buttons off', async () => {
    fakeApi({ ...member, permissions: ['monitoring.read', 'models.load'] }, {
      'GET /api/admin/services': () => ({ json: [] }),
      'GET /api/admin/runtimes': () => ({ json: [row({ id: 'vllm', label: 'vLLM' }), row({ state: 'restarting', since: '2026-11-01T10:00:00Z' }), runtimes[2]] }),
    })
    renderApp('/admin/monitoring')
    await screen.findByText('Engines and sandboxes')
    // models.load: Reload; no runtimes.restart: no Restart, no Reset.
    expect(within(item('vLLM')).getAllByRole('button').map((b) => b.textContent?.trim())).toEqual(['Reload'])
    expect(within(item('Python sandbox')).queryAllByRole('button')).toEqual([])
    const pending = item('llama.cpp')
    expect(within(pending).getByText(/^Restarting since/)).toBeInTheDocument()
    expect(within(pending).getByRole('button', { name: 'Reload llama.cpp' })).toBeDisabled()
  })

  it('is asked again every 3 s while one is on its way back, else every 15 s', () => {
    expect(pollEvery([row({}), row({ id: 'sandbox', state: 'down' })])).toBe(15_000)
    for (const state of ['loading', 'reloading', 'restarting', 'resetting'] as const) expect(pollEvery([row({}), row({ state })])).toBe(3_000)
    expect(pollEvery(undefined)).toBe(15_000)
  })

  it('words a reload of an engine, a restart of a media server, and an engine whose counts the page does not have', () => {
    expect(consequence(row({ busy: 1, waiting: 0 }), 'reload').description).toBe('Its models unload and load again. 1 answer being written is cut off; 0 waiting wait until it is back.')
    expect(consequence({ kind: 'media', label: 'Pictures (imagegen)' }, 'restart').description).toBe('Its server starts again with its model: a picture or video being made is lost.')
    expect(consequence({ kind: 'engine', label: 'vLLM' }, 'restart').description).toMatch(/Answers being written on it are cut off/)
  })
})

describe('the engine summary', () => {
  it('has Reload and Restart for each engine the app runs, and none for the servers it does not', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({
        json: {
          engine: {
            enabled: true, error: null, checkedAt: null, kept: [], max: 2, onRequest: true, loaded: [], loading: [], gpus: [], plan: null,
            instances: [
              { name: 'llamacpp', kind: 'llamacpp', managed: true, problem: null, on: true },
              { name: 'vllm-b', kind: 'vllm', managed: true, problem: null, on: true },
              { name: 'sglang', kind: 'sglang', managed: true, problem: null, on: false },
              { name: 'external', kind: 'external', managed: false, problem: null, on: true },
            ],
          },
          models: [],
        },
      }),
      'POST /api/admin/runtimes/vllm-b/reload': () => ({ status: 202, json: { status: 'reloading' } }),
    })
    renderApp('/admin/models')
    await screen.findByRole('button', { name: 'Reload llama.cpp' })
    expect(screen.getByRole('button', { name: 'Restart llama.cpp' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /SGLang/ })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Reload vLLM (vllm-b)' }))
    const dialog = await screen.findByRole('alertdialog')
    expect(within(dialog).getByText('Reload vLLM (vllm-b)?')).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Reload' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/admin/runtimes/vllm-b/reload')).toBe(true))
  })
})
