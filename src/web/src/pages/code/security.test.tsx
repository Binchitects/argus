import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

const brief = { id: 's1', project: 'g/shop', ref: 'main', status: 'done', step: null, candidates: 12, read: 12, dropped: 1, createdAt: '2026-10-27T00:00:00Z', finishedAt: null, error: null }

const finding = (id: string, over: object) => ({
  id, path: 'src/Orders.cs', line: 4, sinkCode: 'var cmd = new SqlCommand("SELECT ... " + id, conn);', cwe: 'CWE-89', cweTitle: 'CWE-89: SQL Injection (Base)', severity: 'high',
  title: 'SQL built from the request', source: 'Controller.cs:9 Request.Query["id"]', flow: 'Request.Query["id"]\nOrders.Find(id)', exploit: "id = ' OR '1'='1", fix: 'Use a parameter: `@id`.',
  pages: [{ title: 'CWE-89: SQL Injection (Base)', url: 'https://cwe.mitre.org/data/definitions/89.html' }, { title: 'SQL Injection Prevention Cheat Sheet', url: 'https://cheatsheetseries.owasp.org/x.html' }],
  verified: true, verifyNote: 'The id goes into the text.', state: 'open', decidedAt: null, why: null, until: null, ...over,
})

describe('code security', () => {
  it('starts a security review of a repository at a branch', async () => {
    const calls = fakeApi(member, {
      'GET /api/code/reviews': () => ({ json: [] }),
      'GET /api/code/security': () => ({ json: [{ scan: brief, mine: true, open: { high: 2 } }] }),
      'POST /api/code/security': () => ({ status: 202, json: { id: 's2' } }),
      'GET /api/code/security/s2': () => ({ json: { scan: { ...brief, id: 's2', status: 'running', step: 'Tracing src/Orders.cs:4 (1 of 12)' }, summary: null, model: null, by: 'Mo', mine: true, findings: [] } }),
    })
    const { router } = renderApp('/code')
    const reviews = await screen.findByRole('list', { name: 'Security reviews' })
    expect(within(reviews).getByText('2 high')).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('Repository'), 'g/shop')
    await userEvent.type(screen.getByLabelText('Branch, tag or commit'), 'main')
    await userEvent.click(screen.getByRole('button', { name: 'Review security' }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/code/security/s2'))
    expect(calls.find((c) => c.path === '/api/code/security' && c.method === 'POST')?.body).toEqual({ project: 'g/shop', ref: 'main' })
    expect(await screen.findByText(/Tracing src\/Orders.cs:4/)).toBeInTheDocument()
  })

  it('shows each weakness with its sink, path and the pack’s pages, doubted ones apart, and accepts a risk with why and until when', async () => {
    const calls = fakeApi(member, {
      'GET /api/code/security/s1': () => ({
        json: { scan: brief, summary: '12 places read.', model: 'Main', by: 'Mo', mine: true, findings: [finding('f1', {}), finding('f2', { title: 'A constant query', verified: false, verifyNote: 'The value is a constant.' })] },
      }),
      'PUT /api/code/security/s1/findings/f1': () => ({ status: 204 }),
    })
    renderApp('/code/security/s1')
    const list = await screen.findByRole('list', { name: 'Weaknesses' })
    const card = within(list).getByText('SQL built from the request').closest('li')!
    expect(within(card).getByRole('link', { name: 'CWE-89' })).toHaveAttribute('href', 'https://cwe.mitre.org/data/definitions/89.html')
    expect(within(card).getByLabelText('The sink')).toHaveTextContent('new SqlCommand')
    expect(within(card).getByText('Orders.Find(id)')).toBeInTheDocument()
    expect(within(card).getByRole('link', { name: 'SQL Injection Prevention Cheat Sheet' })).toBeInTheDocument()
    expect(screen.getByText(/Doubted by the second check \(1\)/)).toBeInTheDocument()

    await userEvent.click(within(card).getByRole('button', { name: 'Accept the risk' }))
    const dialog = await screen.findByRole('dialog', { name: 'Accept the risk' })
    const save = within(dialog).getByRole('button', { name: 'Save' })
    expect(save).toBeDisabled()
    await userEvent.type(within(dialog).getByLabelText('Why'), 'Internal tool only')
    await userEvent.click(save)
    await waitFor(() => {
      const body = calls.find((c) => c.method === 'PUT')?.body as { state: string; why: string; until: string }
      expect(body.state).toBe('accepted')
      expect(body.why).toBe('Internal tool only')
      expect(new Date(body.until).getTime()).toBeGreaterThan(Date.now())
    })
  })
})
