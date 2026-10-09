import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { makeQueryClient, Providers } from '@/app/providers'
import { blank } from '@/pages/chat/live'
import type { ChatConfig, Message } from '@/pages/chat/types'
import { fakeApi, type Handler } from '@/test/utils'
import { fromSession, keptOutput, reduceCode, type CodeSession, type CodeState, type FileDiff, type SessionSummary } from './api'
import { App } from './app'

const state = (over: Partial<CodeState> = {}): CodeState => ({
  name: 'Code Arena', version: '5.0.0', license: 'AGPL-3.0-only', source: 'https://github.com/Binchitects/argus', manual: 'https://llm.test/help/code-arena', folder: '/home/ada/shop', project: 'shop', branch: 'main', model: 'model-a', context: 32768, contextUsed: 4200, compactAt: 80, compactTarget: 25, servers: [], jobs: [], thinking: null, mode: 'ask',
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
    const answer = await screen.findByRole('region', { name: 'Answer' })
    expect(await within(answer).findByText('Thought for 1.2 s')).toBeInTheDocument()
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

  it('shows a command still running that no turn here watches, from the state, with its Stop', async () => {
    const { calls, now } = backend({
      state: { jobs: [{ id: 3, command: 'npm run build', running: true, status: 'running for 2m 10s' }] },
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
    expect(within(jobs).getByLabelText('Output of job 3')).toHaveTextContent('Its output is not streamed here: the agent reads it with command_output.')
    await userEvent.click(within(jobs).getByRole('button', { name: 'Stop job 3' }))
    expect(calls.find((c) => c.path === '/api/jobs/stop')?.body).toEqual({ id: 3 })
    // The state read again: it has ended, and its box goes.
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Commands with no time limit' })).not.toBeInTheDocument())
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
})
