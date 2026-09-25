import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, member, renderApp } from '@/test/utils'

const line = (nanos: string, container: string, text: string, level?: string) => ({
  time: Number(BigInt(nanos) / 1_000_000n),
  nanos,
  labels: { container, ...(level ? { detected_level: level } : {}) },
  line: text,
})

const lokiQuery = (url: URL) => `{container=~".+"}${url.searchParams.get('search') ? ` |~ "(?i)${url.searchParams.get('search')}"` : ''}`

describe('dashboards', () => {
  it('lists the dashboards but usage, opens one, and sends its variables with each panel', async () => {
    const calls = fakeApi(admin, {
      'GET /api/dashboards/': () => ({ json: [{ uid: 'stack-logs', title: 'Stack Logs', panels: 1, supported: 1 }, { uid: 'usage-by-user', title: 'Usage by user', panels: 5, supported: 5 }] }),
      'GET /api/dashboards/stack-logs': () => ({
        json: {
          uid: 'stack-logs', title: 'Stack Logs', time: { from: 'now-1h', to: 'now' },
          panels: [{ key: 0, type: 'stat', title: 'Lines written', gridPos: { x: 0, y: 0, w: 6, h: 4 }, supported: true, datasources: ['loki'] }],
          variables: [{ name: 'container', label: 'Container', type: 'query', multi: true, includeAll: true, current: ['$__all'] }],
        },
      }),
      'GET /api/dashboards/stack-logs/variables': () => ({ json: [{ name: 'container', options: ['app', 'web'], error: null }] }),
      'POST /api/dashboards/stack-logs/panels/0/query': () => ({ json: { intervalMs: 1000, results: [{ refId: 'A', format: 'time_series', table: null, series: [{ name: 'lines', points: [[1, 42]] }], error: null }] } }),
    })
    const { router } = renderApp('/admin/dashboards')
    await userEvent.click(await screen.findByRole('link', { name: /Stack Logs/ }))
    expect(screen.queryByText('Usage by user')).not.toBeInTheDocument()
    await waitFor(() => expect(router.state.location.pathname).toBe('/admin/dashboards/stack-logs'))
    expect(await screen.findByText('42')).toBeInTheDocument()

    // The options are asked for over times, not "now-1h".
    expect(new URL(calls.find((c) => c.path.startsWith('/api/dashboards/stack-logs/variables'))!.path, 'https://x').searchParams.get('from')).toMatch(/^\d{4}-\d\d-\d\dT/)
    await userEvent.click(screen.getByRole('button', { name: 'Container' }))
    await userEvent.click(await screen.findByRole('menuitemcheckbox', { name: 'web' }))
    await waitFor(() => expect(calls.filter((c) => c.path.endsWith('/query')).at(-1)?.body).toMatchObject({ vars: { container: ['web'] } }))
  })

  it('is for admins only', async () => {
    fakeApi(member)
    renderApp('/admin/dashboards')
    expect(await screen.findByRole('heading', { name: 'Admins only' })).toBeInTheDocument()
  })
})

describe('logs', () => {
  it('shows lines newest first with their container, and filters by level, text and container in the address', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/logs/containers': () => ({ json: { containers: ['app', 'web'] } }),
      'GET /api/admin/logs/': (_b, _i, url) => ({
        json: { query: lokiQuery(url), limit: 1000, more: false, lines: [line('1790000060000000000', 'app', 'request failed: boom', 'error'), line('1790000000000000000', 'web', 'GET / 200')] },
      }),
      'GET /api/admin/logs/volume': () => ({ json: { intervalMs: 60000, series: [{ name: 'error', points: [[1790000000000, 1]] }, { name: 'other', points: [[1790000000000, 1]] }] } }),
    })
    const { router } = renderApp('/admin/logs')
    const region = await screen.findByRole('region', { name: 'Logs, log lines' })
    const items = within(region).getAllByRole('listitem')
    expect(items[0]).toHaveTextContent('app')
    expect(items[0]).toHaveTextContent('request failed: boom')
    expect(items[1]).toHaveTextContent('GET / 200')

    await userEvent.click(screen.getByRole('radio', { name: 'Errors' }))
    await waitFor(() => expect(calls.some((c) => c.path.startsWith('/api/admin/logs/?') && c.path.includes('level=error'))).toBe(true))
    expect(router.state.location.search).toContain('level=error')

    await userEvent.type(screen.getByLabelText('Contains'), 'boom')
    await waitFor(() => expect(calls.some((c) => c.path.startsWith('/api/admin/logs/?') && c.path.includes('search=boom'))).toBe(true))
    expect(await screen.findByText('boom', { selector: 'mark' })).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Containers' }))
    await userEvent.click(await screen.findByRole('menuitemcheckbox', { name: 'web' }))
    await waitFor(() => expect(calls.some((c) => c.path.includes('container=web'))).toBe(true))
    expect(router.state.location.search).toContain('container=web')

    // The query sent to Loki is there to read.
    await userEvent.click(screen.getByText('The query sent to Loki'))
    expect(screen.getByText(/\(\?i\)boom/)).toBeInTheDocument()
  })

  it('live adds the lines written after the newest one shown', async () => {
    let tail = 0
    const calls = fakeApi(admin, {
      'GET /api/admin/logs/containers': () => ({ json: { containers: ['app'] } }),
      'GET /api/admin/logs/': (_b, _i, url) =>
        url.searchParams.get('after')
          ? { json: { query: '', limit: 500, more: false, lines: tail++ === 0 ? [line('1790000120000000000', 'app', 'a new line')] : [] } }
          : { json: { query: '', limit: 1000, more: false, lines: [line('1790000060000000000', 'app', 'an old line')] } },
      'GET /api/admin/logs/volume': () => ({ json: { intervalMs: 60000, series: [] } }),
    })
    renderApp('/admin/logs')
    await screen.findByText('an old line')
    await userEvent.click(screen.getByRole('button', { name: 'Live' }))
    expect(screen.getByRole('button', { name: 'Live' })).toHaveAttribute('aria-pressed', 'true')
    expect(await screen.findByText('a new line', undefined, { timeout: 6000 })).toBeInTheDocument()
    expect(calls.find((c) => c.path.includes('after='))?.path).toContain('after=1790000060000000000')
    const items = within(screen.getByRole('region', { name: 'Logs, log lines' })).getAllByRole('listitem')
    expect(items[0]).toHaveTextContent('a new line')
    expect(items[1]).toHaveTextContent('an old line')
  }, 10_000)

  it('says when Loki is not there', async () => {
    fakeApi(admin, {
      'GET /api/admin/logs/containers': () => ({ status: 503, json: { status: 'loki', error: 'Loki is not reachable: the logs need the logging profile.' } }),
      'GET /api/admin/logs/': () => ({ status: 503, json: { status: 'loki', error: 'Loki is not reachable: the logs need the logging profile.' } }),
      'GET /api/admin/logs/volume': () => ({ status: 503, json: { status: 'loki', error: 'Loki is not reachable: the logs need the logging profile.' } }),
    })
    renderApp('/admin/logs')
    expect((await screen.findAllByText(/Loki is not reachable/)).length).toBeGreaterThan(0)
  })
})

