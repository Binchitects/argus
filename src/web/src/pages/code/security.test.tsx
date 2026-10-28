import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

const brief = {
  id: 's1', project: 'g/shop', ref: 'main', commit: 'abc1234567890def', status: 'done', step: null, candidates: 12, read: 12, dropped: 1, remembered: 0, dependencies: 40, advisoriesAsOf: '2026-10-01', sbom: true,
  scanners: null, recheckedAt: null, createdAt: '2026-10-27T00:00:00Z', finishedAt: '2026-10-01T00:10:00Z', error: null,
}

const finding = (id: string, over: object) => ({
  id, kind: 'code', foundBy: ['model'], rule: null, commit: null, exploited: false, path: 'src/Orders.cs', line: 4, sinkCode: 'var cmd = new SqlCommand("SELECT ... " + id, conn);', cwe: 'CWE-89', cweTitle: 'CWE-89: SQL Injection (Base)', severity: 'high',
  title: 'SQL built from the request', source: 'Controller.cs:9 Request.Query["id"]', flow: 'Request.Query["id"]\nOrders.Find(id)', exploit: "id = ' OR '1'='1", fix: 'Use a parameter: `@id`.',
  pages: [{ title: 'CWE-89: SQL Injection (Base)', url: 'https://cwe.mitre.org/data/definitions/89.html' }, { title: 'SQL Injection Prevention Cheat Sheet', url: 'https://cheatsheetseries.owasp.org/x.html' }],
  verified: true, verifyNote: 'The id goes into the text.', state: 'open', decidedAt: null, why: null, until: null, foundAt: '2026-10-01T00:09:00Z', ...over,
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
        json: {
          scan: { ...brief, recheckedAt: '2026-10-02T01:00:00Z' }, summary: '12 places read.', model: 'Main', by: 'Mo', mine: true,
          findings: [
            finding('p1', {
              kind: 'dependency', exploited: true, path: 'api/packages.lock.json', line: 7, sinkCode: 'Newtonsoft.Json 12.0.3 (NuGet)', cwe: 'CWE-755', title: 'Improper Handling of Exceptional Conditions',
              source: 'GHSA-5crp-9r3c-p9vr, CVE-2024-21907', flow: 'Newtonsoft.Json comes in through Some.Lib', exploit: 'On CISA’s list of known exploited vulnerabilities.', fix: 'Upgrade Newtonsoft.Json to 13.0.1 or later.',
              pages: [{ title: 'GHSA-5crp-9r3c-p9vr', url: 'https://github.com/advisories/GHSA-5crp-9r3c-p9vr' }], foundAt: '2026-10-02T01:00:00Z',
            }),
            finding('f1', {}),
            finding('f2', { title: 'A constant query', verified: false, verifyNote: 'The value is a constant.' }),
          ],
        },
      }),
      'PUT /api/code/security/s1/findings/f1': () => ({ status: 204 }),
    })
    const { router } = renderApp('/code/security/s1')
    const list = await screen.findByRole('list', { name: 'Weaknesses' })
    const card = within(list).getByText('SQL built from the request').closest('li')!
    expect(within(card).getByRole('link', { name: 'CWE-89' })).toHaveAttribute('href', 'https://cwe.mitre.org/data/definitions/89.html')
    expect(within(card).getByLabelText('The sink')).toHaveTextContent('new SqlCommand')
    expect(within(card).getByText('Orders.Find(id)')).toBeInTheDocument()
    expect(within(card).getByRole('link', { name: 'SQL Injection Prevention Cheat Sheet' })).toBeInTheDocument()
    expect(screen.getByText(/Doubted by the model \(1\)/)).toBeInTheDocument()
    expect(screen.getByText('No security scanners here (an admin turns the scanners feature on)')).toBeInTheDocument()
    const packages = screen.getByRole('list', { name: 'Vulnerable packages' })
    expect(within(packages).getByText('known exploited')).toBeInTheDocument()
    expect(within(packages).getByLabelText('The package')).toHaveTextContent('Newtonsoft.Json 12.0.3 (NuGet)')
    expect(within(packages).getByText('Newtonsoft.Json comes in through Some.Lib')).toBeInTheDocument()
    expect(within(packages).getByText('new since the review')).toBeInTheDocument()
    expect(screen.getByText(/Checked again .*, against the advisories of 2026-10-01, at the commit reviewed/)).toBeInTheDocument()
    expect(within(list).queryByText('new since the review')).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Vulnerable packages (1 of 40)' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Bill of materials' })).toHaveAttribute('href', '/api/code/security/s1/sbom')
    expect(screen.getByText(/advisories its packages were checked against are \d+ days old/)).toBeInTheDocument()

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
    expect(router.state.location.pathname).toBe('/code/security/s1')
  })

  it('starts Fix it on a finding, from the branch reviewed', async () => {
    const calls = fakeApi(member, {
      'GET /api/code/security/s1': () => ({ json: { scan: brief, summary: null, model: 'Main', by: 'Mo', mine: true, findings: [finding('f1', {})] } }),
      'POST /api/fixes': () => ({ status: 202, json: { id: 'x1' } }),
      'GET /api/fixes/x1': () => ({ json: { id: 'x1', project: 'g/shop', branch: 'main', title: 'CWE-89 SQL', ask: '', source: 'security', status: 'working', step: 'Starting', log: [], files: [] } }),
      'GET /api/fixes/token': () => ({ json: { linked: true } }),
    })
    const { router } = renderApp('/code/security/s1')
    const list = await screen.findByRole('list', { name: 'Weaknesses' })
    await userEvent.click(within(list).getByRole('button', { name: 'Fix it' }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/code/fixes/x1'))
    expect(calls.find((c) => c.path === '/api/fixes' && c.method === 'POST')?.body).toEqual({ securityFindingId: 'f1' })
  })

  it('fixes the findings chosen at once, a fix each, the rest waiting their turn', async () => {
    const calls = fakeApi(member, {
      'GET /api/code/security/s1': () => ({
        json: { scan: brief, summary: null, model: 'Main', by: 'Mo', mine: true, findings: [finding('f1', {}), finding('f2', { line: 9, title: 'Another query' }), finding('f3', { title: 'Settled', state: 'fixed' })] },
      }),
      'POST /api/fixes/batch': () => ({ json: { fixes: [{ id: 'x1' }, { id: 'x2' }], queued: 0 } }),
      'GET /api/fixes': () => ({ json: [] }),
      'GET /api/fixes/token': () => ({ json: { linked: true } }),
    })
    const { router } = renderApp('/code/security/s1')
    const list = await screen.findByRole('list', { name: 'Weaknesses' })
    expect(screen.queryByRole('region', { name: 'Fix them' })).not.toBeInTheDocument()
    // A settled finding is not chosen from.
    expect(within(list).queryByRole('checkbox', { name: 'Choose “Settled” to fix' })).not.toBeInTheDocument()
    await userEvent.click(within(list).getByRole('checkbox', { name: 'Choose “SQL built from the request” to fix' }))
    await userEvent.click(within(list).getByRole('checkbox', { name: 'Choose “Another query” to fix' }))
    const bar = screen.getByRole('region', { name: 'Fix them' })
    expect(bar).toHaveTextContent('2 chosen')
    await userEvent.click(within(bar).getByRole('button', { name: 'Fix them' }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/code/fixes'))
    expect(calls.find((c) => c.path === '/api/fixes/batch')?.body).toEqual({ securityFindingIds: ['f1', 'f2'] })
  })

  it('shows what the scanners found beside the model, secrets and configuration apart, and filters by who found it', async () => {
    const tools = [
      { name: 'opengrep', version: '1.31.0', ran: true, seconds: 2, findings: 3, error: null },
      { name: 'gitleaks', version: '8.30.1', ran: true, seconds: 1, findings: 1, error: null },
      { name: 'trivy', version: '0.75.0', ran: false, seconds: 0, findings: 0, error: 'trivy did not finish within 900 s' },
    ]
    fakeApi(member, {
      'GET /api/code/security/s1': () => ({
        json: {
          scan: { ...brief, scanners: { tools, history: true, found: 4 } }, summary: 'Scanners ran.', model: 'Main', by: 'Mo', mine: true,
          findings: [
            finding('f1', { foundBy: ['model', 'opengrep'], rule: 'csharp-sql-built-from-strings' }),
            finding('f2', { title: 'A broken algorithm', foundBy: ['opengrep'], rule: 'csharp-weak-crypto', cwe: 'CWE-327', severity: 'low', verified: null, verifyNote: null, path: 'src/Crypto.cs' }),
            finding('f3', {
              kind: 'secret', title: 'A secret (aws-access-token) in the history, commit c0ffee000000', foundBy: ['gitleaks'], rule: 'aws-access-token', cwe: 'CWE-798', sinkCode: '', verified: null,
              verifyNote: null, path: 'config/app.env', commit: 'c0ffee0000000000000000000000000000000000', pages: [],
            }),
            finding('f4', { kind: 'config', title: 'Image user should not be root', foundBy: ['trivy'], rule: 'AVD-DS-0002', cwe: '', verified: null, verifyNote: null, path: 'Dockerfile', line: 1, pages: [] }),
          ],
        },
      }),
    })
    renderApp('/code/security/s1')
    const list = await screen.findByRole('list', { name: 'Weaknesses' })
    const both = within(list).getByText('SQL built from the request').closest('li')!
    expect(within(within(both).getByLabelText('Found by')).getByText('model')).toBeInTheDocument()
    expect(within(within(both).getByLabelText('Found by')).getByText('opengrep')).toBeInTheDocument()
    const weak = within(list).getByText('A broken algorithm').closest('li')!
    expect(within(weak).getByText(/not traced by the model/)).toBeInTheDocument()
    const secrets = screen.getByRole('list', { name: 'Secrets' })
    expect(within(secrets).getByLabelText('Where the secret is')).toHaveTextContent('commit c0ffee000000')
    expect(within(secrets).queryByLabelText('The sink')).not.toBeInTheDocument()
    expect(within(screen.getByRole('list', { name: 'Configuration' })).getByText('Image user should not be root')).toBeInTheDocument()
    const scanners = screen.getByLabelText('The scanners')
    expect(scanners).toHaveTextContent('gitleaks 8.30.1 (with the history)')
    expect(within(scanners).getByText(/trivy/)).toHaveAttribute('title', 'trivy did not finish within 900 s')
    expect(screen.getByRole('link', { name: 'SARIF' })).toHaveAttribute('href', '/api/code/security/s1/sarif')
    expect(screen.getByText('abc123456789')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Scanners only' }))
    expect(within(screen.getByRole('list', { name: 'Weaknesses' })).queryByText('SQL built from the request')).not.toBeInTheDocument()
    expect(within(screen.getByRole('list', { name: 'Weaknesses' })).getByText('A broken algorithm')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Model and scanners' }))
    expect(within(screen.getByRole('list', { name: 'Weaknesses' })).getByText('SQL built from the request')).toBeInTheDocument()
    expect(within(screen.getByRole('list', { name: 'Weaknesses' })).queryByText('A broken algorithm')).not.toBeInTheDocument()
  })
})
