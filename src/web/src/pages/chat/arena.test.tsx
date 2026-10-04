import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import { blank, reduce, type LiveState } from './live'
import { arenaView, sideMessages } from './quality'
import { ChatTree } from './tree'
import type { ArenaMatch, ChatConfig, Conversation, Message } from './types'

const config: ChatConfig = {
  model: 'Main-Model',
  models: [
    { name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: true, loaded: true, prices: { input: null, cachedInput: null, output: null } },
    { name: 'Eyes-Model', context: 32768, maxOutput: 4096, vision: true, tools: true, thinking: false, loaded: true, prices: { input: null, cachedInput: null, output: null } },
    { name: 'Cold-Model', context: 32768, maxOutput: 4096, vision: false, tools: true, thinking: false, loaded: false, prices: { input: null, cachedInput: null, output: null } },
  ],
  presets: [],
  defaultThinking: null,
  argus: false,
  tools: [],
  gitlabUrl: null,
  maxUploadBytes: 20 * 1024 * 1024,
  imageTypes: ['image/png'],
}

const conversation = (over: Partial<Conversation> = {}): Conversation => ({
  id: 'c1', title: 'Stable sorts', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: null, archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '', messages: [], arenas: [], ...over,
})

const msg = (id: string, parentId: string | null, role: Message['role'], over: Partial<Message> = {}): Message => ({ ...blank(id, role, parentId), ...over })

const compared = [
  { type: 'question', id: 'q1', parentId: null },
  { type: 'title', title: 'Stable sorts' },
  { type: 'arena', id: 'm1', questionId: 'q1', side: 'a', step: 1, of: 2 },
  { type: 'assistant', id: 'a1', parentId: 'q1', model: 'Model A', side: 'a' },
  { type: 'content', text: 'Merge sort is stable.' },
  { type: 'arena', id: 'm1', questionId: 'q1', side: 'b', step: 2, of: 2 },
  { type: 'assistant', id: 'b1', parentId: 'q1', model: 'Model B', side: 'b' },
  { type: 'content', text: 'Insertion sort is stable too.' },
  { type: 'done', id: 'a1' },
]
const match = (over: Partial<ArenaMatch> = {}): ArenaMatch => ({ id: 'm1', questionId: 'q1', a: 'a1', b: 'b1', vote: null, models: null, ...over })
const savedMessages = (names = { a: 'Model A', b: 'Model B' }) => [
  msg('q1', null, 'user', { content: 'Which sort is stable?' }),
  msg('a1', 'q1', 'assistant', { content: 'Merge sort is stable.', model: names.a }),
  msg('b1', 'q1', 'assistant', { content: 'Insertion sort is stable too.', model: names.b }),
]

async function ask(text: string) {
  const box = await screen.findByRole('textbox', { name: 'Message' })
  await userEvent.type(box, text)
  await userEvent.keyboard('{Enter}')
}

