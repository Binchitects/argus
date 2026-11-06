import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { visibleNavigation } from '@/app/nav'
import { fakeApi, member, renderApp } from '@/test/utils'

const home = { sources: [{ id: 's1', name: 'HR files', kind: 'upload', syncedAt: new Date().toISOString(), documents: 2 }], meaning: true, code: true, data: true }

const page = (n: number, total = 1) => ({
  total,
  page: n,
  perPage: 20,
  reach: 2000,
  facets: { repos: [{ repo: 'team/mylib', n: total }, { repo: 'team/other', n: 2 }], langs: [{ lang: 'cpp', n: total }], kinds: { symbol: 3, code: total - 4, doc: 1 } },
  branches: ['main', 'v2'],
  branch: null,
})

describe('wiki', () => {
  it('searches documents, code and data at once, each in its own list, and browses a source', async () => {
    const calls = fakeApi(member, {
      'GET /api/wiki': () => ({ json: home }),
      'GET /api/wiki/search': () => ({
        json: {
          query: 'leave',
          knowledge: [{ documentId: 'd1', title: 'policies/leave.md', url: null, source: 'HR files', heading: 'Paid leave', line: 3, snippet: 'Everyone gets 30 days of paid leave.', score: 1 }],
          code: [{ kind: 'code', how: 'words', repoId: 7, repo: 'hr/portal', branch: 'main', path: 'src/Leave.cs', lang: 'csharp', line: 12, name: null, symbolKind: null, scope: null, doc: null, snippet: 'var days = 30;' }],
          codePage: page(1),
          meaning: [],
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

  it('finds a qualified name first, filters the code and reaches every match a page at a time', async () => {
    const symbol = { kind: 'symbol', how: 'name and scope', repoId: 4, repo: 'team/mylib', branch: 'main', path: 'src/mylib.cpp', lang: 'cpp', line: 3, name: 'TestFunc', symbolKind: 'function', scope: 'MyLib', doc: 'Adds one.', snippet: 'TestFunc(int a)' }
    const calls = fakeApi(member, {
      'GET /api/wiki': () => ({ json: home }),
      'GET /api/wiki/search': (_body, _init, url) => {
        const n = Number(url.searchParams.get('page') ?? '1')
        return { json: { query: 'MyLib::TestFunc', knowledge: [], code: [symbol], codePage: page(n, 45), meaning: [], data: [], problems: [] } }
      },
    })
    renderApp('/wiki?q=MyLib%3A%3ATestFunc&in=code')
    const code = await screen.findByRole('region', { name: 'Code' })
    expect(within(code).getByText('45 matches')).toBeInTheDocument()
    expect(within(code).getByRole('link', { name: /team\/mylib\/src\/mylib.cpp:3/ })).toHaveAttribute('href', '/wiki/code/4?path=src%2Fmylib.cpp&line=3')
    expect(within(code).getByText('function MyLib::TestFunc')).toBeInTheDocument()
    expect(within(code).getByText('name and scope')).toBeInTheDocument()

    // Every match is reached: the pages, then back.
    expect(within(code).getByText('Page 1 of 3')).toBeInTheDocument()
    await userEvent.click(within(code).getByRole('button', { name: /Next/ }))
    await waitFor(() => expect(calls.some((c) => c.path.includes('page=2'))).toBe(true))
    expect(await within(screen.getByRole('region', { name: 'Code' })).findByText('Page 2 of 3')).toBeInTheDocument()

    // A kind and a path narrow it, from the first page.
    await userEvent.click(screen.getByRole('radio', { name: /Symbols 3/ }))
    await waitFor(() => expect(calls.some((c) => c.path.includes('kind=symbol') && !c.path.includes('page='))).toBe(true))
    await userEvent.type(screen.getByRole('textbox', { name: 'Path' }), 'src/{Enter}')
    await waitFor(() => expect(calls.some((c) => c.path.includes('kind=symbol') && c.path.includes('path=src%2F'))).toBe(true))
  })

  it('lists each repository once, with how many branches it has', async () => {
    fakeApi(member, {
      'GET /api/wiki/code/repos': () => ({
        json: [{ repoId: 4, repo: 'team/mylib', files: 12, symbols: 40, indexedAt: null, branches: [{ repoId: 4, branch: 'main', files: 12 }, { repoId: 5, branch: 'v2', files: 11 }] }],
      }),
    })
    renderApp('/wiki/code/repos')
    const list = await screen.findByRole('list', { name: 'Repositories' })
    expect(await within(list).findAllByRole('listitem')).toHaveLength(1)
    expect(within(list).getByText(/2 branches/)).toBeInTheDocument()
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
