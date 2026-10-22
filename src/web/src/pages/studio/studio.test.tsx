import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import type { Run, SharedWorkflow, Template, Workflow } from './studio-api'

const template: Template = {
  key: 'digest', name: 'Morning digest', description: 'The news by email.',
  flow: { trigger: { kind: 'schedule', cron: '0 8 * * 1-5', timeZone: 'UTC' }, steps: [{ id: 'news', kind: 'agent', name: 'Find the news', prompt: 'News', tools: ['web'] }, { id: 'mail', kind: 'email', subject: 'Digest', text: '{{news.text}}' }] },
}

const workflow: Workflow = {
  id: 'w1', name: 'Triage', description: '', enabled: false, reach: 'private', groupIds: [], nextRunAt: null, lastRunAt: null, mine: true, hook: 'https://x/api/studio/hooks/w1', secretSet: false,
  flow: { trigger: { kind: 'manual' }, steps: [{ id: 'draft', kind: 'ask', name: 'Draft', prompt: 'Draft about {{input.text}}' }, { id: 'ok', kind: 'approval', question: 'Send {{draft.text}}?' }] },
}

const waitingRun: Run = {
  id: 'r1', workflowId: 'w1', workflow: 'Triage', trigger: 'manual', status: 'waiting', error: null, startedAt: '2026-10-22T08:00:00Z', finishedAt: null, input: { text: 'lunch' }, flow: workflow.flow,
  steps: [
    { path: 'draft', stepId: 'draft', kind: 'ask', name: 'Draft', status: 'done', input: 'Draft about lunch', output: 'A note about lunch', link: null, error: null, startedAt: '2026-10-22T08:00:00Z', finishedAt: '2026-10-22T08:00:02Z' },
    { path: 'ok', stepId: 'ok', kind: 'approval', name: '', status: 'waiting', input: 'Send A note about lunch?', output: '', link: null, error: null, startedAt: '2026-10-22T08:00:02Z', finishedAt: null },
  ],
}

afterEach(() => vi.restoreAllMocks())

