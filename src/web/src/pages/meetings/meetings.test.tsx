import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import type { ChatConfig, Conversation } from '@/pages/chat/types'
import type { MeetingAction, MeetingBrief, MeetingFull } from './api'

const brief = (over: Partial<MeetingBrief> = {}): MeetingBrief => ({
  id: 'm1', title: 'Checkout launch', status: 'ready', step: null, error: null, seconds: 2460, createdAt: '2026-10-23T09:00:00Z', actions: 2, open: 1, ...over,
})

const action = (over: Partial<MeetingAction>): MeetingAction => ({
  id: 'a1', number: 1, text: 'Write the release notes', speaker: 1, given: null, owner: 'Ben', personId: null, person: null, due: '2026-10-30', done: false, ...over,
})

const full = (over: Partial<MeetingFull> = {}): MeetingFull => ({
  meeting: brief(),
  language: 'en',
  summary: 'The checkout launches **Friday**.',
  model: 'Main-Model',
  planId: null,
  transcript: [
    { start: 0, end: 6, speaker: 0, text: 'Ben, are we ready for Friday?' },
    { start: 6, end: 14, speaker: 1, text: 'Yes, once the release notes are written.' },
    { start: 14, end: 22, speaker: 0, text: 'Dana from support needs the notes too.' },
  ],
  speakers: [
    { speaker: 0, name: 'Ana', personId: null, person: null },
    { speaker: 1, name: 'Ben', personId: null, person: null },
  ],
  decisions: ['The checkout launches on Friday.'],
  actions: [action({}), action({ id: 'a2', number: 2, text: 'Tell support', speaker: null, given: 'Dana', owner: 'Dana', due: null })],
  ...over,
})

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: true, loaded: true, prices: { input: 0.2, cachedInput: 0.02, output: 0.8 } }],
  presets: [{ level: 'low', label: 'Quick' }],
  defaultThinking: null,
  argus: false,
  tools: [],
  gitlabUrl: null,
  maxUploadBytes: 20 * 1024 * 1024,
  imageTypes: [],
}

const conversation = (over: Partial<Conversation> = {}): Conversation => ({
  id: 'c9', title: 'About Checkout launch', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: null, archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '', messages: [], ...over,
})

