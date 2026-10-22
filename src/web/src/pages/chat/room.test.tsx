import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import { blank } from './live'
import type { ChatConfig, Conversation, Message, RoomBrief } from './types'

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

const brief = (over: Partial<RoomBrief> = {}): RoomBrief => ({
  role: 'member',
  membersInvite: false,
  answeringFor: null,
  members: [
    { id: 'o1', name: 'Olivia', role: 'owner' },
    { id: member.id, name: member.displayName, role: 'member' },
  ],
  groups: [],
  ...over,
})

const room = (over: Partial<Conversation> = {}): Conversation => ({
  id: 'r1', title: 'Launch', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: 'q2', archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '',
  messages: [
    msg('q1', null, 'user', { content: 'Coffee first?', author: { id: 'o1', name: 'Olivia' }, forPeople: true }),
    msg('q2', 'q1', 'user', { content: 'What could go wrong?', author: { id: member.id, name: member.displayName } }),
  ],
  room: brief(),
  ...over,
})

describe('rooms', () => {
  it('shows who wrote what, sends through the line, and says it to the people only when asked', async () => {
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [{ id: 'r1', title: 'Launch', updatedAt: new Date().toISOString(), isRoom: true, mine: false }] }),
      'GET /api/chat/conversations/r1': () => ({ json: room() }),
      'GET /api/chat/conversations/r1/stamp': () => ({ json: { stamp: 'x', answering: false } }),
      'GET /api/rooms/r1/events': () => ({ hang: true }),
      'POST /api/chat/conversations/r1/queue': () => ({ json: { queued: [], answering: false } }),
    })
    renderApp('/chat/r1')
    expect(await screen.findByText('What could go wrong?')).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Olivia' })).toHaveTextContent('to the room')
    expect(screen.getByRole('region', { name: 'You' })).toBeInTheDocument()

    // A reply to a person, naming another: sent with what it replies to and whom it names.
    await userEvent.click(screen.getByRole('button', { name: 'Reply to Olivia' }))
    expect(screen.getByText(/Replying to Olivia/)).toBeInTheDocument()
    await userEvent.type(screen.getByRole('textbox', { name: 'Message' }), 'Yes @olivia')
    await userEvent.keyboard('{Enter}')
    await waitFor(() => expect(calls.find((c) => c.path === '/api/chat/conversations/r1/queue')?.body).toEqual({ content: 'Yes @olivia', attachments: [], replyTo: 'q1', mentions: ['o1'] }))
    expect(screen.queryByText(/Replying to Olivia/)).toBeNull()

    await userEvent.click(screen.getByRole('button', { name: /People in this room \(2/ }))
    const panel = await screen.findByRole('dialog', { name: 'People in this room' })
    expect(within(panel).getByText('Olivia')).toBeInTheDocument()
    expect(within(panel).getByRole('button', { name: 'Leave the room' })).toBeInTheDocument()
    expect(within(panel).queryByRole('region', { name: 'Invite' })).toBeNull()
  })

  it('a viewer reads without a box to write in', async () => {
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/r1': () => ({ json: room({ room: brief({ role: 'viewer' }) }) }),
      'GET /api/chat/conversations/r1/stamp': () => ({ json: { stamp: 'x', answering: false } }),
      'GET /api/rooms/r1/events': () => ({ hang: true }),
    })
    renderApp('/chat/r1')
    expect(await screen.findByText(/You read this room/)).toBeInTheDocument()
    expect(screen.queryByRole('textbox', { name: 'Message' })).toBeNull()
  })

  it('reads the room in the reader\'s language, others\' messages translated, the original a click away', async () => {
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [{ id: 'r1', title: 'Launch', updatedAt: new Date().toISOString(), isRoom: true, mine: false }] }),
      'GET /api/chat/conversations/r1': () => ({ json: room() }),
      'GET /api/chat/conversations/r1/stamp': () => ({ json: { stamp: 'x', answering: false } }),
      'GET /api/rooms/r1/events': () => ({ hang: true }),
      'GET /api/rooms/languages': () => ({ json: [{ code: 'en', name: 'English', own: 'English' }, { code: 'fa', name: 'Persian', own: 'فارسی' }] }),
      'POST /api/rooms/r1/translations': () => ({ json: { translations: [{ id: 'q1', text: 'اول قهوه؟', same: false }], error: null } }),
    })
    renderApp('/chat/r1')
    expect(await screen.findByText('Coffee first?')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Read the room in your language' }))
    await userEvent.click(await screen.findByRole('menuitemradio', { name: /فارسی/ }))
    const olivia = screen.getByRole('region', { name: 'Olivia' })
    expect(await within(olivia).findByText('اول قهوه؟')).toBeInTheDocument()
    // Only others' messages are asked for; one's own stays as written.
    expect(calls.find((c) => c.path === '/api/rooms/r1/translations')?.body).toEqual({ language: 'fa', ids: ['q1'] })
    expect(screen.getByText('What could go wrong?')).toBeInTheDocument()
    await userEvent.click(within(olivia).getByRole('button', { name: /Show the original/ }))
    expect(within(olivia).getByText('Coffee first?')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reading in Persian' })).toBeInTheDocument()
    expect(localStorage.getItem('arena.room.readIn')).toBe('fa')
    localStorage.removeItem('arena.room.readIn')
  })
})