describe('Agent Studio', () => {
  it('a template becomes a workflow of one\'s own, opened on its canvas', async () => {
    const calls = fakeApi(member, {
      'GET /api/studio': () => ({ json: [] }),
      'GET /api/studio/templates': () => ({ json: [template] }),
      'POST /api/studio': () => ({ status: 201, json: { workflow: { ...workflow, id: 'w2', name: 'Morning digest', flow: template.flow }, secret: null } }),
      'GET /api/studio/w2': () => ({ json: { ...workflow, id: 'w2', name: 'Morning digest', flow: template.flow } }),
      'GET /api/studio/tools': () => ({ json: [] }),
    })
    const { router } = renderApp('/studio')
    await userEvent.click(await within(await screen.findByRole('list', { name: 'Templates' })).findByRole('button', { name: /Morning digest/ }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/studio/w2'))
    expect(calls.find((c) => c.path === '/api/studio' && c.method === 'POST')?.body).toEqual({ name: 'Morning digest', description: 'The news by email.', flow: template.flow, enabled: false })
    const canvas = await screen.findByRole('region', { name: 'The workflow' })
    expect(within(canvas).getByRole('button', { name: 'Trigger: On a schedule' })).toHaveTextContent('0 8 * * 1-5 · UTC')
    expect(within(canvas).getByRole('button', { name: 'Agent with tools: Find the news' })).toHaveTextContent('News · web')
  })

  it('a step is added on the canvas, changed in the panel with a value put in, and saved', async () => {
    let saved: unknown = null
    fakeApi(member, {
      'GET /api/studio/w1': () => ({ json: workflow }),
      'GET /api/studio/tools': () => ({ json: [] }),
      'PUT /api/studio/w1': (body) => {
        saved = body
        return { json: { workflow: { ...workflow, flow: (body as { flow: Workflow['flow'] }).flow }, secret: null } }
      },
    })
    renderApp('/studio/w1')
    const canvas = await screen.findByRole('region', { name: 'The workflow' })
    // After the draft: a text step.
    await userEvent.click(within(canvas).getAllByRole('button', { name: 'Add a step here' })[1]!)
    await userEvent.click(await screen.findByRole('menuitem', { name: /Write text/ }))
    const panel = screen.getByRole('complementary', { name: 'Settings of what is chosen' })
    expect(within(panel).getByRole('heading', { name: 'Write text' })).toBeInTheDocument()
    await userEvent.click(within(panel).getByRole('button', { name: 'Put a value in Text' }))
    await userEvent.click(await screen.findByRole('menuitem', { name: /Draft's answer/ }))
    expect(within(panel).getByLabelText('Text')).toHaveValue('{{draft.text}}')
    await userEvent.click(screen.getByRole('button', { name: /Save/ }))
    await waitFor(() => expect(saved).not.toBeNull())
    expect((saved as { flow: Workflow['flow'] }).flow.steps.map((s) => [s.id, s.kind])).toEqual([['draft', 'ask'], ['s1', 'text'], ['ok', 'approval']])
    expect((saved as { flow: Workflow['flow'] }).flow.steps[1]!.text).toBe('{{draft.text}}')
  })

  it('a run that asks waits for a yes, and replays step by step', async () => {
    let answered: unknown = null
    fakeApi(member, {
      'GET /api/studio/w1': () => ({ json: workflow }),
      'GET /api/studio/tools': () => ({ json: [] }),
      'GET /api/studio/runs/r1': () => ({ json: waitingRun }),
      'POST /api/studio/runs/r1/approve': (body) => {
        answered = body
        return { status: 202, json: { id: 'r1' } }
      },
    })
    renderApp('/studio/w1?run=r1')
    const asks = await screen.findByRole('region', { name: 'It asks you' })
    expect(asks).toHaveTextContent('Send A note about lunch?')
    // The replay is at its last step; back one shows the draft as it was asked and answered.
    expect(screen.getByText('Step 2 of 2')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'The step before' }))
    expect(screen.getByText('Step 1 of 2')).toBeInTheDocument()
    expect(screen.getByText('A note about lunch')).toBeInTheDocument()
    await userEvent.click(within(asks).getByRole('button', { name: 'Yes, go on' }))
    await waitFor(() => expect(answered).toEqual({ approve: true }))
  })

  it('a shared workflow is a form: it runs as its owner, needed fields first', async () => {
    const shared: SharedWorkflow = {
      id: 'w9', name: 'Requests', description: 'Ask the office.', enabled: true, mine: false, owner: 'Ana', trigger: 'form',
      fields: [{ name: 'topic', label: 'Topic', kind: 'text', required: true }, { name: 'details', label: 'Details', kind: 'long' }],
    }
    const calls = fakeApi(member, {
      'GET /api/studio/w9': () => ({ json: shared }),
      'POST /api/studio/w9/run': () => ({ status: 202, json: { id: 'r9' } }),
      'GET /api/studio/runs/r9': () => ({ json: { ...waitingRun, id: 'r9', status: 'done', flow: null, steps: [{ ...waitingRun.steps[0]!, input: null, output: null }] } }),
    })
    renderApp('/studio/w9')
    expect(await screen.findByText(/By Ana/)).toHaveTextContent('It runs as them')
    const run = screen.getByRole('button', { name: /Run it/ })
    expect(run).toBeDisabled()
    await userEvent.type(screen.getByLabelText('Topic'), 'Chairs')
    await userEvent.click(run)
    await waitFor(() => expect(calls.find((c) => c.path === '/api/studio/w9/run')?.body).toEqual({ text: '', fields: { topic: 'Chairs' } }))
    expect(await screen.findByRole('region', { name: 'The run' })).toHaveTextContent('Done')
  })
})
