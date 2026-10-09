import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, type Handler, renderApp } from '@/test/utils'
import type { RepoRow } from './argus-repos'
import { scheduleForm, scheduleSpec, scheduleWords } from './argus-schedule-spec'

const now = Math.floor(Date.now() / 1000)

const branch = (over: object = {}) => ({
  branch: 'main', default: true, sha: '0123456789abcdef', message: 'Decode frames faster', committed_at: now - 7200, indexed_at: now - 600,
  last_run_at: now - 600, stale: false, timed_out: false, symbols_failed: false, error: null, files: 12, symbols: 340, ...over,
})

const repo = (id: number, path: string, over: Partial<RepoRow> = {}): RepoRow => ({
  gitlab_id: id, repo: path, name: path.split('/').at(-1)!, group: path.split('/').slice(0, -1).join('/'), default_branch: 'main', included: true, branches: [],
  seen_at: now, listed: true, languages: [], language: 'C', schedule: '', schedule_words: 'With each scheduled pass', schedule_kind: 'pass',
  next_run_at: null, last_run_at: now - 600, state: 'indexed', problem: null, progress: null, indexed: [branch()], ...over,
})

const repos = [
  repo(1, 'team-a/eal-core'),
  repo(2, 'team-a/etl-decoder', { language: 'Python', state: 'failed', problem: 'git fetch failed: 403', indexed: [branch({ error: 'git fetch failed: 403' })] }),
  repo(3, 'team-b/tools/shim', { language: null, state: 'never', indexed: [], last_run_at: null }),
  repo(4, 'team-b/legacy', { included: false, listed: false, state: 'off', indexed: [] }),
]

const view = (rows: RepoRow[] = repos, over: object = {}) => ({
  json: {
    new_repos: 'include', global_branches: [], listed_at: now, running: false, pending: [],
    schedule: { default: 'pass', words: 'With each scheduled pass', time_zone: 'UTC', zone_problem: null, pass_interval: 0, next_pass_at: null },
    repos: rows, ...over,
  },
})

function routes(extra: Record<string, Handler> = {}): Record<string, Handler> {
  return {
    'GET /api/admin/argus/status': () => ({
      json: { job: { state: 'idle', branches: [], started: null, finished: null, returncode: null, tail: [], trigger: null }, index: { repos: 4, stale: 0, errored: 1, files: 10, symbols: 100 }, interval: 0, webhook: false, pending: [] },
    }),
    'GET /api/admin/argus/schedule': () => ({ json: { schedule: '*/15 * * * *', timeZone: 'UTC', problem: null, nextRuns: [new Date(Date.now() + 600_000).toISOString()] } }),
    'GET /api/admin/argus/webhook': () => ({ json: { enabled: false, fromEnv: false, url: '', header: 'X-Gitlab-Token', deliveries: [] } }),
    'GET /api/admin/argus/repos': () => view(),
    ...extra,
  }
}

const card = () => screen.findByRole('region', { name: 'Repositories' })
const rowOf = (c: HTMLElement, path: string) => within(c).getAllByRole('row').find((r) => r.textContent?.includes(path.split('/').at(-1)!) && r.textContent.includes(path.split('/').slice(0, -1).join('/')))!

