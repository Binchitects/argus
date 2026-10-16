import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { makeQueryClient, Providers } from '@/app/providers'
import { blank } from '@/pages/chat/live'
import type { ChatConfig, Message } from '@/pages/chat/types'
import { argsSummary } from '@/pages/chat/tool-args'
import { fakeApi, type Handler } from '@/test/utils'
import { fromSession, keptOutput, reduceCode, type CodeSession, type CodeState, type FileDiff, type SessionSummary } from './api'
import { App } from './app'
import { getCommand } from './commands'

// An upload goes by XMLHttpRequest (for its progress), which has no server in tests.
vi.mock('@/pages/chat/api', async (original) => ({
  ...(await original<typeof import('@/pages/chat/api')>()),
  uploadFile: vi.fn(async (file: File, onProgress: (p: number) => void) => {
    onProgress(1)
    return { id: `att-${file.name}`, fileName: file.name, size: file.size, truncated: false, kind: file.type.startsWith('image/') ? 'image' : 'text', contentType: file.type }
  }),
}))

// Each tool card's arguments, as read for its line: counted, to see which cards are drawn again.
vi.mock('@/pages/chat/tool-args', async (real) => {
  const actual = await real<typeof import('@/pages/chat/tool-args')>()
  return { ...actual, argsSummary: vi.fn(actual.argsSummary) }
})

const state = (over: Partial<CodeState> = {}): CodeState => ({
  name: 'Code Arena', version: '5.0.0', license: 'AGPL-3.0-only', source: 'https://github.com/Binchitects/argus', manual: 'https://llm.test/help/code-arena', folder: '/home/ada/shop', project: 'shop', branch: 'main', model: 'model-a', context: 32768, contextUsed: 4200, compactAt: 80, compactTarget: 25, servers: [], jobs: [], queued: [], thinking: null, mode: 'ask',
  modes: [
    { name: 'ask', description: 'edits and commands ask first' },
    { name: 'auto-edit', description: 'file edits run without asking; commands ask' },
    { name: 'plan', description: 'read-only: no edits, no commands' },
    { name: 'yolo', description: 'nothing asks: edits and commands run at once' },
  ],
  session: 's2', busy: false, arenaTools: true, tools: { local: 10, servers: [{ name: 'Arena', count: 3 }] }, ...over,
})

const config: ChatConfig = {
  model: 'model-a',
  models: [
    { name: 'model-a', context: 32768, maxOutput: 8192, vision: false, tools: true, thinking: true, loaded: true, prices: { input: null, cachedInput: null, output: null } },
    { name: 'model-b', context: 65536, maxOutput: 8192, vision: false, tools: true, thinking: false, loaded: true, prices: { input: null, cachedInput: null, output: null } },
  ],
  auto: null,
  presets: [{ level: 'off', label: 'Off' }, { level: 'high', label: 'High' }],
  defaultThinking: null, argus: false, tools: [], gitlabUrl: null, maxUploadBytes: 0, imageTypes: [],
}

const msg = (id: string, role: Message['role'], over: Partial<Message> = {}): Message => ({ ...blank(id, role, id === 'm0' ? null : `m${Number(id.slice(1)) - 1}`), ...over })

const diff: FileDiff = { path: 'src/a.txt', added: 1, removed: 1, more: 0, created: false, lines: [[' ', 1, 1, 'one'], ['-', 2, 0, 'two'], ['+', 0, 2, '2'], [' ', 3, 3, 'three']] }
const edit = { id: 'c1', function: { name: 'edit_file', arguments: '{"path":"src/a.txt","old_string":"two","new_string":"2"}' } }

/** What the server holds once the turn is over: the page reads it back. */
const saved: Message[] = [
  msg('m0', 'user', { content: 'Change two to 2' }),
  msg('m1', 'assistant', { model: 'model-a', toolCalls: [edit], promptTokens: 1000, cachedTokens: 500, completionTokens: 20 }),
  msg('m2', 'tool', { content: 'Edited src/a.txt.', toolCallId: 'c1', toolName: 'edit_file' }),
  msg('m3', 'assistant', { model: 'model-a', content: 'Changed **two** to 2.', promptTokens: 1100, cachedTokens: 1000, completionTokens: 10 }),
]

const turn = [
  { type: 'question', id: 'm0', parentId: null },
  { type: 'assistant', id: 'm1', parentId: 'm0', model: 'model-a' },
  { type: 'reasoning', text: 'Reading the file first.' },
  { type: 'thought', ms: 1200 },
  { type: 'usage', prompt: 1000, cached: 500, completion: 20, thinkingMs: 1200, durationMs: 1500 },
  { type: 'tool_call', id: 'c1', name: 'edit_file', arguments: edit.function.arguments, tool: 'local' },
  { type: 'tool_result', id: 'c1', messageId: 'm2', name: 'edit_file', text: 'Edited src/a.txt.', isError: false, declined: false, noAccess: false, durationMs: 5, diff },
  { type: 'assistant', id: 'm3', parentId: 'm2', model: 'model-a' },
  { type: 'content', text: 'Changed **two** to 2.' },
  { type: 'usage', prompt: 1100, cached: 1000, completion: 10, thinkingMs: null, durationMs: 800 },
]

const sessions: SessionSummary[] = [
  { id: 's2', title: 'Fix the build', updatedAt: new Date().toISOString(), messages: 4 },
  { id: 's1', title: 'First question', updatedAt: '2020-01-02T10:00:00Z', messages: 2 },
]

