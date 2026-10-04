import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import type { Assistant, AssistantSummary, ChatConfig, Conversation } from './chat/types'

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: true, loaded: true, prices: { input: 0.2, cachedInput: 0.02, output: 0.8 } }],
  presets: [{ level: 'low', label: 'Quick' }],
  defaultThinking: null,
  argus: false,
  tools: [{ id: 'calculator', title: 'Calculator', description: 'Exact arithmetic.', icon: 'calculator', onByDefault: true, askFirst: false }],
  gitlabUrl: null,
  maxUploadBytes: 20 * 1024 * 1024,
  imageTypes: [],
}

const summary = (over: Partial<AssistantSummary>): AssistantSummary => ({
  id: 'a1', name: 'Release helper', description: 'Writes our release notes', icon: 'pen', color: 'green', reach: 'Groups', owner: 'Ann Owner', mine: false, canEdit: false,
  chats: 12, people: 4, myChats: 0, files: 1, updatedAt: '2026-10-01T00:00:00Z', ...over,
})

const assistant = (over: Partial<Assistant> = {}): Assistant => ({
  id: 'a1', name: 'Release helper', description: 'Writes our release notes', icon: 'pen', color: 'green', reach: 'Groups', mine: false, canEdit: false, canShare: false,
  updatedAt: '2026-10-01T00:00:00Z', createdAt: '', instructions: 'Write release notes in our style.', model: null, thinking: 'low', tools: ['calculator'],
  starters: ['Notes for this week', 'What changed in the API?'], owner: { id: 'u1', name: 'Ann Owner' }, files: [], chats: [], usage: { chats: 12, people: 4 }, sharing: null, ...over,
})

const conversation = (over: Partial<Conversation> = {}): Conversation => ({
  id: 'c1', title: 'New chat', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: null, archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '', messages: [], ...over,
})

