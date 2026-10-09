import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { ems } from '@/components/dashboards/fit'
import { formatValue } from '@/lib/format'
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

  it('keeps a stat with a value per sensor as short as a chart, and an empty chart says its own no-value text', async () => {
    const series = (names: string[]) => ({ json: { intervalMs: 1000, results: [{ refId: 'A', format: 'time_series', table: null, error: null, series: names.map((name, i) => ({ name, points: [[1, 40 + i], [2, 41 + i]] })) }] } })
    const panel = (key: number, type: string, title: string, defaults = {}) => ({ key, type, title, gridPos: { x: key * 4, y: 0, w: 4, h: 7 }, supported: true, datasources: ['prometheus'], options: { graphMode: 'area' }, fieldConfig: { defaults } })
    fakeApi(admin, {
      'GET /api/dashboards/host': () => ({ json: { uid: 'host', title: 'Host', time: { from: 'now-1h', to: 'now' }, panels: [panel(0, 'stat', 'Sensors', { unit: 'celsius' }), panel(1, 'stat', 'Drives'), panel(2, 'timeseries', 'Drives over time', { noValue: 'No NVMe drive' })] } }),
      'POST /api/dashboards/host/panels/0/query': () => series(Array.from({ length: 20 }, (_, i) => `Core ${i}`)),
      'POST /api/dashboards/host/panels/1/query': () => series(['nvme0 · Samsung SSD 990 PRO 2TB', 'nvme1 · Samsung SSD 990 PRO 2TB']),
      'POST /api/dashboards/host/panels/2/query': () => series([]),
    })
    renderApp('/admin/dashboards/host')

    // Twenty values, every one of them, without sparklines, in a box that scrolls past a chart's height on a wide screen.
    const box = await screen.findByRole('region', { name: 'Sensors, every value' })
    expect(within(box).getAllByLabelText(/^Sensors: Core \d+$/)).toHaveLength(20)
    expect(box).toHaveClass('lg:max-h-65')
    expect(box.querySelector('svg')).toBeNull()
    // Two keep their sparklines and need no box. A name wraps to two lines before it is cut, and is whole on hover.
    const drives = screen.getByRole('region', { name: 'Drives' })
    expect(within(drives).queryByRole('region')).toBeNull()
    expect(drives.querySelectorAll('svg[aria-hidden="true"]')).toHaveLength(2)
    const name = within(drives).getByText('nvme1 · Samsung SSD 990 PRO 2TB')
    expect(name).toHaveAttribute('title', 'nvme1 · Samsung SSD 990 PRO 2TB')
    expect(name).toHaveClass('line-clamp-2')
    expect(name).not.toHaveClass('truncate')
    expect(await screen.findByText('No NVMe drive')).toBeInTheDocument()
  })

  it('draws a gauge per GPU small, every one of them, and past two in a box as short as a chart', async () => {
    const series = (names: string[]) => ({ json: { intervalMs: 1000, results: [{ refId: 'A', format: 'time_series', table: null, error: null, series: names.map((name, i) => ({ name, points: [[1, 40 + i]] })) }] } })
    const gauge = (key: number, title: string) => ({ key, type: 'gauge', title, gridPos: { x: key * 4, y: 0, w: 4, h: 5 }, supported: true, datasources: ['prometheus'], fieldConfig: { defaults: { unit: 'celsius' } } })
    const gpus = (n: number) => Array.from({ length: n }, (_, i) => `GPU ${i} · GeForce RTX 3090`)
    fakeApi(admin, {
      'GET /api/dashboards/gpus': () => ({ json: { uid: 'gpus', title: 'GPUs', time: { from: 'now-1h', to: 'now' }, panels: [gauge(0, 'One'), gauge(1, 'Two'), gauge(2, 'Ten')] } }),
      'POST /api/dashboards/gpus/panels/0/query': () => series(['{uuid="u1"}']),
      'POST /api/dashboards/gpus/panels/1/query': () => series(gpus(2)),
      'POST /api/dashboards/gpus/panels/2/query': () => series(gpus(10)),
    })
    renderApp('/admin/dashboards/gpus')

    // One GPU: one full-size gauge named by its panel.
    const one = await screen.findByRole('region', { name: 'One' })
    expect(await within(one).findByRole('figure', { name: /^One: 40/ })).toHaveClass('max-w-[130px]')
    // Two: small, side by side where they fit, each named whole on hover, and no box.
    const two = screen.getByRole('region', { name: 'Two' })
    expect(within(two).queryByRole('region')).toBeNull()
    const second = await within(two).findByRole('figure', { name: /^GPU 1 · GeForce RTX 3090: 41/ })
    expect(second).toHaveClass('max-w-[93px]')
    expect(within(two).getByText('GPU 1 · GeForce RTX 3090')).toHaveAttribute('title', 'GPU 1 · GeForce RTX 3090')
    // Ten: every one, in a box that scrolls past a chart's height on a wide screen.
    const box = await screen.findByRole('region', { name: 'Ten, every value' })
    expect(box).toHaveClass('lg:max-h-65')
    expect(within(box).getAllByRole('figure')).toHaveLength(10)
  })

  it("fits a value to its own cell rather than running past the panel, and sizes a panel's values alike", async () => {
    const values = (pairs: [string, number][]) => ({ json: { intervalMs: 1000, results: [{ refId: 'A', format: 'time_series', table: null, error: null, series: pairs.map(([name, v]) => ({ name, points: [[1, v]] })) }] } })
    const panel = (key: number, type: string, title: string, unit: string) => ({ key, type, title, gridPos: { x: key * 4, y: 0, w: 4, h: 7 }, supported: true, datasources: ['prometheus'], fieldConfig: { defaults: { unit } } })
    fakeApi(admin, {
      'GET /api/dashboards/fit': () => ({ json: { uid: 'fit', title: 'Fit', time: { from: 'now-1h', to: 'now' }, panels: [panel(0, 'stat', 'Power', 'watt'), panel(1, 'gauge', 'Heat', 'celsius'), panel(2, 'stat', 'Drives', 'celsius')] } }),
      'POST /api/dashboards/fit/panels/0/query': () => values([['draw', 35], ['limit', 150]]),
      'POST /api/dashboards/fit/panels/1/query': () => values([['GPU 0', 52], ['GPU 1', 48.4]]),
      'POST /api/dashboards/fit/panels/2/query': () => values(Array.from({ length: 6 }, (_, i): [string, number] => [`nvme${i} · WD_BLACK SN850X 1000GB`, 50 + i])),
    })
    renderApp('/admin/dashboards/fit')

    // Two values: columns of 7rem only where the cell has that much, else one under the other, never wider than it.
    // Each value fits its own column (a container), at the size the widest of them needs, up to the usual size.
    const power = await screen.findByRole('region', { name: 'Power' })
    const shown = await within(power).findAllByLabelText(/^Power: /)
    expect(shown.map((v) => v.textContent)).toEqual(['35 W', '150 W'])
    expect(shown[0]!.closest('.grid.gap-3')).toHaveClass('grid-cols-[repeat(auto-fit,minmax(min(7rem,100%),1fr))]')
    for (const v of shown) {
      expect(v.parentElement).toHaveClass('@container')
      expect(v).toHaveClass('text-[length:min(1.5rem,var(--fit))]', 'xl:text-[length:min(1.75rem,var(--fit))]', 'whitespace-nowrap')
      expect(v.style.getPropertyValue('--fit')).toBe(`calc(100cqi / ${ems('150 W').toFixed(2)})`)
    }
    // Many: the same, in the box that scrolls up and down; nothing in it is wider than the cell, so it never scrolls sideways.
    const drives = await screen.findByRole('region', { name: 'Drives, every value' })
    expect(drives.firstElementChild).toHaveClass('grid-cols-[repeat(auto-fit,minmax(min(7rem,100%),1fr))]')
    expect(within(drives).getAllByLabelText(/^Drives: nvme\d/)).toHaveLength(6)

    // Gauges: each ring is its cell's width up to its full size, and its caption keeps inside the ring's ends
    // (0.62 of it), every caption of the panel at one size.
    const heat = screen.getByRole('region', { name: 'Heat' })
    const figures = await within(heat).findAllByRole('figure')
    expect(figures[0]!.parentElement!.parentElement).toHaveClass('grid-cols-[repeat(auto-fit,minmax(min(4.5rem,100%),1fr))]')
    const widest = Math.max(ems(formatValue(52, 'celsius')), ems(formatValue(48.4, 'celsius')))
    for (const f of figures) {
      expect(f).toHaveClass('@container', 'w-full', 'aspect-[100/78]')
      expect(f.querySelector('figcaption')!.style.getPropertyValue('--fit')).toBe(`calc(100cqi * 0.62 / ${widest.toFixed(2)})`)
    }
  })

  it('shows a chart as a table from a button that is only its icon in a narrow panel, and keeps a cut title whole on hover', async () => {
    const title = 'NVMe temperature over time, every drive'
    fakeApi(admin, {
      'GET /api/dashboards/drives': () => ({ json: { uid: 'drives', title: 'Drives', time: { from: 'now-1h', to: 'now' }, panels: [{ key: 0, type: 'timeseries', title, gridPos: { x: 0, y: 0, w: 8, h: 8 }, supported: true, datasources: ['prometheus'], fieldConfig: { defaults: { unit: 'celsius' } } }] } }),
      'POST /api/dashboards/drives/panels/0/query': () => ({ json: { intervalMs: 1000, results: [{ refId: 'A', format: 'time_series', table: null, error: null, series: [{ name: 'nvme0 · Drive A', points: [[1, 40]] }, { name: 'nvme1 · Drive B', points: [[1, 50]] }] }] } }),
    })
    renderApp('/admin/dashboards/drives')

    const panel = await screen.findByRole('region', { name: title })
    expect(panel).toHaveClass('@container')
    expect(within(panel).getByText(title)).toHaveAttribute('title', title)
    const button = await within(panel).findByRole('button', { name: 'Show as table' })
    expect(within(button).getByText('Show as table')).toHaveClass('hidden', '@sm:inline')
    await userEvent.click(button)
    expect(await within(panel).findByText('nvme1 · Drive B')).toBeInTheDocument()
    expect(within(panel).getByRole('button', { name: 'Show chart' })).toBeInTheDocument()
  })

  it('opens on the last hour, and a range chosen goes in the address, so a link opens the same view', async () => {
    const result = { json: { intervalMs: 1000, results: [{ refId: 'A', format: 'time_series', table: null, error: null, series: [{ name: 'up', points: [[1, 1]] }] }] } }
    const def = (uid: string, time?: { from: string; to: string }) => ({
      json: { uid, title: uid, ...(time ? { time } : {}), panels: [{ key: 0, type: 'stat', title: 'Up', gridPos: { x: 0, y: 0, w: 6, h: 4 }, supported: true, datasources: ['prometheus'] }] },
    })
    const calls = fakeApi(admin, {
      'GET /api/dashboards/plain': () => def('plain'),
      'GET /api/dashboards/day': () => def('day', { from: 'now-24h', to: 'now' }),
      'POST /api/dashboards/plain/panels/0/query': () => result,
      'POST /api/dashboards/day/panels/0/query': () => result,
    })
    /** The span of the last query a panel of this dashboard made, in minutes. */
    const asked = (uid: string) => {
      const body = calls.filter((c) => c.path === `/api/dashboards/${uid}/panels/0/query`).at(-1)?.body as { from: string; to: string } | undefined
      return body ? Math.round((Date.parse(body.to) - Date.parse(body.from)) / 60_000) : null
    }

    // A dashboard whose file gives no range: the last hour, never an empty menu.
    const { router, unmount } = renderApp('/admin/dashboards/plain')
    const range = await screen.findByRole('combobox', { name: 'Time range' })
    expect(range).toHaveTextContent('Last hour')
    await waitFor(() => expect(asked('plain')).toBe(60))
    expect(router.state.location.search).toBe('')

    await userEvent.click(range)
    expect((await screen.findAllByRole('option')).map((o) => o.textContent)).toEqual(['Last 5 minutes', 'Last 15 minutes', 'Last hour', 'Last 6 hours', 'Last 24 hours', 'Last 7 days', 'Last 30 days', 'Last 90 days'])
    await userEvent.click(screen.getByRole('option', { name: 'Last 6 hours' }))
    await waitFor(() => expect(asked('plain')).toBe(360))
    expect(new URLSearchParams(router.state.location.search).get('from')).toBe('now-6h')
    expect(new URLSearchParams(router.state.location.search).get('to')).toBe('now')
    unmount()

    // A file's own range stands until another is chosen; a link's range, even one not in the menu, is the view it opens.
    const day = renderApp('/admin/dashboards/day')
    expect(await screen.findByRole('combobox', { name: 'Time range' })).toHaveTextContent('Last 24 hours')
    day.unmount()
    renderApp('/admin/dashboards/day?from=now-30m&to=now')
    expect(await screen.findByRole('combobox', { name: 'Time range' })).toHaveTextContent('Last 30 minutes')
    await waitFor(() => expect(asked('day')).toBe(30))
  })

  it('a range in the address that cannot be read is the last hour', async () => {
    fakeApi(admin, {
      'GET /api/dashboards/plain': () => ({ json: { uid: 'plain', title: 'plain', panels: [] } }),
    })
    renderApp('/admin/dashboards/plain?from=yesterday-ish&to=now')
    expect(await screen.findByRole('combobox', { name: 'Time range' })).toHaveTextContent('Last hour')
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
