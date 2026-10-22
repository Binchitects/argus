import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import { fit, type MediaConfig, type MediaItem, type MediaJob, type Story } from './media-api'

const config: MediaConfig = { picture: 'FLUX.2-klein-4B', video: 'Wan2.2-TI2V-5B', enlarge: true, cutout: true, voice: true, music: true, going: 0, mostGoing: 3 }

const fox: MediaItem = {
  id: 'i1', kind: 'picture', action: 'make', prompt: 'A red fox in the snow', parentId: null, width: 1024, height: 768, seconds: null, model: 'FLUX.2-klein-4B',
  createdAt: '2026-10-22T08:00:00Z', attachmentId: 'a1', fileName: 'a-red-fox-in-the-snow.png', contentType: 'image/png', size: 900000, thumb: true,
}
const hat: MediaItem = { ...fox, id: 'i2', action: 'edit', prompt: 'Give it a blue hat', parentId: 'i1', attachmentId: 'a2', createdAt: '2026-10-22T08:05:00Z' }
const clip: MediaItem = { ...fox, id: 'i3', kind: 'video', action: 'move', prompt: 'The fox looks up', seconds: 3, width: null, height: null, attachmentId: 'a3', fileName: 'fox.webm', contentType: 'video/webm', thumb: false }

const job = (o: Partial<MediaJob>): MediaJob => ({
  id: 'j1', action: 'make', prompt: 'A lighthouse at dusk', sourceId: null, count: 2, createdAt: '', finishedAt: null, status: 'working', phase: 'Waiting for its turn on the GPU', error: null, items: [], ...o,
})

vi.mock('../chat/api', async (original) => ({
  ...(await original<typeof import('../chat/api')>()),
  // XMLHttpRequest (for upload progress) has no server in tests.
  uploadFile: vi.fn(async (file: File) => ({ id: `att-${file.name}`, fileName: file.name, size: file.size, truncated: false, kind: 'audio', contentType: file.type })),
}))

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

  it('speaks words in a voice from a sample only with the word that it may, makes music, and cuts a picture out', async () => {
    const calls = fakeApi(member, {
      'GET /api/media': () => ({ json: config }),
      'GET /api/media/items': () => ({ json: { items: [fox], more: false } }),
      'GET /api/media/jobs': () => ({ json: [] }),
      'POST /api/media/jobs': (body) => ({ status: 202, json: job({ action: (body as MediaJob).action }) }),
    })
    renderApp('/media')
    await userEvent.click(await screen.findByRole('radio', { name: 'Speech' }))
    await userEvent.type(screen.getByLabelText('What to say'), 'Welcome to the lunch club.')
    await userEvent.click(screen.getByRole('radio', { name: 'A voice from a sample' }))
    const speak = screen.getByRole('button', { name: /Speak it/ })
    expect(speak).toBeDisabled()
    await userEvent.upload(screen.getByLabelText('A sound file of the voice'), new File(['ID3'], 'me.mp3', { type: 'audio/mpeg' }))
    expect(await screen.findByText('me.mp3')).toBeInTheDocument()
    expect(speak).toBeDisabled()
    await userEvent.click(screen.getByLabelText(/This voice is mine, or its owner lets me use it/))
    await userEvent.click(speak)
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ action: 'speak', prompt: 'Welcome to the lunch club.', exaggeration: 0.5, voiceId: 'att-me.mp3', consent: true }),
    )

    await userEvent.click(screen.getByRole('radio', { name: 'Music' }))
    await userEvent.type(screen.getByLabelText('Describe the music'), 'lo-fi piano, rain')
    await userEvent.click(screen.getByRole('radio', { name: '1 min' }))
    await userEvent.click(screen.getByRole('button', { name: /Make the music/ }))
    await waitFor(() => expect(calls.filter((c) => c.method === 'POST').at(-1)?.body).toEqual({ action: 'music', prompt: 'lo-fi piano, rain', seconds: 60 }))

    await userEvent.click(screen.getByRole('button', { name: 'Made from words: A red fox in the snow' }))
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('tab', { name: 'Cut out' }))
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: /Take the background away/ }))
    await waitFor(() => expect(calls.filter((c) => c.method === 'POST').at(-1)?.body).toEqual({ action: 'cutout', sourceId: 'i1', width: 1024, height: 768 }))
  })

  it('a storyboard is written from a few words, changed shot by shot, and made into a video', async () => {
    let story: Story = {
      id: 's1', title: 'The fox in winter', brief: 'A fox in winter', voiceId: null, music: 'calm piano', videoItemId: null, video: null, status: 'draft', phase: null, error: null,
      createdAt: '', updatedAt: '1',
      shots: [
        { visual: 'A fox in the snow', narration: 'Snow fell.', seconds: 3 },
        { visual: 'The fox runs', narration: 'It ran home.', seconds: 4 },
      ],
    }
    const calls = fakeApi(member, {
      'GET /api/media': () => ({ json: config }),
      'GET /api/media/items': () => ({ json: { items: [], more: false } }),
      'GET /api/media/jobs': () => ({ json: [] }),
      'GET /api/media/stories': () => ({ json: [story] }),
      'POST /api/media/stories': () => ({ status: 201, json: story }),
      'GET /api/media/stories/s1': () => ({ json: story }),
      'PUT /api/media/stories/s1': (body) => {
        story = { ...story, ...(body as Partial<Story>), updatedAt: '2' }
        return { json: story }
      },
      'POST /api/media/stories/s1/make': () => {
        story = { ...story, status: 'making', phase: 'Drawing shot 1 of 2', updatedAt: '3' }
        return { status: 202, json: story }
      },
    })
    renderApp('/media')
    await userEvent.click(await screen.findByRole('radio', { name: 'Video' }))
    await userEvent.type(screen.getByLabelText('What the video is about'), 'A fox in winter')
    await userEvent.click(screen.getByRole('radio', { name: '3 shots' }))
    await userEvent.click(screen.getByRole('button', { name: /Write the storyboard/ }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/media/stories')?.body).toEqual({ brief: 'A fox in winter', shots: 3 }))
    const dialog = await screen.findByRole('dialog')
    const shots = within(dialog).getByRole('list', { name: 'Shots' })
    expect(within(shots).getAllByRole('listitem')).toHaveLength(3)
    const narration = within(shots).getAllByLabelText('What the narrator says')[1]
    await userEvent.clear(narration)
    await userEvent.type(narration, 'It ran all the way home.')
    await userEvent.click(within(dialog).getByRole('button', { name: /^Make the video$/ }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/media/stories/s1/make')).toBe(true))
    expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({
      title: 'The fox in winter', voiceId: null, consent: false, music: 'calm piano',
      shots: [{ visual: 'A fox in the snow', narration: 'Snow fell.', seconds: 3 }, { visual: 'The fox runs', narration: 'It ran all the way home.', seconds: 4 }],
    })
    expect(await within(dialog).findByText(/Drawing shot 1 of 2/)).toBeInTheDocument()
    expect(within(dialog).getAllByLabelText('What the narrator says')[0]).toBeDisabled()
  })
})