describe('arena mode', () => {
  it('Compare sends the question to two models, shows them side by side and blind, and the vote reveals the names', async () => {
    let voted = false
    let answered = false
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'POST /api/chat/conversations': () => ({ status: 201, json: conversation({ title: 'New chat' }) }),
      'GET /api/chat/conversations/c1': () => ({
        json: !answered
          ? conversation({ title: 'New chat' })
          : voted
            ? conversation({ messages: savedMessages({ a: 'Main-Model', b: 'Eyes-Model' }), currentLeafId: 'b1', arenas: [match({ vote: 'b', models: { a: 'Main-Model', b: 'Eyes-Model' } })] })
            : conversation({ messages: savedMessages(), currentLeafId: 'a1', arenas: [match()] }),
      }),
      'POST /api/chat/conversations/c1/compare': () => {
        answered = true
        return { events: compared }
      },
      'POST /api/chat/arena/m1/vote': () => {
        voted = true
        return { json: { vote: 'b', models: { a: 'Main-Model', b: 'Eyes-Model' }, currentLeafId: 'b1' } }
      },
    })
    renderApp('/chat')
    await userEvent.click(await screen.findByRole('button', { name: 'Compare' }))
    const picker = await screen.findByRole('dialog', { name: 'Compare two models' })
    // Only models that can answer now are offered.
    expect(within(picker).queryByText('Cold-Model')).not.toBeInTheDocument()
    expect(within(picker).getByText('Two at random.')).toBeInTheDocument()
    await userEvent.click(within(picker).getByRole('button', { name: 'Compare the next question' }))
    expect(screen.getByRole('button', { name: 'Compare' })).toHaveAttribute('aria-pressed', 'true')
    await ask('Which sort is stable?')

    await waitFor(() => expect(calls.find((c) => c.path === '/api/chat/conversations/c1/compare')?.body).toEqual({ content: 'Which sort is stable?', attachments: [], root: true }))
    const comparison = await screen.findByRole('region', { name: 'Comparison' })
    const a = within(comparison).getByRole('region', { name: 'Model A' })
    const b = within(comparison).getByRole('region', { name: 'Model B' })
    expect(await within(a).findByText('Merge sort is stable.')).toBeInTheDocument()
    expect(within(b).getByText('Insertion sort is stable too.')).toBeInTheDocument()
    // Blind: no name in the comparison (the header names only the chat's own model), and no thumbs on a side: the vote is the rating.
    expect(within(comparison).queryByText(/Main-Model|Eyes-Model/)).not.toBeInTheDocument()
    expect(within(comparison).queryByRole('button', { name: 'Good answer' })).not.toBeInTheDocument()
    // Compare was for that question: off again.
    expect(screen.getByRole('button', { name: 'Compare' })).toHaveAttribute('aria-pressed', 'false')

    await userEvent.click(await within(comparison).findByRole('button', { name: 'B is better' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/chat/arena/m1/vote')?.body).toEqual({ vote: 'b' }))
    const revealed = await screen.findByRole('region', { name: 'Comparison' })
    expect(await within(revealed).findByText('Your pick')).toBeInTheDocument()
    expect(within(within(revealed).getByRole('region', { name: 'Model B' })).getAllByText('Eyes-Model').length).toBeGreaterThan(0)
    expect(within(revealed).getByText('Compared. Your vote: B is better')).toBeInTheDocument()
    expect(within(revealed).getByRole('link', { name: 'leaderboard' })).toHaveAttribute('href', '/leaderboard')
  })

  it('two chosen models go with the question; while it streams the page says which answers', async () => {
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: conversation({ messages: savedMessages().slice(0, 1), currentLeafId: 'q1' }) }),
      'POST /api/chat/conversations/c1/compare': () => ({ events: compared.slice(0, 5).map((e) => (e.type === 'question' ? { ...e, id: 'q2', parentId: 'q1' } : e.type === 'arena' ? { ...e, questionId: 'q2' } : e.type === 'assistant' ? { ...e, parentId: 'q2' } : e)), hang: true }),
    })
    renderApp('/chat/c1')
    await userEvent.click(await screen.findByRole('button', { name: 'Compare' }))
    const picker = await screen.findByRole('dialog', { name: 'Compare two models' })
    await userEvent.click(within(picker).getByRole('checkbox', { name: 'Main-Model' }))
    expect(within(picker).getByRole('button', { name: 'Compare the next question' })).toBeDisabled()
    await userEvent.click(within(picker).getByRole('checkbox', { name: 'Eyes-Model' }))
    await userEvent.click(within(picker).getByRole('button', { name: 'Compare the next question' }))
    await ask('And which is fastest?')
    await waitFor(() => expect(calls.find((c) => c.path === '/api/chat/conversations/c1/compare')?.body).toMatchObject({ content: 'And which is fastest?', models: ['Main-Model', 'Eyes-Model'], parentId: 'q1' }))
    const comparison = await screen.findByRole('region', { name: 'Comparison' })
    expect(within(comparison).getByText('Comparing two models: answering 1 of 2, one after the other')).toBeInTheDocument()
    expect(within(comparison).getByText('Answers once Model A is done.')).toBeInTheDocument()
    expect(within(comparison).queryByRole('button', { name: 'A is better' })).not.toBeInTheDocument()
  })

  it('a comparison refused says why, and the question and Compare stay for another try', async () => {
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: conversation({ messages: savedMessages().slice(0, 1), currentLeafId: 'q1' }) }),
      'POST /api/chat/conversations/c1/compare': () => ({ status: 400, json: { status: 'models', error: 'Comparing needs two models you may use that can answer now, and only Main-Model can.' } }),
    })
    renderApp('/chat/c1')
    await userEvent.click(await screen.findByRole('button', { name: 'Compare' }))
    await userEvent.click(within(await screen.findByRole('dialog', { name: 'Compare two models' })).getByRole('button', { name: 'Compare the next question' }))
    await ask('Which is fastest?')
    expect(await screen.findByText(/only Main-Model can/)).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Message' })).toHaveValue('Which is fastest?')
    expect(screen.getByRole('button', { name: 'Compare' })).toHaveAttribute('aria-pressed', 'true')
  })

  it('a comparison stopped before both answered has nothing to vote on', () => {
    const tree = new ChatTree(savedMessages().slice(0, 2))
    const view = arenaView(savedMessages()[0]!, [match({ b: null })], null, false)!
    expect(view.b).toBeNull()
    expect(sideMessages(tree, view.a).map((m) => m.id)).toEqual(['a1'])
    expect(sideMessages(tree, view.b)).toEqual([])
  })

  it('the stream marks where each side starts, and a side ends before the next question', () => {
    let s: LiveState = { messages: [msg('q1', null, 'user')], leaf: 'q1', notices: [], title: null, thinkingSince: null }
    for (const e of compared.slice(2) as Parameters<typeof reduce>[1][]) s = reduce(s, e, null)
    expect(s.arena).toMatchObject({ id: 'm1', questionId: 'q1', side: 'b', step: 2, a: 'a1', b: 'b1' })
    const tree = new ChatTree([...s.messages, msg('t1', 'a1', 'tool'), msg('a2', 't1', 'assistant'), msg('q2', 'a2', 'user')])
    expect(sideMessages(tree, 'a1').map((m) => m.id)).toEqual(['a1', 't1', 'a2'])
  })
})

