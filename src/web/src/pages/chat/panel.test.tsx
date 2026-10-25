import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import type { ChatConfig, Conversation } from './types'

const config: ChatConfig = {
  model: 'Main-Model', models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: false, loaded: true, prices: { input: 0.2, cachedInput: 0.02, output: 0.8 } }],
  presets: [{ level: 'off', label: 'No thinking' }], defaultThinking: 'off', argus: false, tools: [], gitlabUrl: null, maxUploadBytes: 20 * 1024 * 1024, imageTypes: ['image/png'],
}

const conversation: Conversation = {
  id: 'c1', title: 'Release', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: null, archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '', messages: [],
}

afterEach(() => vi.restoreAllMocks())

describe('a panel', () => {
  it('seats roles and an assistant, and shows them speaking in turn, then the verdict', async () => {
    let polls = 0
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [conversation] }),
      'GET /api/chat/conversations/c1': () => ({ json: conversation }),
      'GET /api/panels/roles': () => ({ json: [{ name: 'The expert', role: 'knows' }, { name: 'The critic', role: 'doubts' }, { name: 'The one who says no', role: 'argues' }] }),
      'GET /api/assistants': () => ({ json: [{ id: 'a1', name: 'Release manager', description: 'Plans releases', icon: 'bot', color: 'blue' }] }),
      'POST /api/chat/conversations/c1/panel': () => ({ status: 202, json: { id: 'p1' } }),
      'GET /api/panels/p1': () => {
        polls++
        const done = polls > 1
        return {
          json: {
            id: 'p1', question: 'Ship on Thursday?', status: done ? 'done' : 'working', rounds: 1, error: null,
            panelists: [{ name: 'The expert' }, { name: 'The critic' }, { name: 'Release manager' }],
            turns: [{ round: 1, speaker: 'The expert', text: 'Yes, the tests pass.' }, ...(done ? [{ round: 1, speaker: 'The critic', text: 'Not without the fix.' }] : [])],
            verdict: done ? 'Ship on Friday.\n\n**Who said what**\n- The expert: tests pass' : null,
          },
        }
      },
    })
    renderApp('/chat/c1')
    await userEvent.click(await screen.findByRole('button', { name: 'Ask a panel' }))
    const dialog = await screen.findByRole('dialog', { name: 'Ask a panel' })
    await userEvent.type(within(dialog).getByLabelText('The question'), 'Ship on Thursday?')
    // The one who says no out, the release manager in.
    await userEvent.click(within(dialog).getByRole('checkbox', { name: /The one who says no/ }))
    await userEvent.click(await within(dialog).findByRole('checkbox', { name: /Release manager/ }))
    await userEvent.click(within(dialog).getByRole('radio', { name: 'One round' }))
    await userEvent.click(within(dialog).getByRole('button', { name: /Ask the panel/ }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/chat/conversations/c1/panel')?.body).toEqual({
      question: 'Ship on Thursday?', rounds: 1, panelists: [{ role: 'The expert' }, { role: 'The critic' }, { assistantId: 'a1' }],
    }))
    const said = await screen.findByRole('list', { name: 'What the panel said' })
    expect(said).toHaveTextContent('Yes, the tests pass.')
    expect(await screen.findByRole('region', { name: 'The verdict' }, { timeout: 4000 })).toHaveTextContent('Ship on Friday.')
    expect(screen.getByRole('button', { name: 'Done: it is in the chat' })).toBeInTheDocument()
  })

  it('in a new chat, makes the chat first and the panel goes into it', async () => {
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'POST /api/chat/conversations': () => ({ status: 201, json: { ...conversation, id: 'c9' } }),
      'GET /api/chat/conversations/c9': () => ({ json: { ...conversation, id: 'c9' } }),
      'GET /api/panels/roles': () => ({ json: [{ name: 'The expert', role: 'knows' }, { name: 'The critic', role: 'doubts' }] }),
      'GET /api/assistants': () => ({ json: [] }),
      'POST /api/chat/conversations/c9/panel': () => ({ status: 202, json: { id: 'p9' } }),
      'GET /api/panels/p9': () => ({
        json: { id: 'p9', question: 'Tabs or spaces?', status: 'working', rounds: 1, error: null, panelists: [{ name: 'The expert' }, { name: 'The critic' }], turns: [{ round: 1, speaker: 'The expert', text: 'Spaces.' }], verdict: null },
      }),
    })
    renderApp('/chat')
    await userEvent.click(await screen.findByRole('button', { name: 'Ask a panel' }))
    const dialog = await screen.findByRole('dialog', { name: 'Ask a panel' })
    await userEvent.type(within(dialog).getByLabelText('The question'), 'Tabs or spaces?')
    await userEvent.click(within(dialog).getByRole('button', { name: /Ask the panel/ }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/chat/conversations/c9/panel')).toBe(true))
    expect(calls.findIndex((c) => c.method === 'POST' && c.path === '/api/chat/conversations')).toBeLessThan(calls.findIndex((c) => c.path === '/api/chat/conversations/c9/panel'))
    // It goes on in the chat made for it, still in view.
    expect(await screen.findByRole('list', { name: 'What the panel said' })).toHaveTextContent('Spaces.')
  })
})
