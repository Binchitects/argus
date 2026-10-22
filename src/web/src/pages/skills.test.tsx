import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import type { SkillItem, SkillLibrary } from '@/lib/skills'
import { admin, fakeApi, member, renderApp } from '@/test/utils'

const skill = (over: Partial<SkillItem>): SkillItem => ({
  id: over.name ?? 's', name: 's', title: 'S', description: '', instructions: '', parameters: [], autoUse: true, sharing: 'Personal', groups: [], source: 'mine', from: null, canEdit: true, updatedAt: '', ...over,
})

const team = { id: 'g1', name: 'Platform' }

const library = (over: Partial<SkillLibrary> = {}): SkillLibrary => ({
  isAdmin: false,
  groups: [team],
  skills: [
    skill({
      id: 's1', name: 'review', title: 'Review code', description: 'Review a file for one concern.', instructions: 'Review {{file}} for {{focus}}.',
      parameters: [{ name: 'file', description: 'The path', required: true }, { name: 'focus', required: false, default: 'bugs' }],
    }),
    skill({ id: 's2', name: 'retro', title: 'Retro notes', instructions: 'Sum up {{notes}}.', parameters: [{ name: 'notes', required: true }], source: 'group', sharing: 'Groups', groups: [team], from: 'Ada Admin', canEdit: false }),
    skill({ id: 's3', name: 'standup', title: 'Stand-up', instructions: 'Write my stand-up.', autoUse: false, description: 'Daily notes.', source: 'company', sharing: 'Company', canEdit: false }),
    skill({ id: 's4', name: 'triage', title: 'Triage a GitLab issue', instructions: 'Triage #{{issue}}.', parameters: [{ name: 'issue', required: true }], source: 'plugin', from: 'GitLab issues', canEdit: false }),
  ],
  ...over,
})

describe('skills page', () => {
  it('lists a person’s own skills, those shared with them, the company’s and plugins’, each with what it asks for and whether the model uses it', async () => {
    fakeApi(member, { 'GET /api/skills': () => ({ json: library() }) })
    renderApp('/skills')
    const yours = await screen.findByRole('region', { name: 'Yours' })
    const review = within(yours).getByRole('region', { name: '/review' })
    expect(within(review).getByText('Review a file for one concern.')).toBeInTheDocument()
    expect(within(review).getByText('file')).toBeInTheDocument()
    expect(within(review).getByText('Used on its own')).toBeInTheDocument()
    expect(within(review).getByRole('button', { name: 'Edit' })).toBeInTheDocument()
    const shared = screen.getByRole('region', { name: 'Shared with you' })
    expect(within(shared).getByText('from Ada Admin')).toBeInTheDocument()
    // No description: the model cannot know when to use it.
    expect(within(shared).getByText('/ only')).toBeInTheDocument()
    expect(within(shared).queryByRole('button', { name: 'Edit' })).toBeNull()
    const everyone = screen.getByRole('region', { name: 'For everyone' })
    expect(within(everyone).getByText('/standup')).toBeInTheDocument()
    expect(within(everyone).getByText('/ only')).toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'From plugins' })).getByText('GitLab issues')).toBeInTheDocument()

    await userEvent.type(screen.getByRole('textbox', { name: 'Find a skill' }), 'gitlab')
    expect(screen.queryByRole('region', { name: 'Yours' })).toBeNull()
    expect(screen.getByRole('region', { name: 'From plugins' })).toBeInTheDocument()
  })

  it('a new skill takes its parameters from the instructions, each described, and is shared with chosen groups; only admins share with everyone', async () => {
    const calls = fakeApi(member, {
      'GET /api/skills': () => ({ json: library() }),
      'POST /api/skills': () => ({ status: 201, json: { id: 's9', name: 'explain' } }),
    })
    renderApp('/skills')
    await userEvent.click(await screen.findByRole('button', { name: 'New skill' }))
    const dialog = await screen.findByRole('dialog', { name: 'New skill' })
    expect(within(dialog).queryByRole('radio', { name: /Everyone/ })).toBeNull()
    await userEvent.type(within(dialog).getByLabelText('Slash name'), 'explain')
    await userEvent.type(within(dialog).getByLabelText('Title'), 'Explain code')
    await userEvent.type(within(dialog).getByLabelText('When to use it'), 'Explain code to someone new to it.')
    await userEvent.type(within(dialog).getByLabelText('Instructions'), 'Explain {{{{code}} to a {{{{reader}}. $ARGUMENTS')
    await userEvent.tab()
    const params = within(dialog).getByRole('list', { name: 'Parameters' })
    const reader = await within(params).findByRole('listitem', { name: 'reader' })
    await userEvent.type(within(reader).getByLabelText('What it is'), 'Who reads it')
    await userEvent.type(within(reader).getByLabelText('Default'), 'new hire')
    await userEvent.click(within(reader).getByRole('checkbox', { name: 'Required' }))
    await userEvent.click(within(dialog).getByRole('radio', { name: /People in my groups/ }))
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Platform' }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add skill' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'POST' && c.path === '/api/skills')?.body).toEqual({
        name: 'explain', title: 'Explain code', description: 'Explain code to someone new to it.', instructions: 'Explain {{code}} to a {{reader}}. $ARGUMENTS', autoUse: true,
        sharing: 'Groups', groups: ['g1'],
        parameters: [
          { name: 'code', description: null, required: true, default: null },
          { name: 'reader', description: 'Who reads it', required: false, default: 'new hire' },
        ],
      }),
    )
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'New skill' })).toBeNull())
  })

  it('an admin may share with everyone and keep a skill for / only; a skill is edited and deleted from its card', async () => {
    const calls = fakeApi(admin, {
      'GET /api/skills': () => ({ json: library({ isAdmin: true }) }),
      'PUT /api/skills/s1': () => ({ status: 204 }),
      'DELETE /api/skills/s1': () => ({ status: 204 }),
    })
    renderApp('/skills')
    const review = await screen.findByRole('region', { name: '/review' })
    await userEvent.click(within(review).getByRole('button', { name: 'Edit' }))
    const dialog = await screen.findByRole('dialog', { name: 'Edit /review' })
    expect(within(within(dialog).getByRole('list', { name: 'Parameters' })).getAllByRole('listitem').map((l) => l.getAttribute('aria-label'))).toEqual(['file', 'focus'])
    await userEvent.click(within(dialog).getByRole('switch', { name: 'The model uses it on its own' }))
    await userEvent.click(within(dialog).getByRole('radio', { name: /Everyone/ }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'PUT')?.body).toMatchObject({
        name: 'review', sharing: 'Company', groups: [], autoUse: false,
        parameters: [{ name: 'file', description: 'The path', required: true, default: null }, { name: 'focus', description: null, required: false, default: 'bugs' }],
      }),
    )

    await userEvent.click(within(review).getByRole('button', { name: 'Delete' }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Delete' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.path === '/api/skills/s1')).toBe(true))
  })

  it('Skills is in the sidebar under Workspace', async () => {
    fakeApi(member, { 'GET /api/skills': () => ({ json: library({ skills: [] }) }) })
    renderApp('/skills')
    expect(await screen.findByText('No skills yet')).toBeInTheDocument()
    expect(screen.getAllByRole('link', { name: 'Skills' }).length).toBeGreaterThan(0)
  })
})