/** code-arena web as the page sees it; `session` is what /api/session says, which tests change as the server would. */
function backend(opts: { session?: CodeSession; extra?: Record<string, Handler>; state?: Partial<CodeState> } = {}) {
  const now = { state: state(opts.state), session: opts.session ?? { id: 's2', messages: [], diffs: {}, busy: false } }
  const calls = fakeApi(null, {
    'GET /api/state': () => ({ json: now.state }),
    'GET /api/chat/config': () => ({ json: config }),
    'GET /api/sessions': () => ({ json: sessions }),
    'GET /api/session': () => ({ json: now.session }),
    'POST /api/settings': (body) => {
      now.state = { ...now.state, ...(body as Partial<CodeState>) }
      return { json: now.state }
    },
    'POST /api/approvals': () => ({ status: 204 }),
    'POST /api/stop': () => ({ status: 204 }),
    ...opts.extra,
  })
  return { calls, now }
}

function renderCode() {
  const client = makeQueryClient()
  client.setDefaultOptions({ queries: { ...client.getDefaultOptions().queries, retry: false } })
  return render(
    <Providers client={client}>
      <App />
    </Providers>,
  )
}

async function ask(text: string) {
  const box = await screen.findByRole('textbox', { name: 'Message' })
  await userEvent.type(box, text)
  await userEvent.keyboard('{Enter}')
}

