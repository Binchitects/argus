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
})