describe('feedback', () => {
  const rated = [msg('q1', null, 'user', { content: 'hello' }), msg('a1', 'q1', 'assistant', { content: 'Hi there.', model: 'Main-Model' })]

  it('thumbs up, then down with a reason, words and the chat shared with the admins', async () => {
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: conversation({ messages: rated, currentLeafId: 'a1' }) }),
      'PUT /api/chat/conversations/c1/messages/a1/feedback': (body) => ({ json: { reason: null, comment: null, shared: false, ...(body as object) } }),
      'DELETE /api/chat/conversations/c1/messages/a1/feedback': () => ({ status: 204 }),
    })
    renderApp('/chat/c1')
    const answer = await screen.findByRole('region', { name: 'Answer' })
    const up = within(answer).getByRole('button', { name: 'Good answer' })
    await userEvent.click(up)
    await waitFor(() => expect(up).toHaveAttribute('aria-pressed', 'true'))
    expect(calls.filter((c) => c.method === 'PUT').map((c) => c.body)).toEqual([{ up: true }])
    // The thumb that is on takes it back.
    await userEvent.click(up)
    await waitFor(() => expect(up).toHaveAttribute('aria-pressed', 'false'))
    expect(calls.some((c) => c.method === 'DELETE')).toBe(true)

    // Down is kept at once; then why.
    await userEvent.click(within(answer).getByRole('button', { name: 'Bad answer' }))
    const why = await screen.findByRole('dialog', { name: 'What was wrong' })
    await userEvent.click(within(why).getByRole('button', { name: 'Too long' }))
    await userEvent.type(within(why).getByRole('textbox', { name: 'Tell more' }), 'Half would do.')
    await userEvent.click(within(why).getByRole('checkbox', { name: 'Share this chat with the admins' }))
    await userEvent.click(within(why).getByRole('button', { name: 'Send' }))
    await waitFor(() =>
      expect(calls.filter((c) => c.method === 'PUT').map((c) => c.body)).toEqual([{ up: true }, { up: false }, { up: false, reason: 'too_long', comment: 'Half would do.', share: true }]),
    )
    expect(within(answer).getByRole('button', { name: 'Bad answer' })).toHaveAttribute('aria-pressed', 'true')
  })

  it("an answer rated before shows the person's rating", async () => {
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({
        json: conversation({ messages: [rated[0]!, { ...rated[1]!, feedback: { up: false, reason: 'wrong', comment: null, shared: false } }], currentLeafId: 'a1' }),
      }),
    })
    renderApp('/chat/c1')
    const answer = await screen.findByRole('region', { name: 'Answer' })
    expect(within(answer).getByRole('button', { name: 'Bad answer' })).toHaveAttribute('aria-pressed', 'true')
    expect(within(answer).getByRole('button', { name: 'Good answer' })).toHaveAttribute('aria-pressed', 'false')
  })
})
