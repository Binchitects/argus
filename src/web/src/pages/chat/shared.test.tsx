import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import { blank } from './live'
import type { ChatConfig, Conversation, Message, SharedChat } from './types'

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: true, loaded: true, prices: { input: 0.2, cachedInput: 0.02, output: 0.8 } }],
  presets: [],
  defaultThinking: null,
  argus: false,
  tools: [],
  gitlabUrl: null,
  maxUploadBytes: 20 * 1024 * 1024,
  imageTypes: [],
}

const msg = (id: string, parentId: string | null, role: Message['role'], over: Partial<Message> = {}): Message => ({ ...blank(id, role, parentId), status: 'complete', ...over })

const messages = [
  msg('q1', null, 'user', { content: 'When is the launch?', attachments: [{ id: 'f1', fileName: 'notes.txt', size: 24, truncated: false, kind: 'text', contentType: 'text/plain' }] }),
  msg('a1', 'q1', 'assistant', { content: 'On Friday.', model: 'Main-Model' }),
]

const shared = (over: Partial<SharedChat> = {}): SharedChat => ({
  id: 's1', title: 'Launch date', owner: 'Ann Owner', mine: false, branch: false, sharedAt: '2026-10-01T00:00:00Z', updatedAt: '', currentLeafId: 'a1',
  messages, link: null, conversationId: null, ...over,
})

describe('shared chats', () => {
  it('a shared chat opens read-only, with its files, and forks into the reader’s own chats', async () => {
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/shared/s1': () => ({ json: shared() }),
      'POST /api/shared/s1/fork': () => ({ status: 201, json: { id: 'c9', title: 'Launch date (fork)' } }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c9': () => ({ json: { id: 'c9', title: 'Launch date (fork)', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null, currentLeafId: null, archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '', messages: [] } }),
    })
    const { router } = renderApp('/shared/s1')
    expect(await screen.findByRole('heading', { name: 'Launch date' })).toBeInTheDocument()
    expect(screen.getByText(/Shared by Ann Owner · the whole chat/)).toBeInTheDocument()
    expect(screen.getByText('Read-only')).toBeInTheDocument()
    expect(screen.getByText('When is the launch?')).toBeInTheDocument()
    expect(screen.getByText('On Friday.')).toBeInTheDocument()
    expect(screen.getByText('notes.txt')).toBeInTheDocument()
    // Nothing to write with, edit, answer again or approve.
    expect(screen.queryByRole('textbox', { name: 'Message' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Edit question' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Answer again' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Revoke link' })).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: 'Files (1)' }))
    expect(await screen.findByRole('complementary', { name: 'Files' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Close files' }))
    await waitFor(() => expect(screen.queryByRole('complementary', { name: 'Files' })).toBeNull())

    await userEvent.click(screen.getAllByRole('button', { name: 'Fork into my chats' })[0]!)
    await waitFor(() => expect(router.state.location.pathname).toBe('/chat/c9'))
    expect(calls.find((c) => c.path === '/api/shared/s1/fork')?.body).toEqual({})
  })

  it('a link that is revoked or not for the person does not open', async () => {
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/shared/s1': () => ({ status: 404, json: { status: 'not_found' } }),
    })
    renderApp('/shared/s1')
    expect(await screen.findByText('This link does not open')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Go to your chats' })).toHaveAttribute('href', '/chat')
  })

  it('its owner sees how many opened it and revokes it from the link', async () => {
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/shared/s1': () => ({ json: shared({ mine: true, conversationId: 'c1', link: { id: 's1', reach: 'Company', groups: [], branch: false, leafId: null, opens: 5, people: 3, createdAt: '' } }) }),
      'DELETE /api/chat/conversations/c1/share': () => ({ status: 204 }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: { ...shared(), id: 'c1', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null, archivedAt: null, forkedFrom: null, createdAt: '' } }),
    })
    const { router } = renderApp('/shared/s1')
    expect(await screen.findByText(/Opened 5 times by 3 people/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Revoke link' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.path === '/api/chat/conversations/c1/share')).toBe(true))
    await waitFor(() => expect(router.state.location.pathname).toBe('/chat/c1'))
  })

  it('a chat is shared from its menu with chosen groups, its branch only, and revoked in one click', async () => {
    let share: object | null = null
    const chat: Conversation = {
      id: 'c1', title: 'Launch date', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
      currentLeafId: 'a1', archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '', messages,
    }
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/assistants': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: chat }),
      'GET /api/chat/conversations/c1/share': () => ({ json: { share } }),
      'GET /api/sharing/groups': () => ({ json: [{ id: 'g1', name: 'Launch team', mine: true }] }),
      'PUT /api/chat/conversations/c1/share': () => {
        share = { id: 's1', reach: 'Groups', groups: [{ id: 'g1', name: 'Launch team' }], branch: true, leafId: 'a1', opens: 0, people: 0, createdAt: '' }
        return { json: share }
      },
      'DELETE /api/chat/conversations/c1/share': () => {
        share = null
        return { status: 204 }
      },
    })
    renderApp('/chat/c1')
    await userEvent.click(await screen.findByRole('button', { name: 'Chat actions' }))
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Share' }))
    const dialog = await screen.findByRole('dialog', { name: 'Share this chat' })
    await userEvent.click(within(dialog).getByRole('radio', { name: /Chosen groups/ }))
    expect(within(dialog).getByRole('button', { name: 'Make link' })).toBeDisabled()
    await userEvent.click(within(await within(dialog).findByRole('list', { name: 'Groups that may open it' })).getByRole('checkbox'))
    await userEvent.click(within(dialog).getByRole('radio', { name: /The branch on screen/ }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Make link' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ reach: 'Groups', groups: ['g1'], branch: true, messageId: 'a1' }))
    expect(await within(dialog).findByLabelText('Link')).toHaveValue(`${window.location.origin}/shared/s1`)
    expect(within(dialog).getByText('Nobody has opened it yet.')).toBeInTheDocument()

    await userEvent.click(within(dialog).getByRole('button', { name: 'Revoke link' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE')).toBe(true))
    expect(await within(dialog).findByRole('button', { name: 'Make link' })).toBeInTheDocument()
  })
})