const rule = (over: object) => ({
  group: 'stack', name: 'TargetDown', severity: 'critical', state: 'inactive', health: 'ok', lastError: null, query: 'up == 0', for: 300,
  summary: 'Target {{ $labels.job }} is down', description: 'Scrapes fail.', active: 0, activeAt: null, ...over,
})

describe('alerts', () => {
  it('shows what fires, what fired, and the rules', async () => {
    fakeApi(admin, {
      'GET /api/admin/alerts/': () => ({
        json: {
          firing: [
            { name: 'TargetDown', severity: 'critical', state: 'active', startsAt: new Date(Date.now() - 600_000).toISOString(), summary: 'Target loki is down', description: 'Scrapes fail.', labels: { alertname: 'TargetDown', severity: 'critical', job: 'loki' }, silencedBy: [], inhibitedBy: [] },
            { name: 'GpuHot', severity: 'warning', state: 'suppressed', startsAt: new Date().toISOString(), summary: 'GPU hot', description: null, labels: { alertname: 'GpuHot' }, silencedBy: ['s1'], inhibitedBy: [] },
          ],
          rules: [
            rule({ state: 'firing', active: 1, activeAt: new Date().toISOString() }),
            rule({ group: 'gpu', name: 'GpuHot', severity: 'warning', state: 'pending', query: 'gpu_temp > 85' }),
            rule({ group: 'gpu', name: 'GpuFan', severity: 'warning', health: 'err', lastError: 'bad metric', query: 'fan == 0' }),
            rule({ group: 'host', name: 'DiskFull', severity: 'warning' }),
          ],
          errors: { alertmanager: null, prometheus: null },
        },
      }),
      'GET /api/admin/alerts/history': () => ({
        json: {
          from: new Date(Date.now() - 7 * 86_400_000).toISOString(), to: new Date().toISOString(),
          episodes: [
            { name: 'TargetDown', severity: 'critical', labels: { alertname: 'TargetDown', job: 'loki' }, start: new Date(Date.now() - 600_000).toISOString(), end: null, summary: 'Target loki is down' },
            { name: 'EngineDown', severity: 'critical', labels: { alertname: 'EngineDown' }, start: new Date(Date.now() - 86_400_000).toISOString(), end: new Date(Date.now() - 86_400_000 + 1_800_000).toISOString(), summary: 'The engine is down' },
          ],
        },
      }),
    })
    renderApp('/admin/alerts')
    // Once firing now, once in the history.
    expect(await screen.findAllByText('Target loki is down')).toHaveLength(2)
    expect(screen.getByText('Silenced')).toBeInTheDocument()

    expect(screen.getByText('Still firing')).toBeInTheDocument()
    expect(screen.getByText('The engine is down')).toBeInTheDocument()
    expect(screen.getByText(/30 minutes/)).toBeInTheDocument()

    // Rules by group; the filter keeps what is pending, firing or failing.
    expect(screen.getByRole('heading', { name: 'host' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('radio', { name: 'Pending, firing or failing' }))
    expect(screen.queryByRole('heading', { name: 'host' })).not.toBeInTheDocument()
    expect(screen.getByText('Cannot be evaluated')).toBeInTheDocument()
    await userEvent.click(screen.getByText('GpuFan'))
    expect(screen.getByText('bad metric')).toBeVisible()
    expect(screen.getByText('fan == 0')).toBeVisible()
  })

  it('still shows the rules when Alertmanager does not answer, and says nothing fires when nothing does', async () => {
    fakeApi(admin, {
      'GET /api/admin/alerts/': () => ({ json: { firing: null, rules: [rule({})], errors: { alertmanager: 'Alertmanager is not reachable.', prometheus: null } } }),
      'GET /api/admin/alerts/history': () => ({ json: { from: new Date().toISOString(), to: new Date().toISOString(), episodes: [] } }),
    })
    renderApp('/admin/alerts')
    expect(await screen.findByText('Alertmanager is not reachable.')).toBeInTheDocument()
    expect(screen.getByText('TargetDown')).toBeInTheDocument()
    expect(screen.getByText('Nothing fired')).toBeInTheDocument()
  })
})
