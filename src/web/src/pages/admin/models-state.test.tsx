import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

// "The unloading model text is wrong": a model on its way out reads Unloading…, with why and since when; one on its way
// in reads Loading… with why; only the button pressed shows it works; an admin's Load on an engine whose every place is
// held offers to load it instead of one that is not kept.

const everyone = { audience: 'Everyone', groups: [] }
const ago = (s: number) => new Date(Date.now() - s * 1000).toISOString()

type Row = { status: string; why?: string | null; since?: string | null; instead?: boolean; evicted?: number }

function view(tiny: Row, image: Row = { status: 'unloaded' }) {
  return {
    engine: {
      enabled: true, error: null, checkedAt: '2026-10-10T10:00:00Z', kept: ['Big-Model'], pinned: ['Big-Model'], max: 2, hours: null, onRequest: true,
      loaded: ['Big-Model'], loading: [], unloading: [], gpus: [], plan: null,
    },
    small: null,
    models: [
      { name: 'Big-Model', source: 'local', mode: 'chat', status: 'loaded', file: 'big/Big.gguf', vision: false, access: everyone, kept: true, keptNow: true },
      { name: 'tiny-b', source: 'local', mode: 'chat', file: 'tiny/Tiny.gguf', vision: false, access: everyone, kept: false, keptNow: false, ...tiny },
      { name: 'FLUX.2-klein-4B', source: 'media', mode: 'image_generation', server: 'imagegen', enabled: true, kept: false, keptNow: false, vision: false, access: everyone, ...image },
    ],
  }
}

const card = (name: string) => screen.getByRole('heading', { name: new RegExp(name.replace(/\./g, '\\.')) }).closest('section')!

describe('admin models: loading and unloading', () => {
  it('reads Unloading… with who and since when, then Not loaded, and never Loading…', async () => {
    let tiny: Row = { status: 'loaded' }
    let image: Row = { status: 'loaded' }
    fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: view(tiny, image) }),
      'POST /api/admin/models/tiny-b/unload': () => {
        tiny = { status: 'unloading', why: 'unloaded by ada', since: ago(3) }
        return { status: 202 }
      },
      'POST /api/admin/models/FLUX.2-klein-4B/unload': () => {
        image = { status: 'unloading' }
        return { status: 202 }
      },
    })
    const { client } = renderApp('/admin/models')
    await screen.findByRole('heading', { name: /tiny-b/ })

    await userEvent.click(within(card('tiny-b')).getByRole('button', { name: /Unload/ }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Unload' }))
    expect(await screen.findByText('Unloading tiny-b')).toBeInTheDocument()
    await waitFor(() => expect(within(card('tiny-b')).getByText('Unloading…')).toBeInTheDocument())
    expect(within(card('tiny-b')).getByText('unloaded by ada, for 3 s')).toBeInTheDocument()
    expect(within(card('tiny-b')).queryByText('Loading…')).not.toBeInTheDocument()
    // Neither button while it stops: Load waits for it to be gone.
    expect(within(card('tiny-b')).getByRole('button', { name: /^Load$/ })).toBeDisabled()

    tiny = { status: 'unloaded' }
    await client.invalidateQueries({ queryKey: ['admin', 'models'] })
    await waitFor(() => expect(within(card('tiny-b')).getByText('Not loaded')).toBeInTheDocument())

    // A picture model: Unloading… while its server stops, then Not loaded.
    await userEvent.click(within(card('FLUX.2-klein-4B')).getByRole('button', { name: /Unload/ }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Unload' }))
    await waitFor(() => expect(within(card('FLUX.2-klein-4B')).getByText('Unloading…')).toBeInTheDocument())
    image = { status: 'unloaded' }
    await client.invalidateQueries({ queryKey: ['admin', 'models'] })
    await waitFor(() => expect(within(card('FLUX.2-klein-4B')).getByText('Not loaded')).toBeInTheDocument())
  })

  it('says why a model loads: a request asked for it after an Unload', async () => {
    fakeApi(admin, { 'GET /api/admin/models': () => ({ json: view({ status: 'loading', why: 'for a chat or an API request', since: ago(5) }) }) })
    renderApp('/admin/models')
    await screen.findByRole('heading', { name: /tiny-b/ })
    expect(within(card('tiny-b')).getByText('Loading…')).toBeInTheDocument()
    expect(within(card('tiny-b')).getByText('for a chat or an API request, for 5 s')).toBeInTheDocument()
  })

  it('a poll while the Unload is on its way leaves the Load button alone', async () => {
    let tiny: Row = { status: 'loaded' }
    let release!: () => void
    const answered = new Promise<void>((r) => (release = r))
    fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: view(tiny) }),
      'POST /api/admin/models/tiny-b/unload': () => {
        tiny = { status: 'unloaded' }
        return { status: 202 }
      },
    })
    const fake = vi.mocked(globalThis.fetch)
    const impl = fake.getMockImplementation()!
    fake.mockImplementation(async (input, init) => {
      const res = await impl(input, init)
      if (String(input).endsWith('/tiny-b/unload')) await answered
      return res
    })
    const { client } = renderApp('/admin/models')
    await screen.findByRole('heading', { name: /tiny-b/ })
    await userEvent.click(within(card('tiny-b')).getByRole('button', { name: /Unload/ }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Unload' }))
    await client.invalidateQueries({ queryKey: ['admin', 'models'] })
    await waitFor(() => expect(within(card('tiny-b')).getByText('Not loaded')).toBeInTheDocument())
    expect(within(card('tiny-b')).getByRole('button', { name: /^Load$/ })).not.toHaveAttribute('aria-busy', 'true')
    release()
  })

  it('offers to load a model instead of one holding a place, when every place is held', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/models': () => ({ json: view({ status: 'unloaded' }) }),
      'POST /api/admin/models/tiny-b/load': (_, __, url) =>
        url?.searchParams.get('instead') === 'true'
          ? { status: 202 }
          : {
              status: 409,
              json: { status: 'held', error: 'Every place in the engine is held by a model that never makes room (Big-Model, Chat-Default).', holding: ['Chat-Default'] },
            },
    })
    renderApp('/admin/models')
    await screen.findByRole('heading', { name: /tiny-b/ })
    await userEvent.click(within(card('tiny-b')).getByRole('button', { name: /^Load$/ }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Load' }))
    const instead = await screen.findByRole('alertdialog', { name: 'Load tiny-b instead of Chat-Default?' })
    await userEvent.click(within(instead).getByRole('button', { name: 'Load instead of Chat-Default' }))
    await waitFor(() => expect(calls.filter((c) => c.method === 'POST').map((c) => c.path)).toContain('/api/admin/models/tiny-b/load?instead=true'))
    expect(await screen.findByText('Loading tiny-b')).toBeInTheDocument()
  })

  it('warns when the engine keeps unloading a model by its own choice', async () => {
    fakeApi(admin, { 'GET /api/admin/models': () => ({ json: view({ status: 'unloaded', evicted: 3 }) }) })
    renderApp('/admin/models')
    await screen.findByRole('heading', { name: /tiny-b/ })
    expect(within(card('tiny-b')).getByText('The engine keeps unloading it')).toBeInTheDocument()
  })
})
