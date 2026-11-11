import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { ReactNode } from 'react'
import { describe, expect, it, vi } from 'vitest'
import { makeQueryClient, Providers } from '@/app/providers'
import type { Message } from '@/pages/chat/types'
import { fakeApi } from '@/test/utils'
import { SubAgentLine } from './answer'
import { GoalBar, GoalDialog, LoopsDialog } from './goal'
import { TasksPanel } from './tasks-panel'
import type { AgentTask, AgentTaskDetail, CodeGoal } from './tasks-api'

function wrap(children: ReactNode) {
  const client = makeQueryClient()
  client.setDefaultOptions({ queries: { ...client.getDefaultOptions().queries, retry: false } })
  const shown = render(<Providers client={client}>{children}</Providers>)
  return { rerender: (next: ReactNode) => shown.rerender(<Providers client={client}>{next}</Providers>) }
}

const task = (over: Partial<AgentTask>): AgentTask => ({
  id: 't1', kind: 'subagent', title: 'find the parser', status: 'running', call: 'call_1', as: null, model: 'qwen', writes: false,
  created: new Date().toISOString(), started: new Date(Date.now() - 4000).toISOString(), ended: null, ms: 4000,
  tokens: { prompt: 9000, cached: 3000, completion: 400 }, calls: 2, doing: 'grep class Parser', error: null, kept: false, ...over,
})

