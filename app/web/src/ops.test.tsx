import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeBackend, member, renderApp } from './test-utils'

const dashboard = {
  uid: 'usage-by-user',
  title: 'Usage by person',
  time: { from: 'now-30d', to: 'now' },
  panels: [
    { key: 0, type: 'text', title: 'How a key maps to a person', supported: true, datasources: [], gridPos: { x: 0, y: 0, w: 24, h: 4 }, options: { content: 'Keys map to **people**.<script>alert(1)</script>' } },
    { key: 1, type: 'stat', title: 'Cost', supported: true, datasources: ['litellm-db'], gridPos: { x: 0, y: 4, w: 6, h: 4 }, fieldConfig: { defaults: { unit: 'currencyUSD', decimals: 4 } }, options: { reduceOptions: { calcs: ['lastNotNull'] } } },
    { key: 2, type: 'table', title: 'Per person', supported: true, datasources: ['litellm-db'], gridPos: { x: 6, y: 4, w: 18, h: 4 }, fieldConfig: { defaults: {}, overrides: [{ matcher: { id: 'byName', options: 'Cost' }, properties: [{ id: 'unit', value: 'currencyUSD' }] }] } },
    { key: 3, type: 'row', title: 'Engine', supported: true, datasources: [], gridPos: { x: 0, y: 8, w: 24, h: 1 } },
    { key: 4, type: 'timeseries', title: 'GPU busy', supported: false, datasources: ['prometheus'], gridPos: { x: 0, y: 9, w: 24, h: 6 } },
  ],
}

describe('usage', () => {
  it('shows a member their own usage and nothing about others', async () => {
    const calls = fakeBackend(member, {
      'GET /api/usage/me': () => ({ json: { intervalMs: 86400000, totals: { requests: 3, inputMiss: 1200, inputHit: 400, output: 90, cost: 0.0021 }, series: [], models: [{ model: 'qwen', requests: 3, tokens: 1690, cost: 0.0021 }] } }),
    })
    renderApp('/usage')
    expect(await screen.findByLabelText('Input, cache hit')).toHaveTextContent('400')
    expect(screen.getByText('25% of input')).toBeInTheDocument()
    expect(screen.getByLabelText('Cost')).toHaveTextContent('$0.0021')
    expect(screen.queryByRole('tab', { name: 'Everyone' })).not.toBeInTheDocument()
    expect(calls.some((c) => c.path.startsWith('/api/dashboards'))).toBe(false)
    await userEvent.click(screen.getByRole('button', { name: 'Last 7 days' }))
    expect(screen.getByRole('button', { name: 'Last 7 days' })).toHaveAttribute('aria-pressed', 'true')
  })

  it('draws the usage dashboard for an admin, panel by panel, from the server', async () => {
    const calls = fakeBackend(admin, {
      'GET /api/dashboards/usage-by-user': () => ({ json: dashboard }),
      'POST /api/dashboards/usage-by-user/panels/1/query': () => ({ json: { intervalMs: 3600000, results: [{ refId: 'A', format: 'table', table: { columns: [{ name: 'v', type: 'number' }], rows: [[0.0022]], capped: false }, series: null, error: null }] } }),
      'POST /api/dashboards/usage-by-user/panels/2/query': () => ({ json: { intervalMs: 3600000, results: [{ refId: 'A', format: 'table', table: { columns: [{ name: 'Person', type: 'string' }, { name: 'Cost', type: 'number' }], rows: [['alice@example.test', 0.0016]], capped: false }, series: null, error: null }] } }),
    })
    renderApp('/usage')
    expect(await screen.findByRole('tab', { name: 'Everyone' })).toHaveAttribute('aria-selected', 'true')
    expect(await screen.findByLabelText('Cost', { selector: '.stat-value' })).toHaveTextContent('$0.0022')
    const table = await screen.findByRole('region', { name: 'Per person' })
    expect(await within(table).findByText('$0.0016')).toBeInTheDocument()
    // Markdown is rendered, and a script in it is not.
    expect(screen.getByText('people').tagName).toBe('STRONG')
    expect(document.querySelector('script')).toBeNull()
    // A Prometheus panel says where it is until phase 5, and is never queried.
    expect(within(screen.getByRole('region', { name: 'GPU busy' })).getByText(/moves into the app in phase 5/)).toBeInTheDocument()
    expect(calls.some((c) => c.path.includes('/panels/4/'))).toBe(false)
    expect(screen.getByRole('heading', { level: 2, name: 'Engine' })).toBeInTheDocument()
    // The query carries a time range and an interval, never SQL.
    const q = calls.find((c) => c.path.endsWith('/panels/1/query'))!
    expect(Object.keys(q.body as object).sort()).toEqual(['from', 'intervalMs', 'to'])
  })

  it('shows a panel error in place instead of breaking the page', async () => {
    fakeBackend(admin, {
      'GET /api/dashboards/usage-by-user': () => ({ json: { ...dashboard, panels: [dashboard.panels[1]] } }),
      'POST /api/dashboards/usage-by-user/panels/1/query': () => ({ json: { intervalMs: 1, results: [{ refId: 'A', format: 'table', table: null, series: null, error: 'relation does not exist' }] } }),
    })
    renderApp('/usage')
    expect(await screen.findByRole('alert')).toHaveTextContent('relation does not exist')
  })
})