describe('admin indexing: repositories', () => {
  it('are found by name, path or group and narrowed by state, group, language and whether they are indexed', async () => {
    fakeApi(admin, routes())
    renderApp('/admin/indexing')
    const c = await card()
    await within(c).findByText('eal-core')
    expect(within(c).getByText('git fetch failed: 403', { selector: 'span.line-clamp-2' })).toBeInTheDocument()
    expect(within(c).getByText('Not in GitLab', { selector: 'span' })).toBeInTheDocument()

    await userEvent.type(within(c).getByRole('searchbox'), 'team-b')
    expect(within(c).queryByText('eal-core')).not.toBeInTheDocument()
    expect(within(c).getByText('shim')).toBeInTheDocument()
    expect(within(c).getByText('legacy')).toBeInTheDocument()
    await userEvent.clear(within(c).getByRole('searchbox'))

    // By state, with a count on each.
    await userEvent.click(within(c).getByRole('button', { name: 'Failed 1' }))
    expect(within(c).getByText('etl-decoder')).toBeInTheDocument()
    expect(within(c).queryByText('eal-core')).not.toBeInTheDocument()
    await userEvent.click(within(c).getByRole('button', { name: 'Failed 1' }))

    // By group (its subgroups too), by language, by whether it is indexed.
    await userEvent.click(within(c).getByRole('combobox', { name: 'Group' }))
    await userEvent.click(await screen.findByRole('option', { name: 'team-b' }))
    expect(within(c).getByText('shim')).toBeInTheDocument()
    expect(within(c).queryByText('etl-decoder')).not.toBeInTheDocument()
    await userEvent.click(within(c).getByRole('combobox', { name: 'Indexed or not' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Left out' }))
    expect(within(c).getByText('legacy')).toBeInTheDocument()
    expect(within(c).queryByText('shim')).not.toBeInTheDocument()
    await userEvent.click(within(c).getByRole('combobox', { name: 'Language' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Python' }))
    expect(within(c).getByText('No repositories match these filters.')).toBeInTheDocument()
    await userEvent.click(within(c).getByRole('button', { name: 'Clear the filters' }))
    expect(within(c).getByText('eal-core')).toBeInTheDocument()

    // Sorted by state: what needs a look first.
    await userEvent.click(within(c).getByRole('button', { name: /^Status, sort/ }))
    const names = within(c).getAllByRole('row').slice(1).map((r) => r.querySelector('td:nth-child(3) .font-medium')?.textContent)
    expect(names).toEqual(['etl-decoder', 'shim', 'eal-core', 'legacy'])
  })

  it('shows where a run is with each repository, live', async () => {
    let rows = [
      repo(1, 'team-a/eal-core', { state: 'indexing', progress: { state: 'files', branch: 'main', done: 30, total: 120, started: now } }),
      repo(2, 'team-a/etl-decoder', { state: 'queued', progress: { state: 'queued' } }),
      repo(3, 'team-b/shim', { state: 'failed', progress: { state: 'failed', outcome: 'failed', message: 'Could not fetch it from GitLab: 403.' } }),
    ]
    fakeApi(admin, routes({ 'GET /api/admin/argus/repos': () => view(rows, { running: true }) }))
    renderApp('/admin/indexing')
    const c = await card()
    expect(await within(c).findByText('Reading files on main: 30 of 120')).toBeInTheDocument()
    expect(within(c).getByRole('progressbar', { name: 'team-a/eal-core: how far' })).toHaveAttribute('value', '25')
    expect(within(rowOf(c, 'team-a/etl-decoder')).getByText('Queued')).toBeInTheDocument()
    expect(within(c).getByText('Could not fetch it from GitLab: 403.')).toBeInTheDocument()
    rows = [repo(1, 'team-a/eal-core', { state: 'indexing', progress: { state: 'embedding', done: 64, total: 200 } }), ...rows.slice(1)]
    expect(await within(c).findByText('Embedding for meaning search: 64 of 200', {}, { timeout: 5000 })).toBeInTheDocument()
  })

  it('follows a run from the moment the page sees it, wherever it was started', async () => {
    let running = false
    const status = () => ({
      json: {
        job: { state: running ? 'running' : 'idle', branches: [], started: running ? now : null, finished: null, returncode: null, tail: [], trigger: running ? 'manual' : null },
        index: { repos: 1, stale: 0, errored: 0, files: 10, symbols: 100 }, interval: 0, webhook: false, pending: [],
      },
    })
    const rows = () => (running ? [repo(1, 'team-a/eal-core', { state: 'queued', progress: { state: 'queued' } })] : [repo(1, 'team-a/eal-core')])
    fakeApi(admin, routes({
      'GET /api/admin/argus/status': status,
      'GET /api/admin/argus/repos': () => view(rows(), { running }),
      'POST /api/admin/argus/index': () => {
        running = true
        return { json: { status: 'started' } }
      },
    }))
    renderApp('/admin/indexing')
    const c = await card()
    await within(c).findByText('eal-core')
    expect(within(rowOf(c, 'team-a/eal-core')).getByText('Indexed')).toBeInTheDocument()
    // Index now: the repositories show the run at once, not at their next minute's look.
    await userEvent.click(screen.getByRole('button', { name: 'Index now' }))
    await waitFor(() => expect(within(rowOf(c, 'team-a/eal-core')).getByText('Queued')).toBeInTheDocument(), { timeout: 2000 })
  })

  it('looks every few seconds while the page says a run goes, though its own last look did not see it', async () => {
    let seen = 0
    const running = {
      json: {
        job: { state: 'running', branches: [], started: now, finished: null, returncode: null, tail: [], trigger: 'schedule', progress: null },
        index: { repos: 1, stale: 0, errored: 0, files: 10, symbols: 100 }, interval: 0, webhook: false, pending: [],
      },
    }
    fakeApi(admin, routes({
      'GET /api/admin/argus/status': () => running,
      // Its first look came just before the scheduled pass took it.
      'GET /api/admin/argus/repos': () =>
        seen++ === 0
          ? view([repo(1, 'team-a/eal-core')])
          : view([repo(1, 'team-a/eal-core', { state: 'indexing', progress: { state: 'files', branch: 'main', done: 3, total: 12, started: now } })], { running: true }),
    }))
    renderApp('/admin/indexing')
    const c = await card()
    expect(await within(c).findByText('Reading files on main: 3 of 12', {}, { timeout: 5000 })).toBeInTheDocument()
  })

  it('changes many at once after asking, then says what came of each', async () => {
    const calls = fakeApi(admin, routes({
      'POST /api/admin/argus/repos/batch': (body) => {
        const b = body as { ids: number[]; action: string }
        return {
          json: {
            action: b.action,
            results: b.ids.map((id) => ({ gitlab_id: id, repo: repos.find((r) => r.gitlab_id === id)?.repo, ok: id !== 4, message: id === 4 ? 'Not indexed: choose it for the index first.' : 'Updating now.' })),
          },
        }
      },
    }))
    renderApp('/admin/indexing')
    const c = await card()
    await within(c).findByText('eal-core')
    await userEvent.click(within(rowOf(c, 'team-a/eal-core')).getByRole('checkbox', { name: 'Select row' }))
    const bulk = within(c).getByRole('region', { name: 'Bulk actions' })
    // Every one the filters and search show, not just this page's.
    await userEvent.click(within(bulk).getByRole('button', { name: 'Select all 4' }))
    expect(within(bulk).getByText('4 selected')).toBeInTheDocument()
    await userEvent.click(within(bulk).getByRole('button', { name: /Update now/ }))
    const ask = await screen.findByRole('alertdialog')
    expect(ask).toHaveTextContent('Update 4 repositories now?')
    expect(ask).toHaveTextContent('team-a/eal-core, team-a/etl-decoder, team-b/tools/shim, team-b/legacy')
    await userEvent.click(within(ask).getByRole('button', { name: 'Update now' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/admin/argus/repos/batch')?.body).toEqual({ ids: [1, 2, 3, 4], action: 'reindex' }))
    const done = await screen.findByRole('dialog', { name: 'Updated now' })
    expect(done).toHaveTextContent('Done for 3 of 4 repositories; 1 could not be changed, and each says why.')
    const outcome = within(done).getByRole('list', { name: 'Outcome for each repository' })
    expect(within(outcome).getAllByRole('listitem')[0]).toHaveTextContent('team-b/legacyNot indexed: choose it for the index first.')
    await userEvent.click(within(done).getByRole('button', { name: 'Done' }))

    // A schedule for several, in words.
    await userEvent.click(within(c).getByRole('button', { name: /Schedule…/ }))
    const dialog = await screen.findByRole('dialog', { name: 'Schedule of 4 repositories' })
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'Reindex' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Every day' }))
    await userEvent.clear(within(dialog).getByLabelText('At'))
    await userEvent.type(within(dialog).getByLabelText('At'), '03:30')
    expect(within(dialog).getByText('Every day at 03:30.')).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Apply to 4 repositories' }))
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/admin/argus/repos/batch').at(-1)?.body).toEqual({ ids: [1, 2, 3, 4], action: 'schedule', schedule: 'daily:03:30' }))
    expect(await screen.findByRole('dialog', { name: 'Schedule changed' })).toBeInTheDocument()
  })

  it('shows no next run for a repository GitLab no longer lists, as it cannot be fetched', async () => {
    fakeApi(admin, routes({ 'GET /api/admin/argus/repos': () => view([repo(1, 'team-a/eal-core'), repo(5, 'team-c/moved', { listed: false })]) }))
    renderApp('/admin/indexing')
    const c = await card()
    await within(c).findByText('moved')
    expect(within(rowOf(c, 'team-a/eal-core')).getByText(/next pass in 10 minutes/)).toBeInTheDocument()
    expect(within(rowOf(c, 'team-c/moved')).queryByText(/next pass/)).not.toBeInTheDocument()
  })

  it("sets the schedule for all, and one repository's own, and reads its log", async () => {
    const calls = fakeApi(admin, routes({
      'PUT /api/admin/argus/repos/settings': () => ({ json: { status: 'saved' } }),
      'PATCH /api/admin/argus/repos/1': () => ({ json: { status: 'saved', repo: 'team-a/eal-core' } }),
      'GET /api/admin/argus/repos/1/log': () => ({
        json: {
          repo: 'team-a/eal-core', progress: null,
          lines: [
            { run: (now - 3600) * 1000, at: now - 3600, level: 'info', text: 'Run started by its schedule (every 6 hours).' },
            { run: (now - 3600) * 1000, at: now - 3598, level: 'warning', text: 'main: 2 files could not be read or stored; each is tried again on the next runs (up to 3 times).' },
            { run: now * 1000, at: now, level: 'error', text: 'Could not fetch it from GitLab: 403. Check that Argus\'s GitLab account can read this repository.' },
          ],
        },
      }),
    }))
    renderApp('/admin/indexing')
    const c = await card()
    await within(c).findByText('eal-core')
    expect(within(rowOf(c, 'team-a/eal-core')).getByText(/next pass in 10 minutes/)).toBeInTheDocument()

    await userEvent.click(within(c).getByRole('button', { name: 'Change' }))
    const all = await screen.findByRole('dialog', { name: 'Schedule for all' })
    await userEvent.click(within(all).getByRole('combobox', { name: 'Reindex' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Every few hours' }))
    await userEvent.clear(within(all).getByLabelText('Every (hours)'))
    await userEvent.type(within(all).getByLabelText('Every (hours)'), '6')
    await userEvent.click(within(all).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ schedule: 'hours:6', timeZone: 'UTC' }))

    await userEvent.click(within(rowOf(c, 'team-a/eal-core')).getByRole('button', { name: 'More for team-a/eal-core' }))
    await userEvent.click(await screen.findByRole('menuitem', { name: /Schedule…/ }))
    const own = await screen.findByRole('dialog', { name: 'Schedule of team-a/eal-core' })
    await userEvent.click(within(own).getByRole('combobox', { name: 'Reindex' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Off: only pushes and when asked' }))
    await userEvent.click(within(own).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PATCH')?.body).toEqual({ schedule: 'off' }))

    await userEvent.click(within(rowOf(c, 'team-a/eal-core')).getByRole('button', { name: 'Log of team-a/eal-core' }))
    const log = await screen.findByRole('dialog', { name: 'Log of team-a/eal-core' })
    const runs = await within(log).findAllByRole('region')
    // Newest run first; each line a sentence, warnings and errors marked.
    expect(runs[0]).toHaveTextContent('Could not fetch it from GitLab: 403.')
    expect(within(runs[0]!).getByLabelText('Error')).toBeInTheDocument()
    expect(runs[1]).toHaveTextContent('Run started by its schedule (every 6 hours).')
    expect(within(runs[1]!).getByLabelText('Warning')).toBeInTheDocument()
  })
})

describe('repository schedules', () => {
  it('read and write the form Argus stores, and say themselves', () => {
    expect(scheduleSpec(scheduleForm('weekly:7:23:05'))).toBe('weekly:7:23:05')
    expect(scheduleSpec(scheduleForm('daily:2:30'))).toBe('daily:02:30')
    expect(scheduleSpec(scheduleForm(''))).toBe('')
    expect(scheduleWords('weekly:1:03:00')).toBe('Mondays at 03:00')
    expect(scheduleWords('hours:1')).toBe('Every hour')
    expect(scheduleWords('', 'Every 6 hours')).toBe('Same as all (every 6 hours)')
  })
})
