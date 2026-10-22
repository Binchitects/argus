import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import { notSvg, type MediaConfig, type MediaItem, type MediaJob, type VectorConfig, type VectorDrawing } from './media-api'

const config: MediaConfig = { picture: 'FLUX.2-klein-4B', video: null, enlarge: false, cutout: false, voice: false, music: false, going: 0, mostGoing: 3 }

const vectors: VectorConfig = {
  styles: [{ name: 'flat', about: '' }, { name: 'line', about: '' }, { name: 'isometric', about: '' }],
  traces: [{ name: 'illustration', about: '' }, { name: 'logo', about: '' }],
  kinds: [{ kind: 'fade-in', about: '' }, { kind: 'float', about: '' }, { kind: 'spin', about: '' }, { kind: 'pop', about: '' }],
  formats: ['png', 'gif', 'mp4', 'webm'],
  moves: true,
  tracesPictures: true,
  drawsPictures: true,
}

const boat: MediaItem = {
  id: 'v1', kind: 'vector', action: 'draw', prompt: 'A boat on the sea at sunset', parentId: null, width: 640, height: 400, seconds: 2.5, model: 'Qwen3-4B',
  createdAt: '2026-10-22T08:00:00Z', attachmentId: 'a1', fileName: 'a-boat-on-the-sea.svg', contentType: 'image/svg+xml', size: 4100, thumb: true,
}
const gif: MediaItem = { ...boat, id: 'g1', kind: 'picture', action: 'export', parentId: 'v1', attachmentId: 'a2', fileName: 'a-boat-on-the-sea.gif', contentType: 'image/gif', size: 210_000 }

const still = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 640 400"><rect id="sky" width="640" height="400"/><g id="boat"><path id="hull" d="M0 0h10v10z"/></g><circle id="sun" r="40"/></svg>'
const drawing: VectorDrawing = {
  id: 'v1', prompt: boat.prompt, width: 640, height: 400, still, motions: [{ target: 'sun', kind: 'fade-in', duration: 1 }, { target: 'boat', kind: 'float' }],
  markup: still, seconds: 2.5, loops: true, source: 'drawn', style: 'flat', version: 3, updatedAt: '2026-10-22T08:10:00Z',
}

const job = (o: Partial<MediaJob>): MediaJob => ({
  id: 'j1', action: 'draw', prompt: null, sourceId: null, count: 1, createdAt: '2026-10-22T09:00:00Z', finishedAt: null, status: 'working', phase: null, error: null, items: [], ...o,
})

afterEach(() => vi.restoreAllMocks())