describe('the Tasks panel', () => {
  it('lists the tasks at work above those that ended, each with what it does, its time and tokens, and stops one', async () => {
    const calls = fakeApi(null, {
      'GET /api/agent-tasks': () => ({
        json: {
          tasks: [task({ id: 't1', status: 'done', doing: null, ms: 1200, title: 'read the docs' }), task({ id: 't2' }), task({ id: 't3', kind: 'background', title: 'the build', writes: true, as: 'Reviewer' })],
          running: 2, mostAtOnce: 4,
        },
      }),
      'POST /api/agent-tasks/t2/stop': () => ({ status: 204 }),
    })
    wrap(<TasksPanel selected={null} onSelect={vi.fn()} onClose={vi.fn()} plan={false} />)
    const panel = await screen.findByRole('complementary', { name: 'Tasks' })
    expect(await within(panel).findByText('2 at work')).toBeInTheDocument()
    const working = await within(panel).findByRole('list', { name: 'At work' })
    // Newest first.
    expect(within(working).getAllByRole('button', { name: /: Working$/ }).map((b) => b.getAttribute('aria-label'))).toEqual(['the build: Working', 'find the parser: Working'])
    const row = within(working).getByRole('button', { name: 'find the parser: Working' })
    expect(row).toHaveTextContent(/grep class Parser · 4\.\d s · 9\.4k tokens · 2 tool calls/)
    expect(within(working).getByRole('button', { name: 'the build: Working' })).toHaveTextContent(/yours/)
    expect(within(working).getByRole('button', { name: 'the build: Working' })).toHaveTextContent(/as Reviewer/)
    expect(within(panel).getByRole('list', { name: 'Ended' })).toHaveTextContent('read the docs')
    // An ended one has no Stop.
    expect(within(panel).queryByRole('button', { name: 'Stop read the docs' })).toBeNull()

    await userEvent.click(within(working).getByRole('button', { name: 'Stop find the parser' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/agent-tasks/t2/stop')).toBe(true))
  })

  it('opens a task to its own transcript: what it was asked, its tool calls with their results, and its report', async () => {
    const detail: AgentTaskDetail = {
      ...task({ status: 'done', doing: null, ended: new Date().toISOString() }),
      prompt: 'Find where class Parser is defined.', tools: ['grep', 'read_file'], report: 'Parser is in `src/a.cs:1`.',
      work: { title: 'find the parser', instructions: 'Find where class Parser is defined.', reasoning: '', text: 'Parser is in `src/a.cs:1`.', status: 'done', error: null, ms: 4000,
        steps: [{ id: 'c1', name: 'grep', arguments: '{"pattern":"class Parser"}', result: 'src/a.cs:1: class Parser { }' }] },
    }
    fakeApi(null, {
      'GET /api/agent-tasks': () => ({ json: { tasks: [detail], running: 0, mostAtOnce: 4 } }),
      'GET /api/agent-tasks/call_1': () => ({ json: detail }),
    })
    const onSelect = vi.fn()
    const onClose = vi.fn()
    // The chat's line opens it by its call's id.
    wrap(<TasksPanel selected="call_1" onSelect={onSelect} onClose={onClose} plan={false} />)
    const panel = await screen.findByRole('complementary', { name: 'Tasks' })
    expect(await within(panel).findByRole('region', { name: 'What it was asked' })).toHaveTextContent('Find where class Parser is defined.')
    expect(panel).toHaveTextContent('Tools: grep, read_file')
    expect(within(panel).getByRole('region', { name: 'Its report' })).toHaveTextContent('Parser is in src/a.cs:1.')
    expect(panel).toHaveTextContent(/class Parser/)
    await userEvent.click(within(panel).getByRole('button', { name: 'Back to the tasks' }))
    expect(onSelect).toHaveBeenCalledWith(null)
    // Closing an open task goes back to the list; the panel stays.
    onSelect.mockClear()
    await userEvent.click(within(panel).getByRole('button', { name: 'Close the task' }))
    expect(onSelect).toHaveBeenCalledWith(null)
    expect(onClose).not.toHaveBeenCalled()
  })

  it('starts a task of the persons in the background, as an assistant, changing files only when they say', async () => {
    const calls = fakeApi(null, {
      'GET /api/agent-tasks': () => ({ json: { tasks: [], running: 0, mostAtOnce: 4 } }),
      'GET /api/assistants': () => ({ json: { assistants: [{ id: 'a1', name: 'Reviewer', description: null, instructions: null, model: null, thinking: null, tools: null, skills: [], icon: 'bot', color: 'blue' }], current: null } }),
      'POST /api/agent-tasks': () => ({ status: 201, json: task({ id: 't1', kind: 'background', status: 'queued', title: 'the build' }) }),
    })
    const onSelect = vi.fn()
    wrap(<TasksPanel selected={null} onSelect={onSelect} onClose={vi.fn()} plan={false} />)
    const panel = await screen.findByRole('complementary', { name: 'Tasks' })
    expect(await within(panel).findByText('No tasks in this session yet.')).toBeInTheDocument()
    await userEvent.click(within(panel).getByRole('button', { name: 'New task' }))
    const form = within(panel).getByRole('form', { name: 'New task' })
    const start = within(form).getByRole('button', { name: 'Start' })
    expect(start).toBeDisabled()
    await userEvent.type(within(form).getByRole('textbox', { name: 'What to do' }), 'How is it built?')
    await userEvent.type(within(form).getByRole('textbox', { name: /Its name/ }), 'the build')
    await userEvent.click(within(form).getByRole('switch', { name: 'May change files and run commands' }))
    await userEvent.click(start)
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/agent-tasks')?.body).toEqual({ prompt: 'How is it built?', title: 'the build', edits: true }))
    expect(onSelect).toHaveBeenCalledWith('t1')
  })

  it('keeps a task that changes files from plan mode', async () => {
    fakeApi(null, { 'GET /api/agent-tasks': () => ({ json: { tasks: [], running: 0, mostAtOnce: 4 } }), 'GET /api/assistants': () => ({ json: { assistants: null, current: null } }) })
    wrap(<TasksPanel selected={null} onSelect={vi.fn()} onClose={vi.fn()} plan />)
    await userEvent.click(await screen.findByRole('button', { name: 'New task' }))
    expect(screen.getByRole('switch', { name: 'May change files and run commands' })).toBeDisabled()
    expect(screen.getByText('Not in plan mode.')).toBeInTheDocument()
  })
})

describe('a sub-agent in the chat', () => {
  const result = (content: string): Message => ({ id: 'm3', role: 'tool', content, parentId: 'm2', toolCallId: 'call_1', toolName: 'task', status: 'complete' }) as Message

  it('is one line, never its steps, and opens in the Tasks panel', async () => {
    const onOpen = vi.fn()
    wrap(
      <SubAgentLine
        callId="call_1"
        args='{"description":"find the parser","prompt":"Find it."}'
        result={result('Parser is in src/a.cs:1.\nMore detail.')}
        live={false}
        work={{ title: 'find the parser', instructions: 'Find it.', reasoning: '', text: '', status: 'done', error: null, ms: 4200, model: 'qwen', usage: { prompt: 12000, cached: 0, completion: 300 },
          steps: [{ id: 'c1', name: 'grep', arguments: '{}', result: 'hidden step' }] }}
        onOpen={onOpen}
      />,
    )
    const line = screen.getByRole('button', { name: 'Sub-agent find the parser: Done. Open it in Tasks' })
    expect(line).toHaveTextContent('Parser is in src/a.cs:1. · 4.2 s · 12.3k tokens · qwen')
    expect(screen.queryByText('hidden step')).toBeNull()
    await userEvent.click(line)
    expect(onOpen).toHaveBeenCalledWith('call_1')
  })

  it('says it works while it does, and that it was stopped', () => {
    const { rerender } = wrap(<SubAgentLine callId="call_1" args='{"description":"the lexer"}' live work={{ title: 'the lexer', instructions: '', reasoning: '', text: '', status: 'running', error: null, ms: null, steps: [] }} />)
    expect(screen.getByRole('button', { name: 'Sub-agent the lexer: Working. Open it in Tasks' })).toHaveTextContent('Working…')
    rerender(<SubAgentLine callId="call_1" args='{"description":"the lexer"}' live={false} result={result('The person stopped this sub-agent (the lexer) before it reported.')} />)
    expect(screen.getByRole('button', { name: 'Sub-agent the lexer: Stopped. Open it in Tasks' })).toBeInTheDocument()
  })
})

const goal = (over: Partial<CodeGoal>): CodeGoal => ({
  text: 'The tests pass', check: { kind: 'command', text: 'dotnet test', by: 'person' }, budget: { steps: 200, minutes: 60, tokens: 5_000_000 }, status: 'working',
  turns: 3, steps: 41, tokens: 812_000, ms: 420_000, started: '2026-11-06T10:00:00Z', updated: '2026-11-06T10:07:00Z', passed: false, said: 'Exit code 1.',
  checkedAt: '2026-11-06T10:07:00Z', outcome: null, ...over,
})

describe('the goal', () => {
  it('shows what it spent of its budget and how it is checked, and pauses, resumes and cancels', async () => {
    const calls = fakeApi(null, {
      'POST /api/goal/pause': () => ({ json: { goal: goal({ status: 'paused' }) } }),
      'POST /api/goal/resume': () => ({ json: { goal: goal({}) } }),
      'DELETE /api/goal': () => ({ json: { goal: goal({ status: 'cancelled' }) } }),
    })
    const onChanged = vi.fn()
    const { rerender } = wrap(<GoalBar goal={goal({})} onChanged={onChanged} onDismiss={vi.fn()} />)
    const bar = screen.getByRole('region', { name: /^Goal/ })
    expect(bar).toHaveTextContent('Goal · Working: The tests pass')
    expect(bar).toHaveTextContent('turn 3 · 41 of 200 steps · 7 min 0 s of 60 min · 812k of 5M tokens')
    expect(bar).toHaveTextContent('Check: dotnet test · not passing yet')
    expect(bar).toHaveTextContent('21% of the budget used')
    await userEvent.click(within(bar).getByRole('button', { name: 'Pause the goal' }))
    await waitFor(() => expect(onChanged).toHaveBeenCalled())
    rerender(<GoalBar goal={goal({ status: 'paused' })} onChanged={onChanged} onDismiss={vi.fn()} />)
    await userEvent.click(screen.getByRole('button', { name: 'Resume the goal' }))
    await userEvent.click(screen.getByRole('button', { name: 'Cancel the goal' }))
    await waitFor(() => expect(calls.map((c) => `${c.method} ${c.path}`)).toEqual(['POST /api/goal/pause', 'POST /api/goal/resume', 'DELETE /api/goal']))
  })

  it('says how it ended, until put away', async () => {
    const onDismiss = vi.fn()
    wrap(<GoalBar goal={goal({ status: 'met', passed: true, outcome: 'Goal met: `dotnet test` succeeded (exit code 0), after 3 turns.' })} onChanged={vi.fn()} onDismiss={onDismiss} />)
    const bar = screen.getByRole('region', { name: /^Goal/ })
    expect(bar).toHaveTextContent('Goal · Met')
    expect(bar).toHaveTextContent('Goal met: `dotnet test` succeeded (exit code 0), after 3 turns.')
    expect(within(bar).queryByRole('button', { name: 'Pause the goal' })).toBeNull()
    await userEvent.click(within(bar).getByRole('button', { name: 'Put the goal away' }))
    expect(onDismiss).toHaveBeenCalled()
  })

  it('is set with its check and budget, and refused a budget out of bounds', async () => {
    const calls = fakeApi(null, { 'POST /api/goal': () => ({ json: { goal: goal({}) } }) })
    const onSet = vi.fn()
    wrap(<GoalDialog open onOpenChange={vi.fn()} onSet={onSet} initial="The tests pass" />)
    const dialog = await screen.findByRole('dialog', { name: 'Work toward a goal' })
    const start = within(dialog).getByRole('button', { name: 'Start' })
    // A command check needs its command.
    expect(start).toBeDisabled()
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'The command' }), 'dotnet test')
    const steps = within(dialog).getByRole('textbox', { name: /Steps/ })
    await userEvent.clear(steps)
    await userEvent.type(steps, '0')
    expect(within(dialog).getByText('The budget is out of bounds.')).toBeInTheDocument()
    expect(start).toBeDisabled()
    await userEvent.clear(steps)
    await userEvent.type(steps, '50')
    await userEvent.click(start)
    await waitFor(() => expect(onSet).toHaveBeenCalled())
    expect(calls.find((c) => c.path === '/api/goal')?.body).toEqual({ text: 'The tests pass', check: { kind: 'command', text: 'dotnet test' }, budget: { steps: 50, minutes: 60, tokens: 5_000_000, untilMet: false } })
  })

  it('lets the agent write its check down', async () => {
    const calls = fakeApi(null, { 'POST /api/goal': () => ({ json: { goal: goal({ check: null }) } }) })
    wrap(<GoalDialog open onOpenChange={vi.fn()} onSet={vi.fn()} initial="Ship it" />)
    const dialog = await screen.findByRole('dialog', { name: 'Work toward a goal' })
    await userEvent.click(within(dialog).getByRole('radio', { name: /The agent writes it down first/ }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Start' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/goal')?.body).toMatchObject({ text: 'Ship it', check: null }))
  })

  it('works until it is met with no limit, and waits its turn while another goal is worked on', async () => {
    const calls = fakeApi(null, { 'POST /api/goal': () => ({ json: { goal: goal({}) } }) })
    wrap(<GoalDialog open onOpenChange={vi.fn()} onSet={vi.fn()} initial="Every test passes" queued />)
    const dialog = await screen.findByRole('dialog', { name: 'Add a goal after this one' })
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'The command' }), 'npm test')
    await userEvent.click(within(dialog).getByRole('checkbox', { name: /Until it is met/ }))
    // No budget to set.
    expect(within(dialog).queryByRole('textbox', { name: /Steps/ })).toBeNull()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add' }))
    await waitFor(() => expect((calls.find((c) => c.path === '/api/goal')?.body as { budget: { untilMet: boolean } }).budget.untilMet).toBe(true))
  })

  it('lists the goals waiting in its bar, drops one, and starts the next once the one before ended', async () => {
    const calls = fakeApi(null, { 'POST /api/goal/drop': () => ({ json: { goal: goal({}) } }), 'POST /api/goal/next': () => ({ json: { goal: goal({}) } }) })
    const onChanged = vi.fn()
    wrap(<GoalBar goal={goal({ status: 'met', outcome: 'Goal met.' })} next={[goal({ text: 'Then the docs' }), goal({ text: 'Then the release notes' })]} onChanged={onChanged} onDismiss={vi.fn()} />)
    const waiting = await screen.findByRole('list', { name: 'Goals waiting' })
    expect(within(waiting).getAllByRole('listitem').map((l) => l.textContent)).toEqual(['1.Then the docs', '2.Then the release notes'])
    await userEvent.click(within(waiting).getByRole('button', { name: 'Drop goal 2' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/goal/drop')?.body).toEqual({ place: 2 }))
    await userEvent.click(screen.getByRole('button', { name: /Start the next/ }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/goal/next')).toBe(true))
  })

  it('adds a loop and stops one', async () => {
    const loop = { id: 'L1', prompt: 'Run the tests', everyMs: 3_600_000, edits: false, runs: 2, skipped: 0, nextAt: '2026-11-09T10:00:00Z', lastAt: null, lastTask: 'b3', lastStatus: 'done' }
    const calls = fakeApi(null, {
      'GET /api/loops': () => ({ json: { loops: [loop] } }),
      'POST /api/loops': () => ({ status: 201, json: { loops: [loop] } }),
      'DELETE /api/loops/L1': () => ({ status: 204 }),
    })
    wrap(<LoopsDialog open onOpenChange={vi.fn()} />)
    const list = await screen.findByRole('list', { name: 'Loops' })
    expect(within(list).getByText(/L1 · every 1h · 2 runs · last b3 done/)).toBeInTheDocument()
    await userEvent.click(within(list).getByRole('button', { name: 'Stop loop L1' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.path === '/api/loops/L1')).toBe(true))
    await userEvent.type(screen.getByRole('textbox', { name: 'Each run' }), 'Check the build')
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'How often' }), '30m')
    await userEvent.click(screen.getByRole('button', { name: /Add the loop/ }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/loops')?.body).toEqual({ every: '30m', prompt: 'Check the build', edits: false }))
  })
})