describe('admin operations', () => {
  it('overview raises the stale index with its cause', async () => {
    fakeBackend(admin, {
      'GET /api/admin/overview': () => ({
        json: {
          people: 5, admins: 1, spend: 1.25, overCredit: ['zoe'], warning: null,
          services: [{ name: 'Model gateway', purpose: 'LiteLLM', ok: true, detail: '200 · 3 ms' }, { name: 'Argus', purpose: 'the code index', ok: false, detail: 'unreachable' }],
          index: { configured: true, summary: { repos: 4, stale: 2, never_run: 0, stale_names: ['g/a@main', 'g/b@main'], returncode: 3 }, error: null },
          model: 'qwen',
        },
      }),
    })
    renderApp('/admin')
    expect(await screen.findByLabelText('Services up')).toHaveTextContent('1/2')
    expect(screen.getByLabelText('Code index')).toHaveTextContent('2/4')
    expect(screen.getByText(/2 repositories have a stale index/)).toBeInTheDocument()
    expect(screen.getByText(/could not reach GitLab/)).toBeInTheDocument()
    expect(screen.getByText(/1 at or past their credit/)).toBeInTheDocument()
  })

  it('model page shows the running model and the block to paste for each sample', async () => {
    fakeBackend(admin, {
      'GET /api/admin/model': () => ({
        json: {
          running: { name: 'Qwen3.8-27B', file: 'q.gguf', context: '262144', maxOutput: '32768', mtpDraftMax: '2', gpuPowerLimitW: '150', cpuPowerLimitW: null, thinkingPresets: 'low,high' },
          prices: { input: '0.20', cachedInput: '0.02', output: '0.80' },
          samples: [{ file: 'a.env', title: '27B on a 5090', hardware: 'RTX 5090', download: '17 GB', measured: null, status: null, model: 'Qwen3.8-27B', block: '# >>> MODEL\nMODEL_NAME=Qwen3.8-27B\n# <<< MODEL' }],
        },
      }),
    })
    renderApp('/admin/model')
    expect(await screen.findByText('on (2 draft tokens)')).toBeInTheDocument()
    expect(screen.getByText('running')).toBeInTheDocument()
    expect(screen.getByText(/MODEL_NAME=Qwen3.8-27B/)).toBeInTheDocument()
    expect(screen.getByText(/input \$0.20 · cached input \$0.02 · output \$0.80/)).toBeInTheDocument()
  })

  it('indexing starts a run with the branches typed', async () => {
    const calls = fakeBackend(admin, {
      'GET /api/admin/argus/status': () => ({
        json: {
          job: { state: 'idle', branches: [], started: null, finished: 1790000000, returncode: 1, tail: ['repo x failed'], trigger: 'manual' },
          repos: [{ repo: 'g/app', branch: 'main', default_branch: 'main', last_run_at: null, timed_out: true, symbols_failed: 0 }],
          index: { repos: 1, stale: 1, errored: 1, files: 10, symbols: 100 }, interval: 900, webhook: true, pending: [],
        },
      }),
      'POST /api/admin/argus/index': () => ({ json: { status: 'started' } }),
    })
    renderApp('/admin/indexing')
    expect(await screen.findByText(/every 15 minutes/)).toBeInTheDocument()
    expect(screen.getByText(/at least one repository is unhealthy/)).toBeInTheDocument()
    expect(screen.getByText('timed out')).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText(/Branches/), 'develop, release/*')
    await userEvent.click(screen.getByRole('button', { name: 'Index now' }))
    expect(calls.find((c) => c.path === '/api/admin/argus/index')!.body).toEqual({ branches: ['develop', 'release/*'], allowPartial: false })
  })

  it('an Argus-less deployment says how to turn it on', async () => {
    fakeBackend(admin, { 'GET /api/admin/argus/status': () => ({ json: { configured: false } }) })
    renderApp('/admin/indexing')
    expect(await screen.findByText(/Argus is not set up in this deployment/)).toBeInTheDocument()
  })

  it('settings never render a secret-looking value and say where values come from', async () => {
    fakeBackend(admin, {
      'GET /api/admin/settings': () => ({ json: [{ title: 'Model', rows: [{ name: 'MODEL_NAME', value: 'qwen', purpose: 'what the gateway serves' }, { name: 'THINKING_PRESETS', value: null, purpose: 'presets' }] }] }),
    })
    renderApp('/admin/settings')
    expect(await screen.findByText('qwen')).toBeInTheDocument()
    expect(screen.getByText('—')).toBeInTheDocument()
    expect(screen.getByText(/Secrets are never shown/)).toBeInTheDocument()
  })
})
