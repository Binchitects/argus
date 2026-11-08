import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

const config = {
  model: 'Main', models: [{ name: 'Main', loaded: true, vision: false }], tools: [{ id: 'mcp:s1', name: 'Vet', description: 'Pets', onByDefault: false }],
  maxUploadBytes: 1_000_000, thinking: [], defaultThinking: null,
}

describe('scheduled tasks', () => {
  it('adds a task a plugin offers, its question, when and tools filled in', async () => {
    const calls = fakeApi(member, {
      'GET /api/tasks': () => ({ json: { enabled: true, perPerson: 10, minIntervalMinutes: 5, webhookHosts: '', email: null, tasks: [], gitlabBot: false } }),
      'GET /api/tasks/templates': () => ({
        json: [{ id: 't1', name: 'Morning pets', description: 'The pets to see today.', prompt: "List today's pets.", cron: '0 8 * * 1-5', tools: ['mcp:s1'], plugin: 'vet', pluginTitle: 'Vet' }],
      }),
      'GET /api/chat/config': () => ({ json: config }),
      'POST /api/tasks': () => ({ json: { id: 'k1', name: 'Morning pets' } }),
    })
    renderApp('/tasks')
    const offered = await screen.findByRole('region', { name: 'From plugins' })
    expect(within(offered).getByText('The pets to see today.')).toBeInTheDocument()
    await userEvent.click(within(offered).getByRole('button', { name: /Add/ }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByLabelText('Name')).toHaveValue('Morning pets')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add task' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/tasks')?.body).toMatchObject({ name: 'Morning pets', prompt: "List today's pets.", cron: '0 8 * * 1-5', tools: ['mcp:s1'] }))
  })

  it('adds a ready-made task for developers that reviews several repositories, and links each run to its review', async () => {
    const task = {
      id: 'k2', name: 'Weekly product review', prompt: '', cron: '0 5 * * 1', timeZone: 'UTC', model: null, thinking: null, tools: null, sameChat: false, email: false, webhookSet: false, enabled: true,
      nextRunAt: null, running: false, nextRuns: [], trigger: 'schedule', events: [], replyInGitLab: false, hookUrl: null, action: 'review-repositories', repositories: ['g/app', 'g/lib'], ref: 'main', cross: true,
      lastRun: { id: 'r1', startedAt: '2026-11-02T05:00:00Z', finishedAt: '2026-11-02T05:20:00Z', status: 'done', conversationId: null, link: '/code/reviews/batches/b1', error: null, manual: false, delivery: null },
    }
    let tasks: object[] = []
    const calls = fakeApi(member, {
      'GET /api/tasks': () => ({ json: { enabled: true, perPerson: 10, minIntervalMinutes: 5, webhookHosts: '', email: null, tasks, gitlabBot: false } }),
      'GET /api/tasks/templates': () => ({ json: [] }),
      'GET /api/chat/config': () => ({ json: config }),
      'POST /api/tasks': () => {
        tasks = [task]
        return { json: task }
      },
    })
    renderApp('/tasks')
    const offered = await screen.findByRole('region', { name: 'For developers' })
    await userEvent.click(within(offered).getByRole('button', { name: 'Add Weekly product review' }))
    const dialog = await screen.findByRole('dialog')
    // A review needs no question: its repositories, a branch, and whether to look across them.
    expect(within(dialog).queryByLabelText('What to ask')).toBeNull()
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Repositories' }), 'g/app{Enter}g/lib')
    await userEvent.type(within(dialog).getByLabelText('Branch, tag or commit'), 'main')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add task' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'POST' && c.path === '/api/tasks')?.body).toMatchObject({ name: 'Weekly product review', action: 'review-repositories', repositories: ['g/app', 'g/lib'], ref: 'main', cross: true, cron: '0 5 * * 1' }),
    )
    const card = await screen.findByRole('region', { name: 'Weekly product review' })
    expect(within(card).getByText('Review several repositories')).toBeInTheDocument()
    expect(within(card).getByRole('link', { name: 'Open the review' })).toHaveAttribute('href', '/code/reviews/batches/b1')
  })
})
