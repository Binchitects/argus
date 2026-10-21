import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import { blank } from '../chat/live'
import type { ChatConfig, Conversation } from '../chat/types'
import type { AppBrief } from './apps-api'

const brief = (over: Partial<AppBrief>): AppBrief => ({
  id: 'p1', name: 'Lunch poll', description: 'Where the team eats.', kind: 'html', version: 2, reach: 'company', groupIds: [], remixOfId: null, conversationId: null,
  opens: 3, createdAt: '2026-10-21T10:00:00Z', updatedAt: '2026-10-21T10:00:00Z', owner: 'Ana', mine: false, ...over,
})

const page = '<!doctype html><html><body><h1>Votes</h1></body></html>'

afterEach(() => vi.restoreAllMocks())

describe('apps', () => {
  it('the gallery lists the apps one may open, and Mine only one’s own', async () => {
    fakeApi(member, {
      'GET /api/apps': () => ({ json: [brief({}), brief({ id: 'p2', name: 'My diary', description: '', kind: 'react', reach: 'private', owner: 'Mo Member', mine: true, opens: 0, version: 1 })] }),
    })
    renderApp('/apps')
    const list = await screen.findByRole('list', { name: 'Apps' })
    expect(within(list).getAllByRole('listitem')).toHaveLength(2)
    expect(within(list).getByRole('link', { name: /Lunch poll/ })).toHaveAttribute('href', '/apps/p1')
    expect(within(list).getByRole('link', { name: /Lunch poll/ })).toHaveTextContent('By Ana· version 2· opened 3 times')
    expect(within(list).getByRole('link', { name: /My diary/ })).toHaveTextContent('A component.')
    expect(within(list).getByRole('link', { name: /My diary/ })).toHaveTextContent('Only me')

    await userEvent.click(screen.getByRole('radio', { name: 'Mine' }))
    expect(within(screen.getByRole('list', { name: 'Apps' })).getAllByRole('listitem')).toHaveLength(1)
    expect(screen.queryByRole('link', { name: /Lunch poll/ })).not.toBeInTheDocument()
  })

  it('an app runs at its link, its arena calls answered by the server as the person using it', async () => {
    let opened = ''
    const calls = fakeApi(member, {
      'GET /api/apps/p1': (_b, _i, url) => {
        opened = url.search
        return { json: { app: brief({}), code: page, viewer: 'Mo Member' } }
      },
      'POST /api/apps/p1/ask': () => ({ json: { text: 'Pizza', model: 'Main-Model' } }),
      'PUT /api/apps/p1/data/votes': () => ({ status: 204 }),
    })
    renderApp('/apps/p1')
    expect(await screen.findByRole('heading', { name: 'Lunch poll' })).toBeInTheDocument()
    expect(screen.getByText(/By Ana · version 2 · Where the team eats./)).toBeInTheDocument()
    // Not its owner: Remix, but no Change, Versions or Delete.
    expect(screen.getByRole('button', { name: /Remix/ })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Change/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Delete Lunch poll' })).not.toBeInTheDocument()
    // Opening it counts, for apps of someone else's.
    expect(opened).toBe('?open=true')

    const frame = screen.getByTitle('Preview of Lunch poll') as HTMLIFrameElement
    expect(frame).toHaveAttribute('sandbox', 'allow-scripts allow-forms allow-modals')
    const runner = frame.contentWindow!
    const replies = vi.spyOn(runner, 'postMessage')
    const call = (id: number, method: string, args: Record<string, unknown>) => window.dispatchEvent(new MessageEvent('message', { source: runner, data: { type: 'arena', id, method, args } }))

    call(1, 'ask', { prompt: 'Where shall we eat?', system: 'One word.' })
    await waitFor(() => expect(replies).toHaveBeenCalledWith({ type: 'arena-reply', id: 1, ok: true, value: 'Pizza' }, '*'))
    expect(calls.find((c) => c.path === '/api/apps/p1/ask')?.body).toEqual({ prompt: 'Where shall we eat?', system: 'One word.' })

    call(2, 'store.set', { key: 'votes', value: { pizza: 1 } })
    await waitFor(() => expect(replies).toHaveBeenCalledWith({ type: 'arena-reply', id: 2, ok: true, value: null }, '*'))
    expect(calls.find((c) => c.path === '/api/apps/p1/data/votes')?.body).toEqual({ value: { pizza: 1 } })

    // A message from anywhere but its frame is not answered; an unknown call is an error.
    window.dispatchEvent(new MessageEvent('message', { source: window, data: { type: 'arena', id: 3, method: 'ask', args: { prompt: 'x' } } }))
    call(4, 'launch', {})
    await waitFor(() => expect(replies).toHaveBeenCalledWith({ type: 'arena-reply', id: 4, ok: false, error: 'There is no arena.launch.' }, '*'))
    expect(replies.mock.calls.some(([m]) => (m as { id?: number }).id === 3)).toBe(false)
  })

  it('its owner changes it and puts an old version back', async () => {
    let version = 2
    const calls = fakeApi(member, {
      'GET /api/apps/p1': () => ({ json: { app: brief({ mine: true, owner: 'Mo Member', version }), code: page, viewer: 'Mo Member' } }),
      'GET /api/apps/p1/versions': () => ({ json: [{ version: 2, note: 'Better words', createdAt: '2026-10-21T11:00:00Z', chars: 60 }, { version: 1, note: 'Published', createdAt: '2026-10-21T10:00:00Z', chars: 55 }] }),
      'POST /api/apps/p1/versions/1/restore': () => {
        version = 3
        return { json: brief({ mine: true, version: 3 }) }
      },
      'PUT /api/apps/p1': () => ({ json: brief({ mine: true, name: 'Team lunch' }) }),
    })
    renderApp('/apps/p1')
    await userEvent.click(await screen.findByRole('button', { name: /Versions/ }))
    const versions = within(await screen.findByRole('dialog')).getByRole('list', { name: 'Versions' })
    expect(within(versions).getAllByRole('listitem')[0]).toHaveTextContent('Now')
    await userEvent.click(within(versions).getByRole('button', { name: 'Put back' }))
    await waitFor(() => expect(screen.getByText(/Yours · Everyone · version 3/)).toBeInTheDocument())

    await userEvent.click(screen.getByRole('button', { name: /Change/ }))
    const dialog = await screen.findByRole('dialog')
    await userEvent.clear(within(dialog).getByLabelText('Name'))
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Team lunch')
    await userEvent.click(within(dialog).getByRole('radio', { name: 'Only me' }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ name: 'Team lunch', description: 'Where the team eats.', reach: 'private' }))
  })

  it('a page the chat wrote is published as an app from its preview, and its link shown', async () => {
    const config: ChatConfig = {
      model: 'Main-Model',
      models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: false, loaded: true, prices: { input: 0.2, cachedInput: 0.02, output: 0.8 } }],
      presets: [{ level: 'off', label: 'No thinking' }],
      defaultThinking: 'off',
      argus: false,
      tools: [],
      gitlabUrl: null,
      maxUploadBytes: 20 * 1024 * 1024,
      imageTypes: ['image/png'],
    }
    const messages = [blank('q1', 'user', null, 'A lunch poll'), { ...blank('a1', 'assistant', 'q1', 'Here:\n\n```html\n' + page + '\n```'), model: 'Main-Model' }]
    const conversation: Conversation = {
      id: 'c1', title: 'Lunch', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
      currentLeafId: 'a1', archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '', messages,
    }
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [conversation] }),
      'GET /api/chat/conversations/c1': () => ({ json: conversation }),
      'GET /api/sharing/groups': () => ({ json: [{ id: 'g1', name: 'Team' }] }),
      'POST /api/apps': () => ({ json: brief({ id: 'n1', name: 'Lunch poll', mine: true, reach: 'groups', groupIds: ['g1'] }) }),
    })
    renderApp('/chat/c1')
    await userEvent.click(within(await screen.findByRole('figure', { name: 'Code: html' })).getByRole('button', { name: 'Preview this page' }))
    const panel = await screen.findByRole('complementary', { name: 'Files' })
    await userEvent.click(within(panel).getByRole('button', { name: 'Publish as app' }))
    const dialog = await screen.findByRole('dialog', { name: 'Publish as app' })
    await userEvent.clear(within(dialog).getByLabelText('Name'))
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Lunch poll')
    await userEvent.click(within(dialog).getByRole('radio', { name: 'Some groups' }))
    // Some groups, but none chosen yet: nothing to publish to.
    expect(within(dialog).getByRole('button', { name: 'Publish' })).toBeDisabled()
    await userEvent.click(await within(dialog).findByRole('checkbox', { name: 'Team' }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Publish' }))

    expect(await screen.findByRole('heading', { name: 'Lunch poll is published' })).toBeInTheDocument()
    expect(screen.getByLabelText("The app's link")).toHaveValue(`${window.location.origin}/apps/n1`)
    expect(calls.find((c) => c.path === '/api/apps' && c.method === 'POST')?.body).toEqual({ name: 'Lunch poll', description: '', kind: 'html', code: page, reach: 'groups', groupIds: ['g1'], conversationId: 'c1' })
  })
})
