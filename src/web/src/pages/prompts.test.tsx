import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import type { PromptItem, PromptLibrary } from '@/lib/prompts'
import { admin, fakeApi, member, renderApp } from '@/test/utils'

const prompt = (over: Partial<PromptItem>): PromptItem => ({
  id: over.name ?? 'p', name: 'p', title: 'P', text: '', variables: [], sharing: 'Personal', groups: [], source: 'mine', from: null, canEdit: true, updatedAt: '', ...over,
})

const team = { id: 'g1', name: 'Platform' }

const library = (over: Partial<PromptLibrary> = {}): PromptLibrary => ({
  isAdmin: false,
  groups: [team],
  prompts: [
    prompt({ id: 'p1', name: 'review', title: 'Review code', text: 'Review {{file}} for {{focus}}.', variables: ['file', 'focus'] }),
    prompt({ id: 'p2', name: 'retro', title: 'Retro notes', text: 'Sum up {{notes}}.', variables: ['notes'], source: 'group', sharing: 'Groups', groups: [team], from: 'Ada Admin', canEdit: false }),
    prompt({ id: 'p3', name: 'standup', title: 'Stand-up', text: 'Write my stand-up.', source: 'company', sharing: 'Company', canEdit: false }),
    prompt({ id: 'p4', name: 'triage', title: 'Triage a GitLab issue', text: 'Triage #{{issue}}.', variables: ['issue'], source: 'plugin', from: 'GitLab issues', canEdit: false }),
  ],
  ...over,
})

describe('prompts page', () => {
  it('lists a person’s own prompts, those shared with them, the company’s and plugins’, each with what it asks for', async () => {
    fakeApi(member, { 'GET /api/prompts': () => ({ json: library() }) })
    renderApp('/prompts')
    const yours = await screen.findByRole('region', { name: 'Yours' })
    const review = within(yours).getByRole('region', { name: '/review' })
    expect(within(review).getByText('file')).toBeInTheDocument()
    expect(within(review).getByRole('button', { name: 'Edit' })).toBeInTheDocument()
    const shared = screen.getByRole('region', { name: 'Shared with you' })
    expect(within(shared).getByText('from Ada Admin')).toBeInTheDocument()
    expect(within(shared).queryByRole('button', { name: 'Edit' })).toBeNull()
    expect(within(screen.getByRole('region', { name: 'For everyone' })).getByText('/standup')).toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'From plugins' })).getByText('GitLab issues')).toBeInTheDocument()

    await userEvent.type(screen.getByRole('textbox', { name: 'Find a prompt' }), 'gitlab')
    expect(screen.queryByRole('region', { name: 'Yours' })).toBeNull()
    expect(screen.getByRole('region', { name: 'From plugins' })).toBeInTheDocument()
  })

  it('a new prompt is shared with chosen groups; only admins may share with everyone', async () => {
    const calls = fakeApi(member, {
      'GET /api/prompts': () => ({ json: library() }),
      'POST /api/prompts': () => ({ status: 201, json: { id: 'p9', name: 'explain' } }),
    })
    renderApp('/prompts')
    await userEvent.click(await screen.findByRole('button', { name: 'New prompt' }))
    const dialog = await screen.findByRole('dialog', { name: 'New prompt' })
    expect(within(dialog).queryByRole('radio', { name: /Everyone/ })).toBeNull()
    await userEvent.type(within(dialog).getByLabelText('Slash name'), 'explain')
    await userEvent.type(within(dialog).getByLabelText('Title'), 'Explain code')
    await userEvent.type(within(dialog).getByLabelText('Text'), 'Explain {{{{code}} to a {{{{reader}}.')
    expect(within(dialog).getByText('Asks for: Code, Reader.')).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('radio', { name: /People in my groups/ }))
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Platform' }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add prompt' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'POST' && c.path === '/api/prompts')?.body).toEqual({
        name: 'explain', title: 'Explain code', text: 'Explain {{code}} to a {{reader}}.', sharing: 'Groups', groups: ['g1'],
      }),
    )
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'New prompt' })).toBeNull())
  })

  it('an admin may share with everyone; a prompt is edited and deleted from its card', async () => {
    const calls = fakeApi(admin, {
      'GET /api/prompts': () => ({ json: library({ isAdmin: true }) }),
      'PUT /api/prompts/p1': () => ({ status: 204 }),
      'DELETE /api/prompts/p1': () => ({ status: 204 }),
    })
    renderApp('/prompts')
    const review = await screen.findByRole('region', { name: '/review' })
    await userEvent.click(within(review).getByRole('button', { name: 'Edit' }))
    const dialog = await screen.findByRole('dialog', { name: 'Edit /review' })
    await userEvent.click(within(dialog).getByRole('radio', { name: /Everyone/ }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toMatchObject({ name: 'review', sharing: 'Company', groups: [] }))

    await userEvent.click(within(review).getByRole('button', { name: 'Delete' }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Delete' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.path === '/api/prompts/p1')).toBe(true))
  })

  it('Prompts is in the sidebar under Workspace', async () => {
    fakeApi(member, { 'GET /api/prompts': () => ({ json: library({ prompts: [] }) }) })
    renderApp('/prompts')
    expect(await screen.findByText('No prompts yet')).toBeInTheDocument()
    expect(screen.getAllByRole('link', { name: 'Prompts' }).length).toBeGreaterThan(0)
  })
})