describe('meetings', () => {
  it('lists meetings with their state, and an uploaded recording opens as it is made', async () => {
    const calls = fakeApi(member, {
      'GET /api/meetings': () => ({ json: [brief(), brief({ id: 'm2', title: 'Standup', status: 'processing', step: 'Listening to what was said', seconds: null })] }),
      'POST /api/meetings': () => ({ status: 201, json: brief({ id: 'm3', title: 'Retro', status: 'processing', step: null }) }),
      'GET /api/meetings/m3': () => ({ json: full({ meeting: brief({ id: 'm3', title: 'Retro', status: 'processing', step: 'Reading the recording' }), transcript: [], speakers: [], actions: [], decisions: [], summary: null }) }),
    })
    renderApp('/meetings')
    const list = await screen.findByRole('list', { name: 'Your meetings' })
    expect(within(list).getByText('1 of 2 action items open')).toBeInTheDocument()
    expect(within(list).getByText(/Listening to what was said/)).toBeInTheDocument()
    expect(within(list).getByText(/41:00/)).toBeInTheDocument()

    await userEvent.type(screen.getByRole('textbox', { name: 'Title' }), 'Retro')
    await userEvent.upload(screen.getByLabelText('Recording to upload'), new File(['sound'], 'retro.m4a', { type: 'audio/mp4' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/meetings')).toBe(true))
    const sent = calls.find((c) => c.method === 'POST' && c.path === '/api/meetings')!.body as FormData
    expect((sent.get('file') as File).name).toBe('retro.m4a')
    expect(sent.get('title')).toBe('Retro')
    expect(await screen.findByText(/Reading the recording/, { selector: 'div' })).toBeInTheDocument()
  })

  it('plays the recording from a line, gives a speaker to a person, and changes the action items', async () => {
    const play = vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue()
    let meeting = full()
    const calls = fakeApi(member, {
      'GET /api/meetings/m1': () => ({ json: meeting }),
      'GET /api/sharing/people': () => ({ json: [{ id: 'u7', name: 'Ben Builder', userName: 'ben' }] }),
      'PUT /api/meetings/m1/speakers/1': () => {
        meeting = full({ speakers: [meeting.speakers[0], { speaker: 1, name: 'Ben', personId: 'u7', person: 'Ben Builder' }], actions: [action({ personId: 'u7', person: 'Ben Builder' }), meeting.actions[1]] })
        return { json: meeting }
      },
      'PUT /api/meetings/m1/actions/a1': (body) => {
        meeting = full({ ...meeting, actions: [action({ ...(body as object), personId: 'u7', person: 'Ben Builder' }), meeting.actions[1]] })
        return { json: meeting }
      },
      'PUT /api/meetings/m1/actions/a2': () => ({ json: meeting }),
      'POST /api/meetings/m1/actions': () => {
        meeting = full({ ...meeting, actions: [...meeting.actions, action({ id: 'a3', number: 3, text: 'Book the room', speaker: null, owner: null, due: null })] })
        return { status: 201, json: meeting }
      },
    })
    renderApp('/meetings/m1')
    expect(await screen.findByRole('heading', { name: 'Checkout launch' })).toBeInTheDocument()
    expect(screen.getByText('The checkout launches on Friday.')).toBeInTheDocument()

    // A line plays the recording from its start.
    const said = screen.getByRole('list', { name: 'What was said' })
    await userEvent.click(within(said).getByRole('button', { name: 'Play from 00:06: Ben' }))
    expect(play).toHaveBeenCalled()
    expect(screen.getByLabelText<HTMLAudioElement>('The recording').currentTime).toBe(6)

    // Ben is Ben Builder here: his action item goes to him.
    await userEvent.type(screen.getByRole('textbox', { name: 'Who speaker 2 is in Arena' }), 'Ben')
    await userEvent.click(await screen.findByRole('button', { name: 'Ben Builder' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/meetings/m1/speakers/1')?.body).toEqual({ personId: 'u7' }))
    const items = screen.getByRole('list', { name: 'The action items' })
    expect(await within(items).findByText('→ Ben Builder')).toBeInTheDocument()

    // Done; given to someone else by name; another one added.
    await userEvent.click(within(items).getByRole('checkbox', { name: 'Write the release notes' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/meetings/m1/actions/a1')?.body).toEqual({ done: true }))
    expect(within(items).getByRole('textbox', { name: 'Who, by name: Tell support' })).toHaveValue('Dana')
    await userEvent.selectOptions(within(items).getByRole('combobox', { name: 'Whose: Tell support' }), 'Ana')
    await waitFor(() => expect(calls.find((c) => c.path === '/api/meetings/m1/actions/a2')?.body).toEqual({ speaker: 0 }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Another action item' }), 'Book the room{Enter}')
    expect(await within(items).findByText('Book the room')).toBeInTheDocument()
    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/meetings/m1/actions')?.body).toEqual({ text: 'Book the room' })
    play.mockRestore()
  })

  it('asks about a meeting in a chat that holds its minutes and transcript', async () => {
    const minutes = { id: 'f1', fileName: 'Checkout launch.md', size: 900, truncated: false, kind: 'text' as const, contentType: 'text/markdown' }
    const calls = fakeApi(member, {
      'GET /api/meetings/m1': () => ({ json: full() }),
      'POST /api/meetings/m1/ask': () => ({ status: 201, json: { id: 'c9', message: 'What was decided, and who does what next?', attachment: minutes } }),
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c9': () => ({ json: conversation() }),
      'POST /api/chat/conversations/c9/messages': () => ({ events: [{ type: 'question', id: 'q1', parentId: null }, { type: 'done', id: 'q1' }] }),
    })
    renderApp('/meetings/m1')
    await userEvent.click(await screen.findByRole('button', { name: 'Ask about it' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/chat/conversations/c9/messages')?.body).toMatchObject({ content: 'What was decided, and who does what next?', attachments: ['f1'] }))
  })
})
