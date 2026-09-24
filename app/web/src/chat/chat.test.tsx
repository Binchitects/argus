import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeBackend, member, renderApp } from '../test-utils'

const config = { model: 'Qwen3.8-Flash-Next', presets: [{ level: 'xhigh', label: 'Deep think' }, { level: 'off', label: 'No thinking' }], defaultThinking: 'xhigh', argus: true }
const conversation = { id: 'c1', title: 'New chat', thinking: null, useArgus: true, createdAt: '', updatedAt: '', messages: [] }

const m = (role: string, over: object = {}) => ({
  id: `${role}-${Math.random()}`, role, content: '', reasoning: null, toolName: null, toolCallId: null, toolCalls: null, attachments: [],
  status: 'complete', error: null, promptTokens: null, cachedTokens: null, completionTokens: null, noAccess: false, ...over,
})

/** `saved` is what the server holds once the answer is over: the page reloads it, as it does for real. */
function backend(events: object[], saved: object[] = [], hang = false) {
  let answered = false
  return fakeBackend(member, {
    'GET /api/chat/config': () => ({ json: config }),
    'GET /api/chat/conversations': () => ({ json: [] }),
    'POST /api/chat/conversations': () => ({ status: 201, json: conversation }),
    'GET /api/chat/conversations/c1': () => ({ json: { ...conversation, messages: answered ? saved : [] } }),
    'POST /api/chat/conversations/c1/messages': () => {
      answered = true
      return { events, hang }
    },
  })
}

async function ask(text: string) {
  const box = await screen.findByRole('textbox', { name: 'Message' })
  await userEvent.type(box, text)
  await userEvent.keyboard('{Enter}')
}

describe('chat', () => {
  it('streams an answer with its thinking, and sends what was typed with the chosen settings', async () => {
    const calls = backend([
      { type: 'title', title: 'Explain retries' },
      { type: 'assistant', id: 'a1' },
      { type: 'reasoning', text: 'Consider backoff.' },
      { type: 'content', text: 'Use **exponential** backoff:\n\n```python\nsleep(2 ** n)\n```' },
      { type: 'usage', prompt: 120, cached: 80, completion: 30 },
      { type: 'done', id: 'a1' },
    ], [
      m('user', { content: 'Explain retries' }),
      m('assistant', { reasoning: 'Consider backoff.', content: 'Use **exponential** backoff:\n\n```python\nsleep(2 ** n)\n```', promptTokens: 120, cachedTokens: 80, completionTokens: 30 }),
    ])
    renderApp('/chat')
    await userEvent.selectOptions(await screen.findByLabelText('Thinking'), 'off')
    await ask('Explain retries')
    const answer = await screen.findByLabelText('Answer')
    expect(await within(answer).findByText('exponential')).toHaveProperty('tagName', 'STRONG')
    expect(within(answer).getByText('Thought process')).toBeInTheDocument()
    expect(answer.querySelector('code.hljs')?.textContent).toBe('sleep(2 ** n)')
    expect(within(answer).getByText(/120 in \(80 cached\) · 30 out/)).toBeInTheDocument()
    expect(screen.getByLabelText('You')).toHaveTextContent('Explain retries')
    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/chat/conversations')!.body).toEqual({ thinking: 'off', useArgus: true })
    expect(calls.find((c) => c.path.endsWith('/messages'))!.body).toEqual({ content: 'Explain retries', attachments: [] })
  })

  it('shows tool use, and a no-access notice naming whom to ask', async () => {
    backend([
      { type: 'assistant', id: 'a1' },
      { type: 'tool_call', id: 't1', name: 'find_symbol', arguments: '{"name":"SecretThing"}' },
      { type: 'tool_result', id: 't1', name: 'find_symbol', isError: false, noAccess: true, text: 'Nothing you have access to matches this, but it does exist in 1 repository you cannot read:\n- secret/vault (2 matches) -- maintainers: alice\nTell the person asking...' },
      { type: 'assistant', id: 'a2' },
      { type: 'content', text: 'You cannot read that repository.' },
      { type: 'done', id: 'a2' },
    ], [
      m('user', { content: 'What is SecretThing?' }),
      m('assistant', { toolCalls: [{ id: 't1', function: { name: 'find_symbol', arguments: '{"name":"SecretThing"}' } }] }),
      m('tool', { toolCallId: 't1', toolName: 'find_symbol', noAccess: true, content: 'Nothing you have access to matches this, but it does exist in 1 repository you cannot read:\n- secret/vault (2 matches) -- maintainers: alice\nTell the person asking...' }),
      m('assistant', { content: 'You cannot read that repository.' }),
    ])
    renderApp('/chat')
    await ask('What is SecretThing?')
    expect(await screen.findByText('find_symbol')).toBeInTheDocument()
    expect(screen.getByText('name: SecretThing')).toBeInTheDocument()
    const note = screen.getByRole('note')
    expect(note).toHaveTextContent('You do not have access to some of this code.')
    expect(note).toHaveTextContent('secret/vault (2 matches) -- maintainers: alice')
    expect(screen.getByText('You cannot read that repository.')).toBeInTheDocument()
  })

  it('shows why an answer failed', async () => {
    backend([{ type: 'assistant', id: 'a1' }, { type: 'error', message: 'You have used all your credit. Ask an admin to raise it.' }], [
      m('user', { content: 'hello' }),
      m('assistant', { status: 'failed', error: 'You have used all your credit. Ask an admin to raise it.' }),
    ])
    renderApp('/chat')
    await ask('hello')
    expect(await screen.findByRole('alert')).toHaveTextContent('You have used all your credit.')
  })

  it('stop ends the stream and gives the composer back', async () => {
    backend([{ type: 'assistant', id: 'a1' }, { type: 'content', text: 'partial answer' }], [m('user', { content: 'long one' }), m('assistant', { content: 'partial answer', status: 'stopped' })], true)
    renderApp('/chat')
    await ask('long one')
    await userEvent.click(await screen.findByRole('button', { name: 'Stop' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Send' })).toBeInTheDocument())
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(await screen.findByText('Stopped.')).toBeInTheDocument()
    expect(screen.getByText('partial answer')).toBeInTheDocument()
  })

  it('model HTML never runs', async () => {
    const evil = 'hello there <img src=x onerror="window.pwned=1"><script>window.pwned=2</script>'
    backend([{ type: 'assistant', id: 'a1' }, { type: 'content', text: evil }, { type: 'done', id: 'a1' }], [m('user', { content: 'xss?' }), m('assistant', { content: evil })])
    renderApp('/chat')
    await ask('xss?')
    await screen.findByText(/hello there/)
    expect(document.querySelector('.answer script')).toBeNull()
    expect(document.querySelector('.answer img')?.getAttribute('onerror')).toBeNull()
    expect((window as unknown as { pwned?: number }).pwned).toBeUndefined()
  })
})
