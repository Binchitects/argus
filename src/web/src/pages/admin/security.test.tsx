import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

const zero = { critical: 0, high: 0, medium: 0, low: 0 }

describe('admin security', () => {
  it('shows every repository worst first, what cannot wait, this week and the risks accepted', async () => {
    fakeApi(admin, {
      'GET /api/admin/security': () => ({
        json: {
          repositories: [
            { scanId: 's1', project: 'g/shop', ref: 'main', commit: 'abc', finishedAt: '2026-10-27T00:00:00Z', advisoriesAsOf: '2026-10-28', recheckedAt: '2026-10-28T01:00:00Z', sbom: true, by: 'Mo', open: { ...zero, critical: 1, high: 2 }, exploited: 1, overdue: 1, fresh: 1 },
            { scanId: 's2', project: 'g/blog', ref: null, commit: null, finishedAt: '2026-10-20T00:00:00Z', advisoriesAsOf: '2026-10-20', recheckedAt: null, sbom: false, by: 'Ana', open: zero, exploited: 0, overdue: 0, fresh: 0 },
          ],
          totals: { open: { ...zero, critical: 1, high: 2 }, exploited: 1, overdue: 1, fresh: 1, fixedThisWeek: 1 },
          exploited: [{ id: 'f1', scanId: 's1', project: 'g/shop', kind: 'dependency', severity: 'critical', exploited: true, title: 'Log4Shell in log4j-core', path: 'pom.xml', line: 12, foundAt: '2026-10-28T01:00:00Z', overdue: false }],
          overdue: [{ id: 'f2', scanId: 's1', project: 'g/shop', kind: 'code', severity: 'high', exploited: false, title: 'SQL built from the request', path: 'src/Orders.cs', line: 4, foundAt: '2026-09-01T00:00:00Z', overdue: true }],
          fresh: [{ id: 'f1', scanId: 's1', project: 'g/shop', kind: 'dependency', severity: 'critical', exploited: true, title: 'Log4Shell in log4j-core', foundAt: '2026-10-28T01:00:00Z' }],
          fixedThisWeek: [{ id: 'f3', scanId: 's1', project: 'g/shop', kind: 'secret', severity: 'high', title: 'A secret (aws-access-token) in the code', decidedAt: '2026-10-27T10:00:00Z', by: 'Mo' }],
          accepted: [{ id: 'f4', scanId: 's2', project: 'g/blog', kind: 'config', severity: 'medium', title: 'Image runs as root', why: 'The base image needs it until December', until: '2026-12-31T12:00:00Z', decidedAt: '2026-10-01T00:00:00Z', by: 'Ana', lapsed: false }],
          advisories: { asOf: '2026-10-28', daysOld: 0 },
        },
      }),
      'GET /api/code/security/s1': () => ({ json: { scan: { id: 's1', project: 'g/shop', status: 'done', scanners: null }, summary: null, model: null, by: 'Mo', mine: false, findings: [] } }),
    })
    const { router } = renderApp('/admin/security')
    expect(await screen.findByLabelText('Known exploited', { selector: 'div' })).toHaveTextContent('1')
    expect(screen.getByLabelText('Past their time to fix', { selector: 'div' })).toHaveTextContent('1')
    const rows = screen.getAllByRole('row')
    expect(rows[1]).toHaveTextContent('g/shop @ main')
    expect(within(rows[1]).getByRole('link', { name: 'Download' })).toHaveAttribute('href', '/api/code/security/s1/sbom')
    const cannot = screen.getByRole('list', { name: 'Known exploited' })
    expect(within(cannot).getByText('known exploited')).toBeInTheDocument()
    expect(within(screen.getByRole('list', { name: 'Past their time to fix' })).getByText('past its time to fix')).toBeInTheDocument()
    expect(within(screen.getByRole('list', { name: 'Fixed this week' })).getByText(/by Mo/)).toBeInTheDocument()
    const accepted = screen.getByRole('list', { name: 'Risks accepted' })
    expect(within(accepted).getByText('Ana: The base image needs it until December')).toBeInTheDocument()
    await userEvent.click(within(cannot).getByRole('link', { name: 'Log4Shell in log4j-core' }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/code/security/s1'))
  })

  it('warns when there are no advisories to check packages against', async () => {
    fakeApi(admin, {
      'GET /api/admin/security': () => ({
        json: { repositories: [], totals: { open: zero, exploited: 0, overdue: 0, fresh: 0, fixedThisWeek: 0 }, exploited: [], overdue: [], fresh: [], fixedThisWeek: [], accepted: [], advisories: null },
      }),
    })
    renderApp('/admin/security')
    expect(await screen.findByText(/No advisories pack is installed/)).toBeInTheDocument()
    expect(screen.getByText('No repository has been reviewed yet')).toBeInTheDocument()
  })
})