describe('vector drawings', () => {
  it('are made from words as an illustration (traced) or drawn as SVG in a style, or uploaded and made safe', async () => {
    const calls = fakeApi(member, {
      'GET /api/media': () => ({ json: config }),
      'GET /api/media/vectors/config': () => ({ json: vectors }),
      'GET /api/media/items': () => ({ json: { items: [], more: false } }),
      'GET /api/media/jobs': () => ({ json: [] }),
      'POST /api/media/jobs': (body) => ({ status: 202, json: job({ action: (body as MediaJob).action }) }),
      'POST /api/media/vectors': () => ({ json: { id: 'v9', changed: ['a script taken out'] } }),
    })
    renderApp('/media')
    // What to make (the gallery has a Vector of its own, to show only drawings).
    await userEvent.click(await within(await screen.findByRole('radiogroup', { name: 'Make' })).findByRole('radio', { name: 'Vector' }))
    // An illustration first: the picture model draws it, and it is traced.
    await userEvent.type(screen.getByLabelText('Describe it, and how it moves'), 'A fox logo')
    await userEvent.click(screen.getByRole('radio', { name: 'Logo' }))
    await userEvent.click(screen.getByRole('button', { name: /Make it/ }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/media/jobs')?.body).toEqual({ action: 'trace', prompt: 'A fox logo', trace: 'logo', animate: 'always' }))

    await userEvent.click(screen.getByRole('radio', { name: 'Drawn as SVG' }))
    await userEvent.type(screen.getByLabelText('Describe it, and how it moves'), 'A rocket icon')
    await userEvent.click(screen.getByRole('radio', { name: 'Wide' }))
    await userEvent.click(screen.getByRole('button', { name: /Make it/ }))
    await waitFor(() => expect(calls.filter((c) => c.method === 'POST' && c.path === '/api/media/jobs')[1]?.body).toEqual({ action: 'draw', prompt: 'A rocket icon', style: 'flat', width: 640, height: 400, animate: 'always' }))

    const file = new File(['<svg xmlns="http://www.w3.org/2000/svg"><script>x</script></svg>'], 'mine.svg', { type: 'image/svg+xml' })
    await userEvent.upload(screen.getByLabelText('An SVG file'), file)
    await waitFor(() => expect(calls.find((c) => c.path === '/api/media/vectors')?.body).toEqual({ svg: '<svg xmlns="http://www.w3.org/2000/svg"><script>x</script></svg>', name: 'mine.svg' }))
    expect(await screen.findByText(/made safe: a script taken out/)).toBeInTheDocument()
  })

  it('a drawing opens on its stage: its motions changed by hand and by words, its work followed, exported and its versions put back', async () => {
    let jobs: MediaJob[] = []
    const calls = fakeApi(member, {
      'GET /api/media': () => ({ json: config }),
      'GET /api/media/vectors/config': () => ({ json: vectors }),
      'GET /api/media/items': () => ({ json: { items: [gif, boat], more: false } }),
      'GET /api/media/jobs': () => ({ json: jobs }),
      'POST /api/media/jobs': (body) => {
        jobs = [job({ action: (body as MediaJob).action, sourceId: 'v1', phase: 'Planning its motions' }), ...jobs]
        return { status: 202, json: jobs[0] }
      },
      'GET /api/media/vectors/v1': () => ({ json: drawing }),
      'PUT /api/media/vectors/v1': (body) => ({ json: { drawing: { ...drawing, ...(body as object), version: 4 }, problems: ['#moon is not in the drawing'] } }),
      'GET /api/media/vectors/v1/versions': () => ({ json: [{ version: 3, change: 'animated: the sun rises', createdAt: '' }, { version: 2, change: 'drawn', createdAt: '' }] }),
      'POST /api/media/vectors/v1/versions/2/restore': () => ({ status: 204 }),
    })
    renderApp('/media')
    const gallery = await screen.findByRole('list', { name: 'Your pictures, drawings and clips' })
    expect(within(gallery).getByRole('button', { name: 'Drawn as SVG: A boat on the sea at sunset' })).toHaveTextContent('3 s')
    await userEvent.click(within(gallery).getByRole('button', { name: 'Drawn as SVG: A boat on the sea at sunset' }))
    const dialog = await screen.findByRole('dialog')
    expect(await within(dialog).findByTitle('The drawing')).toBeInTheDocument()
    expect(within(dialog).getByText('Plays 2.5 s, then loops.')).toBeInTheDocument()
    expect(within(dialog).getByRole('slider', { name: 'Time' })).toBeInTheDocument()

    // Its motions, changed by hand: one taken out, then played (what could not be done is said).
    const list = within(dialog).getByRole('list', { name: 'Its motions' })
    expect(within(list).getAllByRole('listitem')).toHaveLength(2)
    await userEvent.click(within(list).getByRole('button', { name: 'Take the float of boat out' }))
    await userEvent.click(within(dialog).getByRole('button', { name: /Play these/ }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ motions: [{ target: 'sun', kind: 'fade-in', duration: 1 }] }))
    expect(await within(dialog).findByText(/#moon is not in the drawing/)).toBeInTheDocument()

    // By words: the work shows in the drawing while it goes.
    await userEvent.type(within(dialog).getByLabelText('Animate by words'), 'The sun rises')
    await userEvent.click(within(dialog).getByRole('button', { name: /Animate it/ }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/media/jobs')?.body).toEqual({ action: 'animate', prompt: 'The sun rises', sourceId: 'v1' }))
    expect(await within(dialog).findByText('Planning its motions…')).toBeInTheDocument()

    // Exported: each format at its own sizes; what was made from it is there to download.
    await userEvent.click(within(dialog).getByRole('tab', { name: 'Export' }))
    expect(within(dialog).getByRole('link', { name: 'Download a-boat-on-the-sea.gif' })).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('radio', { name: 'MP4' }))
    await userEvent.click(within(dialog).getByRole('radio', { name: '1920 px' }))
    await userEvent.click(within(dialog).getByRole('button', { name: /Make the MP4/ }))
    await waitFor(() =>
      expect(calls.filter((c) => c.method === 'POST' && c.path === '/api/media/jobs')[1]?.body).toEqual({ action: 'export', format: 'mp4', side: 1920, background: '#ffffff', sourceId: 'v1' }),
    )

    await userEvent.click(within(dialog).getByRole('tab', { name: 'Versions' }))
    const versions = await within(dialog).findByRole('list', { name: 'Versions' })
    expect(within(versions).getAllByRole('listitem')).toHaveLength(2)
    await userEvent.click(await within(versions).findByRole('button', { name: 'Put back' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/media/vectors/v1/versions/2/restore')).toBe(true))
  })

  it('code typed by hand is an SVG only when it reads as XML and starts with <svg>', () => {
    expect(notSvg(still)).toBeNull()
    expect(notSvg('<svg xmlns="http://www.w3.org/2000/svg"><rect></svg>')).toBeTruthy()
    expect(notSvg('<g xmlns="http://www.w3.org/2000/svg"/>')).toBe('It does not start with <svg>.')
  })
})