describe('Code Arena in the browser', () => {
  it('streams a turn: the thinking, the answer as Markdown, the tool card with its diff, then the tokens with the cached share', async () => {
    let end!: () => void
    const over = new Promise<void>((r) => (end = r))
    const { calls, now } = backend({
      extra: {
        'POST /api/messages': () => {
          now.session = { id: 's2', messages: saved, diffs: { m2: diff }, busy: false }
          return { events: turn, until: over }
        },
      },
    })
    renderCode()
    expect(await screen.findByRole('heading', { name: 'What should we work on?' })).toBeInTheDocument()
    await ask('Change two to 2')

    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/messages')?.body).toEqual({ text: 'Change two to 2' })
    // The events come in at the next frame, together: the question then has the server's id.
    expect(await screen.findByText('Thought for 1.2 s')).toBeInTheDocument()
    const answer = screen.getByRole('region', { name: 'Answer' })
    expect(within(answer).getByText('Edit file')).toBeInTheDocument()
    const changes = within(answer).getByRole('figure', { name: 'Changes to src/a.txt' })
    expect(within(changes).getByText('two')).toBeInTheDocument()
    expect(within(changes).getByText('Removed:')).toBeInTheDocument()
    expect(within(changes).getByText('+1')).toBeInTheDocument()
    expect(within(answer).getByText('two', { selector: 'strong' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Stop' })).toBeInTheDocument()

    end()
    // Over: the saved turn, with its model and tokens and the share read from the cache.
    expect(await screen.findByText(/2\.1 K in \(71% cached\) · 30 out/)).toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'Answer' })).getByText('model-a')).toBeInTheDocument()
    expect(screen.getByRole('figure', { name: 'Changes to src/a.txt' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Send' })).toBeInTheDocument()
  })

  it('asks before a tool runs, as Arena does: Allow, Always for this session or Deny; and Stop stops the turn', async () => {
    let end!: () => void
    const over = new Promise<void>((r) => (end = r))
    const command = { id: 'c1', function: { name: 'run_shell', arguments: '{"command":"npm test"}' } }
    const { calls } = backend({
      extra: {
        'POST /api/messages': () => ({
          events: [
            { type: 'question', id: 'm0', parentId: null },
            { type: 'assistant', id: 'm1', parentId: 'm0', model: 'model-a' },
            { type: 'usage', prompt: 900, cached: 0, completion: 12, thinkingMs: null, durationMs: 400 },
            { type: 'tool_call', id: 'c1', name: 'run_shell', arguments: command.function.arguments, tool: 'local' },
            { type: 'approval', id: 'c1', name: 'run_shell', arguments: command.function.arguments, tool: 'local', title: 'run_shell', always: 'for `npm test …`' },
          ],
          until: over,
        }),
      },
    })
    renderCode()
    await ask('Run the tests')

    const question = await screen.findByRole('alert')
    expect(question).toHaveTextContent('Allow Run shell to run with these arguments?')
    expect(question).toHaveTextContent('Always for this session: no more questions for `npm test …`.')
    expect(within(question).getByRole('button', { name: 'Allow' })).toBeInTheDocument()
    expect(within(question).getByRole('button', { name: 'Deny' })).toBeInTheDocument()
    expect(screen.getByText('Waiting for you')).toBeInTheDocument()
    await userEvent.click(within(question).getByRole('button', { name: 'Always for this session' }))
    expect(calls.find((c) => c.path === '/api/approvals')?.body).toEqual({ id: 'c1', answer: 'always' })
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument())

    await userEvent.click(screen.getByRole('button', { name: 'Stop' }))
    expect(calls.some((c) => c.method === 'POST' && c.path === '/api/stop')).toBe(true)
    end()
    expect(await screen.findByRole('button', { name: 'Send' })).toBeInTheDocument()
  })

  it('queues a message typed while an answer is written, shows it above the box, and clears the queue', async () => {
    let end!: () => void
    const over = new Promise<void>((r) => (end = r))
    const { calls, now } = backend({
      extra: {
        'POST /api/messages': () => {
          now.state = { ...now.state, busy: true }
          return { events: turn.slice(0, 4), until: over }
        },
        'POST /api/queue': (body) => {
          now.state = { ...now.state, queued: [...now.state.queued, (body as { text: string }).text] }
          return { status: 202, json: { queued: now.state.queued.length } }
        },
        'DELETE /api/queue': () => {
          now.state = { ...now.state, queued: [] }
          return { json: now.state }
        },
      },
    })
    renderCode()
    await ask('Change two to 2')
    expect(await screen.findByRole('button', { name: 'Stop' })).toBeInTheDocument()

    // The box still takes the next message while the answer is written: it is queued, not refused.
    await ask('Then run the tests')
    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/queue')?.body).toEqual({ text: 'Then run the tests' })
    const queued = await screen.findByRole('region', { name: 'Queued messages' })
    expect(within(queued).getByText('Then run the tests')).toBeInTheDocument()
    expect(calls.filter((c) => c.method === 'POST' && c.path === '/api/messages')).toHaveLength(1)

    await userEvent.click(within(queued).getByRole('button', { name: 'Clear' }))
    // Sent as JSON (code-arena web refuses any other call that changes something).
    expect(calls.find((c) => c.method === 'DELETE' && c.path === '/api/queue')?.body).toEqual({})
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Queued messages' })).not.toBeInTheDocument())
    now.state = { ...now.state, busy: false }
    end()
  })

  it('offers no "Always" for stopping a command: the person is asked each time', async () => {
    let end!: () => void
    const over = new Promise<void>((r) => (end = r))
    const { calls } = backend({
      state: { mode: 'yolo' },
      extra: {
        'POST /api/messages': () => ({
          events: [
            { type: 'question', id: 'm0', parentId: null },
            { type: 'assistant', id: 'm1', parentId: 'm0', model: 'model-a' },
            { type: 'tool_call', id: 'c1', name: 'stop_command', arguments: '{"job":1}', tool: 'local' },
            { type: 'approval', id: 'c1', name: 'stop_command', arguments: '{"job":1}', tool: 'local', title: 'stop_command', always: null },
          ],
          until: over,
        }),
      },
    })
    renderCode()
    await ask('Stop the build')

    const question = await screen.findByRole('alert')
    expect(question).toHaveTextContent('Allow Stop command to run with these arguments?')
    expect(question).not.toHaveTextContent('Always for this session')
    expect(within(question).queryByRole('button', { name: 'Always for this session' })).not.toBeInTheDocument()
    await userEvent.click(within(question).getByRole('button', { name: 'Allow' }))
    expect(calls.find((c) => c.path === '/api/approvals')?.body).toEqual({ id: 'c1', answer: 'allow' })
    end()
    expect(await screen.findByRole('button', { name: 'Send' })).toBeInTheDocument()
  })

  it('shows what Laya made of a command it asks about, even in yolo', async () => {
    let end!: () => void
    const over = new Promise<void>((r) => (end = r))
    const args = '{"command":"rm -rf ~/projects"}'
    const risk = 'Laya says this command may be destructive (96%) and write outside the workspace (93%), so this asks although the mode would run it. Laya: destructive 96%, outside the workspace 93%, network 3%'
    const { calls } = backend({
      state: { mode: 'yolo' },
      extra: {
        'POST /api/messages': () => ({
          events: [
            { type: 'question', id: 'm0', parentId: null },
            { type: 'assistant', id: 'm1', parentId: 'm0', model: 'model-a' },
            { type: 'tool_call', id: 'c1', name: 'run_shell', arguments: args, tool: 'local' },
            { type: 'approval', id: 'c1', name: 'run_shell', arguments: args, tool: 'local', title: 'run_shell', always: 'for `rm …`', risk },
          ],
          until: over,
        }),
      },
    })
    renderCode()
    await ask('Clean up my projects')

    const question = await screen.findByRole('alert')
    expect(question).toHaveTextContent('Allow Run shell to run with these arguments?')
    expect(question).toHaveTextContent('may be destructive (96%) and write outside the workspace (93%)')
    expect(question).toHaveTextContent('Laya: destructive 96%, outside the workspace 93%, network 3%')
    await userEvent.click(within(question).getByRole('button', { name: 'Deny' }))
    expect(calls.find((c) => c.path === '/api/approvals')?.body).toEqual({ id: 'c1', answer: 'deny' })
    end()
    expect(await screen.findByRole('button', { name: 'Send' })).toBeInTheDocument()
  })

  it('switches the mode, the model and the thinking for the next turns', async () => {
    const { calls } = backend()
    renderCode()

    const mode = await screen.findByRole('combobox', { name: 'Mode' })
    expect(mode).toHaveTextContent('Ask')
    await userEvent.click(mode)
    await userEvent.click(await screen.findByRole('option', { name: /Auto-edit/ }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/settings')?.body).toEqual({ mode: 'auto-edit' }))
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Mode' })).toHaveTextContent('Auto-edit'))

    await userEvent.click(screen.getByRole('button', { name: 'Model: model-a' }))
    await userEvent.click(await screen.findByRole('menuitem', { name: /model-b/ }))
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/settings').map((c) => c.body)).toContainEqual({ model: 'model-b' }))
    expect(await screen.findByRole('button', { name: 'Model: model-b' })).toBeInTheDocument()
  })

  it("turns Arena's tools on and off for the session with the web chat's Tools menu", async () => {
    const group = (id: string, title: string, on: boolean) => ({ id, title, description: `${title}'s tools.`, icon: 'globe', onByDefault: true, askFirst: false, on, loaded: false, functions: 2 })
    const { calls } = backend({
      state: { toolGroups: [group('web', 'Web search', true), group('gitlab', 'GitLab', true)] },
      extra: {
        'POST /api/settings': (body) => ({ json: state({ toolGroups: [group('web', 'Web search', true), group('gitlab', 'GitLab', (body as { tools: string[] }).tools.includes('gitlab'))] }) }),
      },
    })
    renderCode()
    await userEvent.click(await screen.findByRole('button', { name: 'Tools: 2 of 2 on' }))
    await userEvent.click(await screen.findByRole('switch', { name: /GitLab/ }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/settings')?.body).toEqual({ tools: ['web'] }))
    expect(await screen.findByRole('button', { name: 'Tools: 1 of 2 on' })).toBeInTheDocument()
  })

  it('attaches files as the web chat does, when Arena takes them: shown above the box, sent with the message and shown with the question', async () => {
    let end!: () => void
    const over = new Promise<void>((r) => (end = r))
    const { calls, now } = backend({
      extra: {
        'GET /api/chat/config': () => ({ json: { ...config, maxUploadBytes: 1_000_000 } }),
        'POST /api/messages': () => {
          now.state = { ...now.state, busy: true }
          return { events: turn.slice(0, 1), until: over }
        },
      },
    })
    renderCode()
    await userEvent.upload(await screen.findByLabelText('Attach files'), new File(['42'], 'numbers.txt', { type: 'text/plain' }))
    const going = await screen.findByRole('list', { name: 'Files to send' })
    expect(within(going).getByText('numbers.txt')).toBeInTheDocument()
    await ask('What do they say?')
    await waitFor(() =>
      expect(calls.find((c) => c.path === '/api/messages')?.body).toEqual({
        text: 'What do they say?',
        attachments: [{ id: 'att-numbers.txt', fileName: 'numbers.txt', size: 2, truncated: false, kind: 'text', contentType: 'text/plain' }],
      }),
    )
    // Sent: the box is empty of them, and the question shows them.
    await waitFor(() => expect(screen.queryByRole('list', { name: 'Files to send' })).not.toBeInTheDocument())
    expect(await screen.findByText('numbers.txt')).toBeInTheDocument()
    now.state = { ...now.state, busy: false }
    end()
  })

  it('offers no files without Arena to take them', async () => {
    backend()
    renderCode()
    await screen.findByRole('textbox', { name: 'Message' })
    expect(screen.queryByRole('button', { name: 'Attach' })).not.toBeInTheDocument()
  })

  it("lists this folder's sessions, newest first, and resumes one with its history", async () => {
    const first: CodeSession = { id: 's1', messages: [msg('m0', 'user', { content: 'First question' }), msg('m1', 'assistant', { content: 'The first answer.', model: 'model-a' })], diffs: {}, busy: false }
    const { calls, now } = backend({
      extra: {
        'POST /api/sessions/resume': () => {
          now.state = { ...now.state, session: 's1' }
          now.session = first
          return { json: first }
        },
      },
    })
    renderCode()

    // The sessions are the side bar's Chat view.
    await userEvent.click(await screen.findByRole('button', { name: 'Chat' }))
    const list = await screen.findByRole('navigation', { name: 'Sessions' })
    const items = await within(list).findAllByRole('button', { name: /Fix the build|First question/ })
    expect(items.map((b) => b.textContent)).toEqual(['Fix the build', 'First question'])
    expect(items[0]).toHaveAttribute('aria-current', 'page')
    expect(within(list).getByText('Today')).toBeInTheDocument()

    await userEvent.click(items[1]!)
    expect(calls.find((c) => c.path === '/api/sessions/resume')?.body).toEqual({ id: 's1' })
    expect(await screen.findByText('The first answer.')).toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'You' })).getByText('First question')).toBeInTheDocument()
    await waitFor(() => expect(within(list).getByRole('button', { name: 'First question' })).toHaveAttribute('aria-current', 'page'))
  })

  it("lists Arena's chats below the sessions, opens one, sends the sessions Arena lacks, and reads the session again when the web adds to it", async () => {
    const at = new Date().toISOString()
    const opened: CodeSession = {
      id: 's3', diffs: {}, busy: false,
      messages: [msg('m0', 'user', { content: 'Plan the release' }), msg('m1', 'assistant', { content: 'Step one: freeze.', model: 'model-a' })],
    }
    const { calls, now } = backend({
      state: { arenaChats: true, synced: 0 },
      extra: {
        'GET /api/sessions': () => ({ json: [{ ...sessions[0]!, chat: 'c-linked' }, sessions[1]!] }),
        'GET /api/sessions/web': () => ({
          json: [
            { id: 'c-linked', title: 'Fix the build', origin: 'code-arena', place: '/home/ada/shop', updatedAt: at, messages: 4, session: 's2' },
            { id: 'c-web', title: 'Plan the release', origin: null, place: null, updatedAt: at, messages: 2, session: null },
            { id: 'c-other', title: 'Refactor the parser', origin: 'code-arena', place: '/home/ada/parser', updatedAt: at, messages: 9, session: null },
          ],
        }),
        'POST /api/sessions/web': () => {
          now.state = { ...now.state, session: 's3', chat: 'c-web' }
          now.session = opened
          return { json: opened }
        },
        'POST /api/sessions/send-all': () => ({ json: { sent: 2, failed: [] } }),
      },
    })
    renderCode()
    await userEvent.click(await screen.findByRole('button', { name: 'Chat' }))
    const list = await screen.findByRole('navigation', { name: 'Sessions' })
    // The session kept with its chat in Arena says so.
    expect(await within(list).findByRole('button', { name: /^Fix the build\s*\(kept in step with its chat in Arena\)$/ })).toBeInTheDocument()

    // Arena's other chats, on asking: the one kept with a session here is listed above, as that session.
    await userEvent.click(within(list).getByRole('button', { name: 'Chats in Arena' }))
    const arena = await within(list).findByRole('list', { name: 'Chats in Arena' })
    await waitFor(() => expect(within(arena).getAllByRole('button').map((b) => b.textContent)).toEqual(['Plan the release', 'Code Arena: Refactor the parser']))
    await userEvent.type(within(list).getByRole('textbox', { name: 'Find a chat in Arena' }), 'parser')
    expect(within(arena).getAllByRole('button').map((b) => b.textContent)).toEqual(['Code Arena: Refactor the parser'])
    await userEvent.clear(within(list).getByRole('textbox', { name: 'Find a chat in Arena' }))

    await userEvent.click(within(arena).getByRole('button', { name: 'Plan the release' }))
    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/sessions/web')?.body).toEqual({ id: 'c-web' })
    expect(await screen.findByText('Step one: freeze.')).toBeInTheDocument()

    await userEvent.click(within(list).getByRole('button', { name: /Send the other sessions here/ }))
    expect(await screen.findByText('Sent to Arena: 2 sessions.')).toBeInTheDocument()

    // The web adds to the chat while nothing runs: code-arena takes it in, and the thread reads the session again.
    now.session = { ...opened, messages: [...opened.messages, msg('m2', 'user', { content: 'asked on the web' }), msg('m3', 'assistant', { content: 'answered on the web', model: 'model-a' })] }
    now.state = { ...now.state, synced: 1 }
    expect(await screen.findByText('answered on the web', {}, { timeout: 8000 })).toBeInTheDocument()
    expect(calls.filter((c) => c.path === '/api/session').length).toBeGreaterThan(1)
  }, 15_000)

  it('shows where the sync with Arena stands, what was said while nothing ran, and the sessions Arena lacks', async () => {
    const { now } = backend({
      state: {
        arenaChats: true,
        chat: 'c1',
        sync: { chat: 'c1', account: 'Pat (pat@example.test)', waiting: 2, trouble: null, behind: false },
        notices: [{ id: 1, text: 'Taken in from the web chat: 2 messages.', at: new Date().toISOString() }],
      },
      extra: { 'GET /api/sessions': () => ({ json: [sessions[0]!, { ...sessions[1]!, unsent: true, waiting: 3 }] }) },
    })
    renderCode()
    const chat = await screen.findByRole('region', { name: 'Chat' })
    expect(await within(chat).findByText('2 to send')).toBeInTheDocument()
    expect(within(chat).getByLabelText(/Arena: Kept in step with its chat in Arena \(Pat \(pat@example.test\)'s chats\)\. 2 messages still to send\./)).toBeInTheDocument()
    expect(await screen.findByText('Taken in from the web chat: 2 messages.')).toBeInTheDocument()

    // Arena out of reach now: said in the header, and the notice raised meanwhile comes once.
    now.state = {
      ...now.state,
      sync: { chat: 'c1', account: null, waiting: 2, trouble: 'Arena cannot be reached', behind: false },
      notices: [...(now.state.notices ?? []), { id: 2, text: 'Chats are not in step with Arena now: Arena cannot be reached.', at: new Date().toISOString() }],
    }
    expect(await within(chat).findByText('Not in step', {}, { timeout: 8000 })).toBeInTheDocument()
    expect(await screen.findByText('Chats are not in step with Arena now: Arena cannot be reached.')).toBeInTheDocument()

    await userEvent.click(await screen.findByRole('button', { name: 'Chat' }))
    const list = await screen.findByRole('navigation', { name: 'Sessions' })
    expect(await within(list).findByText('not sent')).toHaveAttribute('title', 'Arena lacks 3 of its messages')
  }, 15_000)

  it("brings back with ↑ this session's messages, then what this folder sent before, and comes back to the draft", async () => {
    const session: CodeSession = { id: 's2', messages: [msg('m0', 'user', { content: 'Run the tests' }), msg('m1', 'assistant', { content: 'They pass.', model: 'model-a' })], diffs: {}, busy: false }
    const { calls } = backend({ session, extra: { 'GET /api/history': () => ({ json: [{ text: 'Run the tests' }, { text: 'Fix the build\nand say why' }] }) } })
    renderCode()
    expect(await screen.findByText('They pass.')).toBeInTheDocument()
    const box = screen.getByRole('textbox', { name: 'Message' }) as HTMLTextAreaElement
    await userEvent.type(box, 'half')
    await userEvent.keyboard('{ArrowUp}')
    expect(box).toHaveValue('Run the tests')
    expect(calls.some((c) => c.path === '/api/history')).toBe(false)
    // This session has no more: the folder's, the terminal's included, each once.
    await userEvent.keyboard('{ArrowUp}')
    await waitFor(() => expect(box).toHaveValue('Fix the build\nand say why'))
    await userEvent.keyboard('{ArrowDown}{ArrowDown}')
    expect(box).toHaveValue('half')
    await userEvent.keyboard('{ArrowUp}{Escape}')
    expect(box).toHaveValue('half')
  })

  it('shows a command with no time limit as it runs: the end of its output, and Stop; the status bar counts it', async () => {
    let end!: () => void
    const over = new Promise<void>((r) => (end = r))
    const { calls, now } = backend({
      state: { jobs: [{ id: 1, command: 'npm test', running: true, status: 'running for 3s' }] },
      extra: {
        'POST /api/messages': () => ({
          events: [
            { type: 'question', id: 'm0', parentId: null },
            { type: 'assistant', id: 'm1', parentId: 'm0', model: 'model-a' },
            { type: 'tool_call', id: 'c1', name: 'run_shell', arguments: '{"command":"npm test","no_time_limit":true}', tool: 'local' },
            { type: 'job', job: 1, command: 'npm test', running: true, status: 'running for 0s' },
            { type: 'tool_result', id: 'c1', messageId: 'm2', name: 'run_shell', text: 'Started as job 1, with no time limit.', isError: false, declined: false, noAccess: false, durationMs: 5 },
            { type: 'job_output', job: 1, text: 'PASS cart.test.ts\n' },
            { type: 'job_output', job: 1, text: 'RUNS checkout.test.ts\n' },
          ],
          until: over,
        }),
        'POST /api/jobs/stop': () => ({ status: 204 }),
      },
    })
    renderCode()
    await ask('Run the tests')

    const jobs = await screen.findByRole('region', { name: 'Commands with no time limit' })
    await waitFor(() => expect(within(jobs).getByLabelText('Output of job 1')).toHaveTextContent(/PASS cart\.test\.ts\s+RUNS checkout\.test\.ts/))
    expect(within(jobs).getByText('npm test')).toBeInTheDocument()
    expect(within(jobs).getByText('running, no time limit')).toBeInTheDocument()
    const status = screen.getByRole('contentinfo', { name: 'Status bar' })
    expect(within(status).getByRole('button', { name: '1 command running with no time limit: show them in the chat' })).toBeInTheDocument()

    // Folded and opened; Stop asks code-arena to end it (the agent is told).
    await userEvent.click(within(jobs).getByRole('button', { name: 'job 1: npm test' }))
    expect(within(jobs).queryByLabelText('Output of job 1')).not.toBeInTheDocument()
    await userEvent.click(within(jobs).getByRole('button', { name: 'Stop job 1' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/jobs/stop')?.body).toEqual({ id: 1 }))
    expect(within(jobs).getByRole('button', { name: 'Stop job 1' })).toBeDisabled()

    now.state = { ...now.state, jobs: [{ id: 1, command: 'npm test', running: false, status: 'stopped (by the person, in the IDE) after 4s' }] }
    end()
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Commands with no time limit' })).not.toBeInTheDocument())
  })

  it('shows a command still running that no turn here watches, its output from the state, with its Stop', async () => {
    const { calls, now } = backend({
      state: { jobs: [{ id: 3, command: 'npm run build', running: true, status: 'running for 2m 10s', output: 'compiling 41 of 90\n' }] },
      extra: {
        'POST /api/jobs/stop': () => {
          now.state = { ...now.state, jobs: [{ id: 3, command: 'npm run build', running: false, status: 'stopped (by the person, in the IDE) after 2m 12s' }] }
          return { status: 204 }
        },
      },
    })
    renderCode()
    const jobs = await screen.findByRole('region', { name: 'Commands with no time limit' })
    expect(within(jobs).getByText('npm run build')).toBeInTheDocument()
    expect(within(jobs).getByLabelText('Output of job 3')).toHaveTextContent('compiling 41 of 90')
    await userEvent.click(within(jobs).getByRole('button', { name: 'Stop job 3' }))
    expect(calls.find((c) => c.path === '/api/jobs/stop')?.body).toEqual({ id: 3 })
    // The state read again: it has ended, and its box goes.
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Commands with no time limit' })).not.toBeInTheDocument())
  })

  it('shows the question of a turn it did not start (a queued message), and a job in the background as such', () => {
    let live = fromSession({ id: 's2', messages: [msg('m0', 'user', { content: 'first' }), msg('m1', 'assistant', { content: 'done' })], diffs: {}, busy: true })
    live = reduceCode(live, { type: 'question', id: 'm2', parentId: 'm1', text: 'then run the tests' } as never, null)
    expect(live.messages.at(-1)).toMatchObject({ id: 'm2', role: 'user', content: 'then run the tests', parentId: 'm1' })
    live = reduceCode(live, { type: 'job', job: 4, command: 'npm run dev', running: true, status: 'running for 0s', background: true }, null)
    expect(live.jobs[0]).toMatchObject({ id: 4, background: true })
  })

  it("keeps the end of a command's output, and how it ended", () => {
    let live = reduceCode(fromSession(undefined), { type: 'job', job: 2, command: 'make', running: true, status: 'running for 0s' }, null)
    live = reduceCode(live, { type: 'job_output', job: 2, text: 'x'.repeat(keptOutput) }, null)
    live = reduceCode(live, { type: 'job_output', job: 2, text: 'the end\n' }, null)
    live = reduceCode(live, { type: 'job_output', job: 7, text: 'no such job' }, null)
    expect(live.jobs).toHaveLength(1)
    expect(live.jobs[0]!.output).toHaveLength(keptOutput)
    expect(live.jobs[0]!.output.endsWith('xthe end\n')).toBe(true)
    live = reduceCode(live, { type: 'job_end', job: 2, running: false, status: 'exit code 2 after 1m 03s', exitCode: 2, stopped: false }, null)
    expect(live.jobs[0]).toMatchObject({ running: false, status: 'exit code 2 after 1m 03s', failed: true })
    const ok = reduceCode(reduceCode(fromSession(undefined), { type: 'job', job: 1, command: 'ls', running: true, status: '' }, null), { type: 'job_end', job: 1, running: false, status: 'exit code 0 after 0s', exitCode: 0, stopped: false }, null)
    expect(ok.jobs[0]!.failed).toBe(false)
  })

  it("draws a long session's last turns, and the earlier ones when asked for", async () => {
    const long: Message[] = Array.from({ length: 35 }, (_, i) => [
      msg(`m${2 * i}`, 'user', { content: `Question ${i + 1}` }),
      msg(`m${2 * i + 1}`, 'assistant', { content: `Answer ${i + 1}.`, model: 'model-a' }),
    ]).flat()
    backend({ session: { id: 's2', messages: long, diffs: {}, busy: false } })
    renderCode()
    expect(await screen.findByText('Answer 35.')).toBeInTheDocument()
    expect(screen.getAllByRole('region', { name: 'You' })).toHaveLength(30)
    expect(screen.queryByText('Answer 5.')).not.toBeInTheDocument()
    expect(screen.getByText('Answer 6.')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Show earlier (5 more turns)' }))
    expect(screen.getAllByRole('region', { name: 'You' })).toHaveLength(35)
    expect(screen.getByText('Answer 1.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Show earlier/ })).not.toBeInTheDocument()
  })

  it('does not draw the finished turns again while a turn streams, nor when the session is read back after it', async () => {
    const read = (n: number) => ({ id: `r${n}`, function: { name: 'read_file', arguments: `{"path":"old-${n}.ts"}` } })
    const earlier: Message[] = Array.from({ length: 5 }, (_, i) => [
      msg(`m${3 * i}`, 'user', { content: `Read old-${i}.ts` }),
      msg(`m${3 * i + 1}`, 'assistant', { model: 'model-a', toolCalls: [read(i)] }),
      msg(`m${3 * i + 2}`, 'tool', { content: 'one\ntwo', toolCallId: `r${i}`, toolName: 'read_file' }),
    ]).flat()
    let end!: () => void
    const over = new Promise<void>((r) => (end = r))
    const answer = 'All five files are read, and nothing in them needs a change.'
    const { calls, now } = backend({
      session: { id: 's2', messages: earlier, diffs: {}, busy: false },
      extra: {
        'POST /api/messages': () => {
          // Read back after the turn: the same earlier messages, as new objects from the server.
          now.session = {
            id: 's2', diffs: {}, busy: false,
            messages: [...structuredClone(earlier), msg('m15', 'user', { content: 'Anything else?' }), msg('m16', 'assistant', { content: answer, model: 'model-a' })],
          }
          return {
            events: [
              { type: 'question', id: 'm15', parentId: 'm14' },
              { type: 'assistant', id: 'm16', parentId: 'm15', model: 'model-a' },
              ...answer.split(' ').map((w, i) => ({ type: 'content', text: i === 0 ? w : ` ${w}` })),
            ],
            until: over,
          }
        },
      },
    })
    renderCode()
    expect(await screen.findAllByText('Read file')).toHaveLength(5)
    const summary = vi.mocked(argsSummary)
    const earlierCards = () => summary.mock.calls.filter(([args]) => args.includes('old-')).length
    const atFirst = earlierCards()
    expect(atFirst).toBeGreaterThan(0)

    await ask('Anything else?')
    expect(await screen.findByText(answer)).toBeInTheDocument()
    end()
    // Over, and the session read back: its answer is the saved one, with the model under it.
    await waitFor(() => expect(within(screen.getAllByRole('region', { name: 'Answer' }).at(-1)!).getByText('model-a')).toBeInTheDocument())
    expect(calls.filter((c) => c.path === '/api/session').length).toBeGreaterThan(1)
    expect(earlierCards()).toBe(atFirst)
  })

  it("reads the session list again only when it changed: this session's first turn, not each turn", async () => {
    const { calls } = backend({
      session: { id: 's2', messages: saved, diffs: { m2: diff }, busy: false },
      extra: { 'POST /api/messages': () => ({ events: [{ type: 'question', id: 'm4', parentId: 'm3' }, { type: 'assistant', id: 'm5', parentId: 'm4', model: 'model-a' }, { type: 'content', text: 'Done.' }] }) },
    })
    renderCode()
    await userEvent.click(await screen.findByRole('button', { name: 'Chat' }))
    const list = await screen.findByRole('navigation', { name: 'Sessions' })
    await within(list).findByRole('button', { name: 'First question' })
    const listed = () => calls.filter((c) => c.method === 'GET' && c.path === '/api/sessions').length
    const before = listed()
    await ask('And the tests?')
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/session').length).toBeGreaterThan(1))
    await screen.findByRole('button', { name: 'Send' })
    expect(listed()).toBe(before)
    // Only moved to the top, as changed now.
    expect(within(list).getAllByRole('button', { name: /Fix the build|First question/ }).map((b) => b.textContent)).toEqual(['Fix the build', 'First question'])
  })

  it('reads the session list again after the first turn of a session it does not have yet', async () => {
    const fresh = backend({ state: { session: 's3' }, extra: { 'POST /api/messages': () => ({ events: [{ type: 'question', id: 'm0', parentId: null }, { type: 'assistant', id: 'm1', parentId: 'm0', model: 'model-a' }, { type: 'content', text: 'Hi.' }] }) } })
    renderCode()
    await userEvent.click(await screen.findByRole('button', { name: 'Chat' }))
    await within(await screen.findByRole('navigation', { name: 'Sessions' })).findByRole('button', { name: 'First question' })
    const at = fresh.calls.filter((c) => c.method === 'GET' && c.path === '/api/sessions').length
    await ask('Hello')
    await waitFor(() => expect(fresh.calls.filter((c) => c.method === 'GET' && c.path === '/api/sessions').length).toBe(at + 1))
  })

  it('shows the pictures and plays the sounds and videos that Arena\'s tools made in the folder', async () => {
    const draw = { id: 'd1', function: { name: 'Arena__draw_picture', arguments: '{"prompt":"a red bicycle"}' } }
    const speak = { id: 'd2', function: { name: 'Arena__speak', arguments: '{"text":"hello"}' } }
    const made: Message[] = [
      msg('m0', 'user', { content: 'Draw a bicycle and say hello' }),
      msg('m1', 'assistant', { model: 'model-a', toolCalls: [draw, speak] }),
      msg('m2', 'tool', { content: 'The picture is saved as .code-arena/media/draw_picture-20261016-120000.png.', toolCallId: 'd1', toolName: 'Arena__draw_picture' }),
      msg('m3', 'tool', { content: 'Saved /home/ada/shop/.code-arena/media/speak-20261016-120001.mp3 and .code-arena/media/clip-1.mp4 (and ../.code-arena/media/../../etc/passwd.png is not one).', toolCallId: 'd2', toolName: 'Arena__speak' }),
      msg('m4', 'assistant', { model: 'model-a', content: 'Here they are.' }),
    ]
    backend({ session: { id: 's2', messages: made, diffs: {}, busy: false } })
    renderCode()
    const picture = await screen.findByRole('img', { name: 'draw_picture-20261016-120000.png' })
    expect(picture).toHaveAttribute('src', '/api/media?path=.code-arena%2Fmedia%2Fdraw_picture-20261016-120000.png')
    const sound = screen.getByLabelText('speak-20261016-120001.mp3')
    expect(sound.tagName).toBe('AUDIO')
    expect(sound).toHaveAttribute('src', '/api/media?path=.code-arena%2Fmedia%2Fspeak-20261016-120001.mp3')
    expect(screen.getByLabelText('clip-1.mp4').tagName).toBe('VIDEO')
    expect(screen.queryByRole('img', { name: /passwd/ })).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'View draw_picture-20261016-120000.png' }))
    expect(await screen.findByRole('dialog')).toBeInTheDocument()
  })

  it('says so when the page holds the key of an earlier run', async () => {
    fakeApi(null, { 'GET /api/state': () => ({ status: 401, json: { status: 'unauthorized', error: 'Open the address' } }), 'GET /api/chat/config': () => ({ status: 401 }) })
    renderCode()
    expect(await screen.findByText('This page is from an earlier run')).toBeInTheDocument()
  })

  it("says where the manual is when code-arena knows no Arena's address to link to", async () => {
    backend({ state: { manual: null } })
    renderCode()
    await userEvent.click(await screen.findByRole('button', { name: 'Help' }))
    const sheet = await screen.findByRole('dialog', { name: 'Code Arena' })
    expect(within(sheet).queryByRole('link')).not.toBeInTheDocument()
    expect(within(sheet).getByText(/in your Argus Arena's manual: Manual, then Code Arena/)).toBeInTheDocument()
  })

  it("quotes part of an answer as the web chat does: the toolbar, the command (listed only with a selection) and Ctrl+Shift+'", async () => {
    const answered = [msg('m0', 'user', { content: 'How do I tidy commits?' }), msg('m1', 'assistant', { model: 'model-a', content: 'Use git rebase to squash commits.' })]
    const server = backend({
      session: { id: 's2', messages: answered, diffs: {}, busy: false },
      extra: {
        'POST /api/messages': () => {
          // Read back after the turn: the question with its quote apart, as code-arena web gives it.
          server.now.session = { ...server.now.session, messages: [...answered, msg('m2', 'user', { content: 'Why?', quote: 'git rebase to squash' })] }
          return { events: [{ type: 'question', id: 'm2', parentId: 'm1', text: 'Why?', quote: 'git rebase to squash' }, { type: 'done' }] }
        },
      },
    })
    const calls = server.calls
    renderCode()
    const answer = await screen.findByRole('region', { name: 'Answer' })
    const command = () => getCommand('codeArena.chat.quoteSelection')
    await waitFor(() => expect(command()).toBeDefined())
    expect(command()!.enabled!()).toBe(false)

    const words = within(answer).getByText('Use git rebase to squash commits.').firstChild!
    const range = document.createRange()
    range.setStart(words, 4)
    range.setEnd(words, 24)
    document.getSelection()!.removeAllRanges()
    document.getSelection()!.addRange(range)
    document.dispatchEvent(new Event('selectionchange'))
    const bar = await screen.findByRole('toolbar', { name: 'The selected words' })
    expect(within(bar).getByRole('button', { name: /Reply/ })).toHaveAttribute('title', "Reply quoting this (Ctrl+Shift+')")
    expect(command()!.enabled!()).toBe(true)

    await userEvent.keyboard("{Control>}{Shift>}'{/Shift}{/Control}")
    expect(await screen.findByRole('group', { name: 'Quoted in your reply' })).toHaveTextContent('git rebase to squash')
    await ask('Why?')
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/messages')?.body).toEqual({ text: 'Why?', quote: 'git rebase to squash' }))
    expect(screen.queryByRole('group', { name: 'Quoted in your reply' })).not.toBeInTheDocument()
    expect(await screen.findByRole('figure', { name: 'Quoting an earlier answer' })).toHaveTextContent('git rebase to squash')
  })

  it('shows the quote of a question from a turn it did not start (a queued message)', () => {
    let live = fromSession({ id: 's2', messages: [msg('m0', 'user', { content: 'first' }), msg('m1', 'assistant', { content: 'done' })], diffs: {}, busy: true })
    live = reduceCode(live, { type: 'question', id: 'm2', parentId: 'm1', text: 'why?', quote: 'done' } as never, null)
    expect(live.messages.at(-1)).toMatchObject({ id: 'm2', content: 'why?', quote: 'done' })
  })
})
