import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import { fit, type MediaConfig, type MediaItem, type MediaJob } from './media-api'

const config: MediaConfig = { picture: 'FLUX.2-klein-4B', video: 'Wan2.2-TI2V-5B', enlarge: true, going: 0, mostGoing: 3 }

const fox: MediaItem = {
  id: 'i1', kind: 'picture', action: 'make', prompt: 'A red fox in the snow', parentId: null, width: 1024, height: 768, seconds: null, model: 'FLUX.2-klein-4B',
  createdAt: '2026-10-22T08:00:00Z', attachmentId: 'a1', fileName: 'a-red-fox-in-the-snow.png', contentType: 'image/png', size: 900000, thumb: true,
}
const hat: MediaItem = { ...fox, id: 'i2', action: 'edit', prompt: 'Give it a blue hat', parentId: 'i1', attachmentId: 'a2', createdAt: '2026-10-22T08:05:00Z' }
const clip: MediaItem = { ...fox, id: 'i3', kind: 'video', action: 'move', prompt: 'The fox looks up', seconds: 3, width: null, height: null, attachmentId: 'a3', fileName: 'fox.webm', contentType: 'video/webm', thumb: false }

const job = (o: Partial<MediaJob>): MediaJob => ({
  id: 'j1', action: 'make', prompt: 'A lighthouse at dusk', sourceId: null, count: 2, createdAt: '', finishedAt: null, status: 'working', phase: 'Waiting for its turn on the GPU', error: null, items: [], ...o,
})

afterEach(() => vi.restoreAllMocks())

describe('media studio', () => {
  it('makes pictures from words, shows the work going where it will be, and what failed', async () => {
    let jobs: MediaJob[] = [job({ id: 'old', status: 'failed', error: 'You have had 50 pictures drawn today.' })]
    const calls = fakeApi(member, {
      'GET /api/media': () => ({ json: config }),
      'GET /api/media/items': () => ({ json: { items: [hat, fox, clip], more: false } }),
      'GET /api/media/jobs': () => ({ json: jobs }),
      'POST /api/media/jobs': () => {
        jobs = [job({}), ...jobs]
        return { status: 202, json: jobs[0] }
      },
    })
    renderApp('/media')
    const gallery = await screen.findByRole('list', { name: 'Your pictures and clips' })
    expect(within(gallery).getAllByRole('button')).toHaveLength(3)
    expect(within(gallery).getByRole('button', { name: 'Set moving: The fox looks up' })).toHaveTextContent('3 s')
    expect(screen.getByText('You have had 50 pictures drawn today.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Dismiss' }))
    expect(screen.queryByText('You have had 50 pictures drawn today.')).toBeNull()

    await userEvent.type(screen.getByLabelText('Describe a picture'), 'A lighthouse at dusk')
    await userEvent.click(screen.getByRole('radio', { name: 'Wide' }))
    await userEvent.click(screen.getByRole('radio', { name: 'Two' }))
    await userEvent.click(screen.getByRole('button', { name: /Make it/ }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/media/jobs')?.body).toEqual({ action: 'make', prompt: 'A lighthouse at dusk', count: 2, width: 1024, height: 576 }))
    // Two places in the gallery, saying what it does.
    expect(await within(gallery).findAllByText('Waiting for its turn on the GPU…')).toHaveLength(2)

    await userEvent.click(screen.getByRole('radio', { name: 'Clips' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/media/items?kind=video')).toBe(true))
  })

  it('a picture opens large, with where it came from, and makes variations, an enlargement or a clip from it', async () => {
    const calls = fakeApi(member, {
      'GET /api/media': () => ({ json: config }),
      'GET /api/media/items': () => ({ json: { items: [hat, fox], more: false } }),
      'GET /api/media/jobs': () => ({ json: [] }),
      'POST /api/media/jobs': (body) => ({ status: 202, json: job({ action: (body as MediaJob).action }) }),
      'DELETE /api/media/items/i2': () => ({ status: 204 }),
    })
    renderApp('/media')
    await userEvent.click(await screen.findByRole('button', { name: 'Changed: Give it a blue hat' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByRole('heading', { name: 'Give it a blue hat' })).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Made from: A red fox in the snow' })).toBeInTheDocument()

    // Changing it needs words.
    expect(within(dialog).getByRole('button', { name: /Change it/ })).toBeDisabled()
    await userEvent.click(within(dialog).getByRole('tab', { name: 'Variations' }))
    await userEvent.click(within(dialog).getByRole('radio', { name: 'A lot' }))
    await userEvent.click(within(dialog).getByRole('button', { name: /Make variations/ }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ action: 'vary', sourceId: 'i2', width: 1024, height: 768, count: 2, strength: 0.75 }))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())

    await userEvent.click(screen.getByRole('button', { name: 'Changed: Give it a blue hat' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('tab', { name: 'Enlarge' }))
    expect(screen.getByRole('dialog')).toHaveTextContent('1024 × 768 becomes 4096 × 3072')
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('tab', { name: 'Move' }))
    await userEvent.type(within(screen.getByRole('dialog')).getByLabelText('What happens'), 'Snow falls')
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: /Make the clip/ }))
    await waitFor(() => expect(calls.filter((c) => c.method === 'POST').at(-1)?.body).toEqual({ action: 'move', sourceId: 'i2', width: 1024, height: 768, prompt: 'Snow falls', seconds: 3 }))

    await userEvent.click(screen.getByRole('button', { name: 'Changed: Give it a blue hat' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: /Delete/ }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Delete' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.path === '/api/media/items/i2')).toBe(true))
  })

  it('without a video model there is no Move, and the sizes it works at are the server’s', async () => {
    fakeApi(member, {
      'GET /api/media': () => ({ json: { ...config, video: null, enlarge: false } }),
      'GET /api/media/items': () => ({ json: { items: [fox], more: false } }),
      'GET /api/media/jobs': () => ({ json: [] }),
    })
    renderApp('/media')
    await userEvent.click(await screen.findByRole('button', { name: 'Made from words: A red fox in the snow' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).queryByRole('tab', { name: 'Move' })).toBeNull()
    expect(within(dialog).queryByRole('tab', { name: 'Enlarge' })).toBeNull()
    expect(fit(1536, 1024)).toEqual({ width: 1024, height: 672 })
    expect(fit(500, 300)).toEqual({ width: 496, height: 288 })
  })
})
