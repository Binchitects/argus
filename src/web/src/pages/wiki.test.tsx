import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { visibleNavigation } from '@/app/nav'
import { fakeApi, member, renderApp } from '@/test/utils'

const home = { sources: [{ id: 's1', name: 'HR files', kind: 'upload', syncedAt: new Date().toISOString(), documents: 2 }], meaning: true, code: true, data: true }

describe('wiki', () => {
  it('searches documents, code and data at once, each in its own list, and browses a source', async () => {
    const calls = fakeApi(member, {
      'GET /api/wiki': () => ({ json: home }),
      'GET /api/wiki/search': () => ({
        json: {
          query: 'leave',
          knowledge: [{ documentId: 'd1', title: 'policies/leave.md', url: null, source: 'HR files', heading: 'Paid leave', line: 3, snippet: 'Everyone gets 30 days of paid leave.', score: 1 }],
          code: [{ how: 'text', repoId: 7, repo: 'hr/portal', path: 'src/Leave.cs', line: 12, name: null, kind: null, snippet: 'var days = 30;' }],
          data: [{ sourceId: 'db1', source: 'HR database', table: 'leave_requests', kind: 'table', comment: null, columns: [{ name: 'days', type: 'integer', comment: null }], matched: ['days'] }],
          problems: [],
        },
      }),
      'GET /api/wiki/documents': () => ({ json: { documents: [{ id: 'd1', title: 'policies/leave.md', url: null, sourceId: 's1', source: 'HR files', syncedAt: new Date().toISOString(), passages: 2 }], next: null } }),
    })
    renderApp('/wiki')
    await userEvent.type(await screen.findByRole('textbox', { name: 'Search the wiki' }), 'leave')
    await userEvent.click(screen.getByRole('button', { name: 'Search' }))
    const docs = await screen.findByRole('region', { name: 'Documents' })
    expect(within(docs).getByRole('link', { name: 'policies/leave.md' })).toHaveAttribute('href', '/wiki/documents/d1')
    expect(within(docs).getByText('Everyone gets 30 days of paid leave.')).toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'Code' })).getByRole('link', { name: /hr\/portal\/src\/Leave.cs:12/ })).toHaveAttribute('href', '/wiki/code/7?path=src%2FLeave.cs&line=12')
    expect(within(screen.getByRole('region', { name: 'Data' })).getByText('leave_requests')).toBeInTheDocument()
    expect(calls.some((c) => c.path === '/api/wiki/search?q=leave&in=all')).toBe(true)

    await userEvent.click(screen.getByRole('button', { name: /HR files/ }))
    expect(await within(await screen.findByRole('list', { name: 'Documents' })).findByRole('link', { name: 'policies/leave.md' })).toBeInTheDocument()
    await waitFor(() => expect(calls.some((c) => c.path.startsWith('/api/wiki/documents?source=s1'))).toBe(true))
  })

  it('reads a document whole, its passages joined without their overlap', async () => {
    fakeApi(member, {
      'GET /api/wiki/documents/d1': () => ({
        json: {
          id: 'd1', title: 'policies/leave.md', url: 'https://wiki.example/leave', sourceId: 's1', source: 'HR files', syncedAt: new Date().toISOString(), passages: [
            { ordinal: 0, heading: null, line: 1, endLine: 3, text: '# Leave\n\nEveryone gets 30 days of paid leave a year.' },
            { ordinal: 1, heading: null, line: 3, endLine: 5, text: 'Everyone gets 30 days of paid leave a year.\n\nTell your manager by nine.' },
          ],
        },
      }),
    })
    renderApp('/wiki/documents/d1')
    expect(await screen.findByRole('heading', { name: 'policies/leave.md' })).toBeInTheDocument()
    expect(screen.getAllByText('Everyone gets 30 days of paid leave a year.')).toHaveLength(1)
    expect(screen.getByText('Tell your manager by nine.')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Open the original/ })).toHaveAttribute('href', 'https://wiki.example/leave')
  })

  it('is in everyone’s menu', () => {
    expect(visibleNavigation(member).flatMap((s) => s.items.map((i) => i.title))).toContain('Wiki')
  })
})
