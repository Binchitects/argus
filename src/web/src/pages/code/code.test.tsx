import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

const brief = { id: 'v1', project: 'g/app', iid: 7, fromRef: 'main', toRef: 'fast', title: '!7 Count faster', webUrl: null, status: 'done', step: null, verdict: 'changes', risk: 'medium', files: 2, createdAt: '2026-10-20T00:00:00Z', finishedAt: null, error: null }

const finding = (id: string, over: object) => ({ id, path: 'count.c', line: 2, severity: 'major', category: 'bug', title: 'Off by one', body: 'Reads past the end.', suggestion: null, verified: true, verifyNote: 'The diff shows it.', state: 'open', ...over })

describe('code', () => {
  it('lists a project’s merge requests and starts a review', async () => {
    const calls = fakeApi(member, {
      'GET /api/code/reviews': () => ({ json: [] }),
      'GET /api/code/mrs': () => ({ json: [{ iid: 7, title: 'Count faster', state: 'opened', author: 'dev', source_branch: 'fast', target_branch: 'main' }] }),
      'POST /api/code/reviews': () => ({ status: 202, json: { id: 'v1' } }),
      'GET /api/code/reviews/v1': () => ({ json: { review: { ...brief, status: 'running', step: 'Reviewing count.c (1 of 2)' }, summary: null, ci: null, model: null, headSha: null, previousId: null, roomId: null, promptTokens: 0, completionTokens: 0, by: 'Mo', mine: true, findings: [] } }),
    })
    const { router } = renderApp('/code')
    await userEvent.type(await screen.findByLabelText('Project'), 'g/app')
    await userEvent.click(screen.getByRole('button', { name: 'Show' }))
    const list = await screen.findByRole('list', { name: 'Merge requests' })
    await userEvent.click(within(list).getByRole('button', { name: 'Review' }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/code/reviews/v1'))
    expect(calls.find((c) => c.path === '/api/code/reviews' && c.method === 'POST')?.body).toEqual({ project: 'g/app', iid: 7 })
    expect(await screen.findByText(/Reviewing count.c/)).toBeInTheDocument()
  })

  it('shows a review’s findings, doubted ones apart, and resolves one', async () => {
    const calls = fakeApi(member, {
      'GET /api/code/reviews/v1': () => ({
        json: {
          review: brief, summary: 'The change counts.', ci: 'Pipeline failed; failed: test', model: 'Main', headSha: 'bbb', previousId: null, roomId: null, promptTokens: 1, completionTokens: 1, by: 'Mo', mine: true,
          findings: [finding('f1', {}), finding('f2', { title: 'Index past z', verified: false, verifyNote: 'The caller checks the bound.' })],
        },
      }),
      'PUT /api/code/reviews/v1/findings/f1': () => ({ status: 204 }),
    })
    renderApp('/code/reviews/v1')
    const findings = await screen.findByRole('list', { name: 'Findings' })
    expect(within(findings).getByText('Off by one')).toBeInTheDocument()
    expect(within(findings).queryByText('Index past z')).toBeNull()
    expect(screen.getByText('Changes suggested')).toBeInTheDocument()
    expect(screen.getByText('CI: Pipeline failed; failed: test')).toBeInTheDocument()
    await userEvent.click(within(findings).getByRole('button', { name: 'Resolved' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ state: 'resolved' }))
  })
})
