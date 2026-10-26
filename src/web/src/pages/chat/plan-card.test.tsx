import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import { blank } from './live'
import type { ChatConfig, Conversation, Message } from './types'

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: true, loaded: true, prices: { input: null, cachedInput: null, output: null } }],
  presets: [],
  defaultThinking: null,
  argus: false,
  tools: [{ id: 'plans', title: 'Plans', description: 'Makes and keeps plans.', icon: 'list-checks', onByDefault: false, askFirst: false }],
  gitlabUrl: null,
  maxUploadBytes: 1024 * 1024,
  imageTypes: [],
}

const msg = (id: string, parentId: string | null, role: Message['role'], over: Partial<Message> = {}): Message => ({ ...blank(id, role, parentId), ...over })

/** A chat whose answer proposed a plan of its own work. */
const chat: Conversation = {
  id: 'c1', title: 'Market study', thinking: null, tools: ['plans'], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: 'a2', archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '',
  messages: [
    msg('q1', null, 'user', { content: 'Study the market for e-bikes.' }),
    msg('a1', 'q1', 'assistant', { toolCalls: [{ id: 'call_1', function: { name: 'plan_make', arguments: '{"title":"Market study","propose":true}' } }] }),
    msg('t1', 'a1', 'tool', { toolCallId: 'call_1', toolName: 'plan_make', content: '{"link":"/plans/p1"}', details: { plan: 'p1' } }),
    msg('a2', 't1', 'assistant', { content: 'Here is my plan: approve it and I start.' }),
  ],
}

const none = { start: null, due: null, owner: null, notes: null, dependsOn: [], files: [], checkResult: null, checkOutput: null, checkedAt: null }
const plan = {
  id: 'p1', title: 'Market study', goal: 'Who sells e-bikes in Europe, at what price', conversationId: 'c1', source: 'chat', place: null, review: 'proposed', updatedAt: '', done: 0, warnings: [],
  steps: [
    { number: 1, title: 'Find the competitors', status: 'todo', check: 'Five named, with prices', ...none },
    { number: 2, title: 'Write the summary', status: 'todo', check: null, ...none },
  ],
}

describe('plans in the chat', () => {
  it('a plan the chat proposed is shown under its answer, a step changed and approved there, each answer sent as the next message', async () => {
    let reviewed = plan
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: chat }),
      'GET /api/plans/p1': () => ({ json: reviewed }),
      'POST /api/plans/p1/review': (body) => {
        const b = body as { action: string; step?: number; text?: string }
        if (b.action === 'approve') reviewed = { ...plan, review: 'approved' }
        return { json: { message: b.action === 'approve' ? 'I approve the plan "Market study". Carry it out now.' : `Change step ${b.step}: ${b.text}. Then propose the plan again.` } }
      },
      'POST /api/chat/conversations/c1/messages': () => ({ events: [{ type: 'question', id: 'q2', parentId: 'a2' }, { type: 'assistant', id: 'a3', parentId: 'q2', model: 'Main-Model' }, { type: 'content', text: 'On it.' }, { type: 'done' }] }),
    })
    renderApp('/chat/c1')
    const card = await screen.findByRole('region', { name: 'Plan: Market study' })
    expect(within(card).getByText('Proposed: waiting for you')).toBeInTheDocument()
    expect(within(card).getByText('Done when: Five named, with prices')).toBeInTheDocument()

    await userEvent.click(within(card).getByRole('button', { name: 'Change step 1' }))
    await userEvent.type(within(card).getByRole('textbox', { name: 'What should change in step 1' }), 'only Europe')
    await userEvent.click(within(card).getByRole('button', { name: 'Send' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/chat/conversations/c1/messages')?.body).toMatchObject({ content: 'Change step 1: only Europe. Then propose the plan again.' }))
    expect(calls.find((c) => c.path === '/api/plans/p1/review')?.body).toEqual({ action: 'change', step: 1, text: 'only Europe' })
  })

  it('approved, the plan carried out shows each step as it stands, and asks nothing more', async () => {
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: chat }),
      'GET /api/plans/p1': () => ({ json: { ...plan, review: 'approved', done: 1, steps: [{ ...plan.steps[0], status: 'done' }, { ...plan.steps[1], status: 'doing' }] } }),
    })
    renderApp('/chat/c1')
    const card = await screen.findByRole('region', { name: 'Plan: Market study' })
    expect(within(card).getByText('1 of 2 done')).toBeInTheDocument()
    expect(within(within(card).getByRole('list', { name: 'Its steps' })).getByLabelText('Doing')).toBeInTheDocument()
    expect(within(card).queryByRole('button', { name: /Approve/ })).not.toBeInTheDocument()
  })
})
