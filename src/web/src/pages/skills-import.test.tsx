import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { SkillImportPreview, SkillItem, SkillLibrary, SkillPreview } from '@/lib/skills'
import { fakeApi, member, renderApp } from '@/test/utils'

const saved = vi.hoisted(() => [] as { name: string }[])
vi.mock('@/lib/zip', async (actual) => ({ ...(await actual<typeof import('@/lib/zip')>()), saveBlob: (_: Blob, name: string) => saved.push({ name }) }))

const skill = (over: Partial<SkillItem>): SkillItem => ({
  id: over.name ?? 's', name: 's', title: 'S', description: '', instructions: 'Do it.', parameters: [], autoUse: true, sharing: 'Personal', groups: [], source: 'mine', from: null, canEdit: true, updatedAt: '', ...over,
})

const library = (): SkillLibrary => ({
  mayShareCompany: false,
  mayShareGroups: false,
  mayTurnBuiltIn: false,
  groups: [],
  skills: [
    skill({ id: 's1', name: 'review-mr', title: 'My review' }),
    skill({ id: 's2', name: 'release', title: 'Release', files: ['notes.md'] }),
    skill({ id: 's3', name: 'standup', title: 'Stand-up', source: 'company', sharing: 'Company', canEdit: false }),
  ],
})

const previewed = (over: Partial<SkillPreview>): SkillPreview => ({
  folder: over.name ?? 'x', name: 'x', title: 'X', description: '', parameters: 0, files: [], bytes: 120, leftOut: [], problem: null, nameProblem: null, clash: null, shadows: null,
  suggested: over.name ?? 'x', masked: false, ...over,
})

const preview: SkillImportPreview = {
  room: 197,
  leftOut: ['skills/logo.png: not a text file'],
  skills: [
    previewed({ name: 'review-mr', title: 'Review a merge request', files: [{ path: 'checklist.md', bytes: 40 }], clash: { id: 's1', title: 'My review', version: 1 }, suggested: 'review-mr-2' }),
    previewed({ name: 'triage', title: 'Triage', shadows: 'company' }),
    previewed({ name: 'deploy', title: 'Deploy', problem: 'The safeguards refused key.pem: it holds a private key.' }),
  ],
}

describe('skills import and export', () => {
  beforeEach(() => {
    saved.length = 0
  })

  it('previews an import, renames the one whose name is taken, and imports only what was chosen', async () => {
    const calls = fakeApi(member, {
      'GET /api/skills': () => ({ json: library() }),
      'POST /api/skills/import/preview': () => ({ json: preview }),
      'POST /api/skills/import': () => ({ json: { imported: [{ folder: 'review-mr', id: 'n1', name: 'review-theirs', replaced: false }, { folder: 'triage', id: 'n2', name: 'triage', replaced: false }], skipped: [{ folder: 'deploy', name: 'deploy', reason: 'Skipped.' }] } }),
    })
    renderApp('/skills')
    await userEvent.click(await screen.findByRole('button', { name: 'Import' }))
    const dialog = await screen.findByRole('dialog', { name: 'Import skills' })
    await userEvent.upload(within(dialog).getByLabelText('A .zip of skills or a SKILL.md'), new File(['zip'], 'skills.zip', { type: 'application/zip' }))

    const list = await within(dialog).findByRole('list', { name: 'Skills to import' })
    const review = within(list).getByRole('listitem', { name: '/review-mr' })
    expect(within(review).getByText(/You have \/review-mr already/)).toBeInTheDocument()
    expect(within(review).getByText('1 file')).toBeInTheDocument()
    // Nothing is in the way of triage: it comes in, before the company's of its name.
    const triage = within(list).getByRole('listitem', { name: '/triage' })
    expect(within(triage).getByRole('radio', { name: 'Import' })).toBeChecked()
    expect(within(triage).getByText('There is the company’s /triage: yours will come first.')).toBeInTheDocument()
    const deploy = within(list).getByRole('listitem', { name: '/deploy' })
    expect(within(deploy).getByText(/The safeguards refused key.pem/)).toBeInTheDocument()
    expect(within(deploy).queryByRole('radiogroup')).toBeNull()
    expect(within(dialog).getByText('Left out of the upload (1)')).toBeInTheDocument()

    // A clash is left unless chosen for.
    expect(within(review).getByRole('radio', { name: 'Skip' })).toBeChecked()
    expect(within(dialog).getByRole('button', { name: 'Import 1 skill' })).toBeEnabled()
    await userEvent.click(within(review).getByRole('radio', { name: 'Rename' }))
    const name = within(review).getByRole('textbox', { name: 'New name for /review-mr' })
    expect(name).toHaveValue('review-mr-2')
    await userEvent.clear(name)
    await userEvent.type(name, 'Review Theirs')
    expect(name).toHaveValue('review-theirs')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Import 2 skills' }))

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Import skills' })).toBeNull())
    const sent = calls.find((c) => c.method === 'POST' && c.path === '/api/skills/import')!.body as FormData
    expect((sent.get('files') as File).name).toBe('skills.zip')
    expect(JSON.parse(sent.get('choices') as string)).toEqual([
      { folder: 'review-mr', action: 'rename', name: 'review-theirs' },
      { folder: 'triage', action: 'import' },
      { folder: 'deploy', action: 'skip' },
    ])
    expect(await screen.findByText('Imported 2 skills: /review-theirs, /triage.')).toBeInTheDocument()
  })

  it('exports one skill, its SKILL.md alone, or the skills selected together', async () => {
    const calls = fakeApi(member, {
      'GET /api/skills': () => ({ json: library() }),
      'GET /api/skills/export': () => ({ json: {} }),
    })
    renderApp('/skills')
    const yours = await screen.findByRole('region', { name: 'Yours' })
    const release = within(yours).getByRole('region', { name: '/release' })
    expect(within(release).getByText('notes.md')).toBeInTheDocument()
    // With files, only as a .zip.
    expect(within(release).queryByRole('button', { name: 'Download the SKILL.md of /release' })).toBeNull()
    await userEvent.click(within(release).getByRole('button', { name: 'Export /release' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/skills/export?ids=s2')).toBe(true))
    await userEvent.click(within(yours).getByRole('button', { name: 'Download the SKILL.md of /review-mr' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/skills/export?ids=s1&format=md')).toBe(true))

    await userEvent.click(within(yours).getByRole('checkbox', { name: 'Select /review-mr' }))
    await userEvent.click(within(screen.getByRole('region', { name: 'For everyone' })).getByRole('checkbox', { name: 'Select /standup' }))
    const bar = screen.getByRole('group', { name: 'Selected skills' })
    expect(within(bar).getByText('2 selected')).toBeInTheDocument()
    await userEvent.click(within(bar).getByRole('button', { name: 'Export selected' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/skills/export?ids=s1,s3')).toBe(true))
    await waitFor(() => expect(saved).toHaveLength(3))
    await userEvent.click(within(bar).getByRole('button', { name: 'Clear' }))
    expect(screen.queryByRole('group', { name: 'Selected skills' })).toBeNull()
  })

  it('offers no import without the permission to make skills', async () => {
    fakeApi({ ...member, denied: ['feature.skills'] }, { 'GET /api/skills': () => ({ json: library() }) })
    renderApp('/skills')
    await screen.findByRole('region', { name: 'Yours' })
    expect(screen.queryByRole('button', { name: 'Import' })).toBeNull()
    expect(screen.getAllByRole('button', { name: /^Export \// }).length).toBeGreaterThan(0)
  })
})
