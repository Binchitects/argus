import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import { blank } from './live'
import type { AgentWork, ChatConfig, Conversation, Message } from './types'

const config: ChatConfig = {
  model: 'Main-Model',
  models: [{ name: 'Main-Model', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: true, loaded: true, prices: { input: null, cachedInput: null, output: null } }],
  presets: [],
  defaultThinking: null,
  codeIntel: false,
  tools: [],
  gitlabUrl: null,
  maxUploadBytes: 1024 * 1024,
  imageTypes: [],
}

const msg = (id: string, parentId: string | null, role: Message['role'], over: Partial<Message> = {}): Message => ({ ...blank(id, role, parentId), ...over })

/** What Code Arena keeps of a task call's sub-agent (src/CodeArena/SubAgentWork.cs). */
const work: AgentWork = {
  title: 'find the parser',
  instructions: 'Find where class Parser is defined.',
  reasoning: '',
  text: 'Parser is in src/a.cs:1.',
  steps: [{ id: 's1', name: 'grep', arguments: '{"pattern":"class Parser"}', result: 'src/a.cs:1: class Parser { }', isError: false }],
  status: 'done',
  error: null,
  ms: 1200,
}

const task = (details: Message['details']): Conversation => ({
  id: 'c1', title: 'Where is the parser', thinking: null, tools: [], useCodeIntel: false, model: null, systemPrompt: null, temperature: null, topP: null, maxTokens: null,
  currentLeafId: 'a2', archivedAt: null, forkedFrom: null, createdAt: '', updatedAt: '',
  messages: [
    msg('q1', null, 'user', { content: 'where is the parser?' }),
    msg('a1', 'q1', 'assistant', { toolCalls: [{ id: 'call_1', function: { name: 'task', arguments: '{"description":"find the parser","prompt":"Find where class Parser is defined."}' } }] }),
    msg('t1', 'a1', 'tool', { toolCallId: 'call_1', toolName: 'task', content: 'Parser is in src/a.cs:1.', details }),
    msg('a2', 't1', 'assistant', { content: 'The sub-agent says: Parser is in src/a.cs:1.' }),
  ],
})

describe("Code Arena's sub-agents", () => {
  it('a task call shows its sub-agent as the chat shows its own: what it was asked, its tool calls and its report', async () => {
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: task({ agents: [work] }) }),
    })
    renderApp('/chat/c1')
    const card = (await screen.findByText('find the parser', { selector: '*' })).closest('.rounded-lg') as HTMLElement
    await userEvent.click(within(card).getAllByRole('button')[0])
    expect(await within(card).findByText('1 of 1 part done')).toBeInTheDocument()
    expect(within(card).getByText('Parser is in src/a.cs:1.')).toBeInTheDocument()
  })

  it('a task call with no work kept (a session from before) is a tool call as before, not a sub-agent that never ran', async () => {
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config }),
      'GET /api/chat/conversations': () => ({ json: [] }),
      'GET /api/chat/conversations/c1': () => ({ json: task(null) }),
    })
    renderApp('/chat/c1')
    await screen.findByText('The sub-agent says: Parser is in src/a.cs:1.')
    expect(screen.queryByText(/not run/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/of 1 part done/)).not.toBeInTheDocument()
  })
})
