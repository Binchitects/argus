import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import { blank } from './live'
import type { ChatConfig, Conversation } from './types'

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: false, loaded: true, prices: { input: null, cachedInput: null, output: null } }],
  presets: [], defaultThinking: null, argus: false, tools: [], gitlabUrl: null, maxUploadBytes: 1024 * 1024, imageTypes: [],
}

const conversation: Conversation = {
  id: 'c1', title: 'Pricing', thinking: null, tools: [], useArgus: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: 'a1', archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '',
  messages: [{ ...blank('q1', 'user', null), content: 'Hello' }, { ...blank('a1', 'assistant', 'q1'), content: 'Hi.' }],
}

const answered = {
  events: [
    { type: 'question', id: 'q2', parentId: 'a1' },
    { type: 'assistant', id: 'a2', parentId: 'q2', model: 'Main-Model' },
    { type: 'content', text: 'Their prices are public.' },
    { type: 'done', id: 'a2' },
  ],
}

describe('safeguards in the chat', () => {
  it('a rule that asks for a reason waits for it, and the message goes with it', async () => {
    const sent: unknown[] = []
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: conversation }),
      'POST /api/chat/conversations/c1/messages': (body) => {
        sent.push(body)
        return (body as { acknowledged?: string[] }).acknowledged
          ? answered
          : { status: 409, json: { status: 'safeguard_justify', error: 'It looks like it is about competitors. Say why you need to send it: your reason is recorded.', rules: ['r1'] } }
      },
    })
    renderApp('/chat/c1')
    await screen.findByText('Hi.')
    await userEvent.type(screen.getByRole('textbox', { name: 'Message' }), 'How does Acme price this?{Enter}')
    const dialog = await screen.findByRole('dialog', { name: 'Why do you need to send it?' })
    expect(within(dialog).getByText(/about competitors/)).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Send it' })).toBeDisabled()
    await userEvent.type(within(dialog).getByLabelText('Your reason'), 'Market research for the board')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Send it' }))
    await waitFor(() => expect(sent).toHaveLength(2))
    expect(sent[1]).toMatchObject({ content: 'How does Acme price this?', acknowledged: ['r1'], justification: 'Market research for the board' })
    await waitFor(() => expect(screen.getByRole('textbox', { name: 'Message' })).toHaveValue(''))
  })

  it('a refusal can be sent for review, with why', async () => {
    const calls = fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: conversation }),
      'POST /api/chat/conversations/c1/messages': () => ({ status: 403, json: { status: 'safeguard_blocked', error: 'This message was not sent.', eventId: 'e1' } }),
      'POST /api/safeguards/events/e1/review': () => ({ status: 204 }),
    })
    renderApp('/chat/c1')
    await screen.findByText('Hi.')
    await userEvent.type(screen.getByRole('textbox', { name: 'Message' }), 'About Project Falcon{Enter}')
    expect(await screen.findByText('This message was not sent.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Ask for a review' }))
    const dialog = await screen.findByRole('dialog', { name: 'Ask for a review' })
    await userEvent.type(within(dialog).getByLabelText('Why it should be allowed'), 'I lead the project.')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Send for review' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/safeguards/events/e1/review')?.body).toEqual({ reason: 'I lead the project.' }))
    // The message is back in the box.
    expect(screen.getByRole('textbox', { name: 'Message' })).toHaveValue('About Project Falcon')
  })
})