describe('assistants', () => {
  it('the gallery lists what the person may use with its use, finds one, and starts a chat with it from a starter', async () => {
    const calls = fakeApi(member, {
      'GET /api/assistants': () => ({ json: [summary({}), summary({ id: 'a2', name: 'My notes', description: null, reach: 'Private', owner: 'Mo Member', mine: true, canEdit: true, chats: 1, people: 1, myChats: 1 })] }),
      'GET /api/assistants/a1': () => ({ json: assistant() }),
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'POST /api/chat/conversations': () => ({ status: 201, json: conversation({ assistant: { id: 'a1', name: 'Release helper', icon: 'pen', color: 'green', starters: [], noAccess: false } }) }),
      'GET /api/chat/conversations/c1': () => ({ json: conversation() }),
      'POST /api/chat/conversations/c1/messages': () => ({ events: [{ type: 'question', id: 'q1', parentId: null }, { type: 'done', id: 'q1' }] }),
    })
    const { router } = renderApp('/assistants')
    const card = await screen.findByRole('listitem', { name: 'Release helper' })
    expect(within(card).getByText('By Ann Owner')).toBeInTheDocument()
    expect(within(card).getByText('12 chats · 4 people in the last 30 days')).toBeInTheDocument()
    expect(within(card).getByText('Groups')).toBeInTheDocument()

    await userEvent.type(screen.getByLabelText('Search assistants'), 'release notes')
    expect(screen.queryByRole('listitem', { name: 'My notes' })).toBeNull()
    expect(screen.getByRole('listitem', { name: 'Release helper' })).toBeInTheDocument()
    await userEvent.clear(screen.getByLabelText('Search assistants'))
    await userEvent.click(screen.getByRole('radio', { name: 'Yours' }))
    expect(screen.getByRole('listitem', { name: 'My notes' })).toBeInTheDocument()
    expect(screen.queryByRole('listitem', { name: 'Release helper' })).toBeNull()
    await userEvent.click(screen.getByRole('radio', { name: 'All' }))

    // A new chat with it: its name and starters on the empty chat; a starter is the first question.
    await userEvent.click(screen.getByRole('button', { name: 'Start a chat with Release helper' }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/chat'))
    expect(await screen.findByRole('heading', { name: 'Release helper', level: 1 })).toBeInTheDocument()
    const starters = await screen.findByLabelText('Conversation starters')
    await userEvent.click(within(starters).getByRole('button', { name: 'Notes for this week' }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/chat/c1'))
    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/chat/conversations')?.body).toEqual({ assistantId: 'a1' })
    expect(calls.find((c) => c.path === '/api/chat/conversations/c1/messages')?.body).toMatchObject({ content: 'Notes for this week' })
  })

  it('a new assistant is made and its page opens', async () => {
    const calls = fakeApi(member, {
      'GET /api/assistants': () => ({ json: [] }),
      'POST /api/assistants': () => ({ status: 201, json: { id: 'a9', name: 'Codec rewrite' } }),
      'GET /api/assistants/a9': () => ({ json: assistant({ id: 'a9', name: 'Codec rewrite', mine: true, canEdit: true, canShare: true, reach: 'Private', starters: [], sharing: { groups: [], editorPeople: [], editorGroups: [] } }) }),
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
    })
    const { router } = renderApp('/assistants')
    expect(await screen.findByText('No assistants yet')).toBeInTheDocument()
    await userEvent.click(screen.getAllByRole('button', { name: 'New assistant' })[0]!)
    const dialog = await screen.findByRole('dialog', { name: 'New assistant' })
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Codec rewrite')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create' }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/chat/assistants/a9'))
    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/assistants')?.body).toEqual({ name: 'Codec rewrite', description: '' })
    expect(await screen.findByRole('heading', { name: 'Codec rewrite', level: 1 })).toBeInTheDocument()
  })

  it('someone who uses an assistant reads it, and cannot change or share it', async () => {
    fakeApi(member, {
      'GET /api/assistants/a1': () => ({ json: assistant() }),
      'GET /api/assistants': () => ({ json: [summary({ myChats: 1 })] }),
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
    })
    renderApp('/chat/assistants/a1')
    expect(await screen.findByRole('heading', { name: 'Release helper', level: 1 })).toBeInTheDocument()
    expect(screen.getByText(/12 chats started, 4 people in the last 30 days/)).toBeInTheDocument()
    const about = screen.getByRole('region', { name: 'About' })
    expect(within(about).getByText('Model: the default · Thinking: Quick · Tools: Calculator')).toBeInTheDocument()
    expect(within(about).getByText('Notes for this week')).toBeInTheDocument()
    expect(screen.getByText('Write release notes in our style.')).toBeInTheDocument()
    expect(screen.queryByLabelText('Assistant instructions')).toBeNull()
    expect(screen.queryByRole('region', { name: 'Settings' })).toBeNull()
    expect(screen.queryByRole('region', { name: 'Sharing' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Remove assistant' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Add files' })).toBeNull()
    // In the chat list: the assistants the person uses.
    expect(within(await screen.findByRole('list', { name: 'Assistants' })).getByRole('link', { name: /Release helper/ })).toHaveAttribute('href', '/chat/assistants/a1')
  })

  it('its owner changes its starters and shares it with a group', async () => {
    const calls = fakeApi(member, {
      'GET /api/assistants/a1': () => ({ json: assistant({ mine: true, canEdit: true, canShare: true, reach: 'Private', sharing: { groups: [], editorPeople: [], editorGroups: [] } }) }),
      'GET /api/assistants': () => ({ json: [] }),
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/sharing/groups': () => ({ json: [{ id: 'g1', name: 'Release team', mine: true }] }),
      'GET /api/sharing/people': (_b, _i, url) => ({ json: url.searchParams.get('q') === 'be' ? [{ id: 'p2', name: 'Ben Editor', userName: 'ben' }] : [] }),
      'PATCH /api/assistants/a1': () => ({ status: 204 }),
      'PUT /api/assistants/a1/sharing': () => ({ status: 204 }),
    })
    renderApp('/chat/assistants/a1')
    const settings = await screen.findByRole('region', { name: 'Settings' })
    await userEvent.click(within(settings).getByRole('button', { name: 'Remove starter 2' }))
    await userEvent.click(within(settings).getByRole('button', { name: 'Add a starter' }))
    await userEvent.type(within(settings).getByLabelText('Starter 2'), 'Draft the announcement')
    await userEvent.click(within(settings).getByRole('button', { name: 'Save settings' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PATCH')?.body).toMatchObject({ starters: ['Notes for this week', 'Draft the announcement'], tools: ['calculator'], thinking: 'low', model: '' }))

    const sharing = screen.getByRole('region', { name: 'Sharing' })
    // Only admins share with everyone.
    expect(within(sharing).getByRole('radio', { name: /Everyone/ })).toBeDisabled()
    await userEvent.click(within(sharing).getByRole('radio', { name: /Chosen groups/ }))
    expect(within(sharing).getByRole('button', { name: 'Save sharing' })).toBeDisabled()
    await userEvent.click(within(within(sharing).getByRole('list', { name: 'Groups that use it' })).getByRole('checkbox'))
    await userEvent.type(within(sharing).getByLabelText('Find people by name'), 'be')
    await userEvent.click(await within(sharing).findByRole('button', { name: /Ben Editor/ }))
    await userEvent.click(within(sharing).getByRole('button', { name: 'Save sharing' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ reach: 'Groups', groups: ['g1'], editorPeople: ['p2'], editorGroups: [] }))
  })

  it('a chat with an assistant the person lost access to says so, and goes on without it', async () => {
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/assistants': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({
        json: conversation({
          title: 'Notes', currentLeafId: 'a1m',
          assistant: { id: 'a1', name: 'Release helper', icon: 'pen', color: 'green', starters: [], noAccess: true },
          messages: [
            { id: 'q1', parentId: null, role: 'user', content: 'Notes please', reasoning: null, toolName: null, toolCallId: null, toolCalls: null, attachments: [], status: 'complete', error: null, model: null, promptTokens: null, cachedTokens: null, completionTokens: null, thinkingMs: null, durationMs: null, createdAt: '', noAccess: false },
            { id: 'a1m', parentId: 'q1', role: 'assistant', content: 'Here they are.', reasoning: null, toolName: null, toolCallId: null, toolCalls: null, attachments: [], status: 'complete', error: null, model: 'Main-Model', promptTokens: null, cachedTokens: null, completionTokens: null, thinkingMs: null, durationMs: null, createdAt: '', noAccess: false },
          ],
        }),
      }),
      'PATCH /api/chat/conversations/c1': () => ({ status: 204 }),
    })
    renderApp('/chat/c1')
    expect(await screen.findByText(/You no longer have access to the assistant/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Go on without it' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PATCH')?.body).toEqual({ assistantId: '00000000-0000-0000-0000-000000000000' }))
  })
})
