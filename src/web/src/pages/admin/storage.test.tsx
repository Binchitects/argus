import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, member, renderApp } from '@/test/utils'
import { fileSearch, growth, noFilter, parseRoom, roomOf, type CleanupPlan, type FileRow, type PersonFiles, type StorageReport } from './storage-api'

const GB = 1024 ** 3
const MB = 1024 ** 2

const report: StorageReport = {
  at: '2026-10-09T10:00:00Z',
  settings: { alertPercent: 80, personMegabytes: 500, mediaDays: 90, backupsKept: 14 },
  disks: {
    alertPercent: 80,
    problem: null,
    list: [
      {
        id: '/dev/nvme0n1p2', name: '/', device: '/dev/nvme0n1p2', fsType: 'ext4', size: 1000 * GB, free: 100 * GB, used: 900 * GB, percent: 90, source: 'host',
        holds: ['The model library', 'Backups'], above: true, trend: [[1, 800 * GB], [2, 900 * GB]], perDay: 2 * GB, fullInDays: 50,
      },
      { id: '/dev/sdb1', name: '/data', device: '/dev/sdb1', fsType: 'xfs', size: 2000 * GB, free: 1800 * GB, used: 200 * GB, percent: 10, source: 'host', holds: [], above: false, trend: [], perDay: null, fullInDays: null },
    ],
  },
  databases: {
    list: [
      { name: 'llmapp', bytes: 3 * GB, what: 'The app: chats and their files' },
      { name: 'litellm', bytes: 1 * GB, what: 'The gateway' },
    ],
    tables: [{ database: 'llmapp', name: 'chat_attachments', bytes: 2 * GB, rows: 1200, what: "the chat's files" }],
    problem: null,
  },
  files: {
    total: { count: 1200, bytes: 2 * GB },
    groups: [
      { origin: 'upload', kind: 'file', state: 'chat', count: 1000, bytes: GB },
      { origin: 'picture', kind: 'image', state: 'none', count: 200, bytes: GB },
    ],
    people: [{ id: 'p1', userName: 'ann', displayName: 'Ann', email: 'ann@example.test', count: 1200, bytes: 2 * GB, ownMegabytes: null, held: false }],
    problem: null,
  },
  library: {
    report: {
      dir: '/library', exists: true, bytes: 40 * GB,
      files: [
        { path: 'qwen/qwen3-8b.gguf', bytes: 8 * GB, parts: 1, kind: 'chat', use: 'engine', models: ['Qwen (loaded)'], note: null },
        { path: 'old/llama-70b.gguf', bytes: 32 * GB, parts: 1, kind: 'chat', use: 'unused', models: [], note: null },
      ],
      folders: [], partials: [],
    },
    problem: null,
  },
  argus: { configured: true, report: { data_dir: '/var/lib/argus', index_bytes: 5 * MB, mirrors_bytes: 20 * MB, trees_bytes: 3 * MB, packs_bytes: MB, other_bytes: 0, library_dir: null, library_bytes: null, disk: null } },
  backups: { dir: '/backups', state: 'missing', bytes: 0, otherBytes: 0, backups: [] },
  logs: { keeps: '14 days', week: [{ name: 'app', bytes: 50 * MB }], weekStored: 5 * MB, problem: null },
  metrics: { bytes: 2 * GB, keeps: '30 days', maxSize: null, oldest: null, problem: null },
  unseen: ["Docker's images and its build cache: `docker system df -v` on the host lists them."],
  rules: [{ what: 'Legal hold', rule: 'Nobody is on legal hold.' }],
  trends: { 'db:llmapp': [[1, 2 * GB], [2, 3 * GB]] },
}

const file = (over: Partial<FileRow>): FileRow => ({
  id: 'f1', userId: 'p1', person: 'Ann', email: 'ann@example.test', name: 'report.pdf', kind: 'file', contentType: 'application/pdf', origin: 'upload', state: 'chat',
  bytes: 10 * MB, createdAt: '2026-10-01T00:00:00Z', chatId: 'c1', chatTitle: 'Quarterly numbers', held: false, ...over,
})

const files: FileRow[] = [
  file({}),
  file({ id: 'f2', name: 'cat.png', kind: 'image', origin: 'picture', state: 'none', chatId: null, chatTitle: null, bytes: 2 * MB }),
  file({ id: 'f3', userId: 'p2', person: 'Hal', name: 'evidence.docx', bytes: MB, held: true }),
]

const people: PersonFiles[] = [
  { id: 'p1', userName: 'ann', displayName: 'Ann', email: 'ann@example.test', count: 2, bytes: 12 * MB, ownMegabytes: null, held: false },
  { id: 'p2', userName: 'hal', displayName: 'Hal', email: 'hal@example.test', count: 1, bytes: MB, ownMegabytes: 0, held: true },
]

const plan = (over: Partial<CleanupPlan>): CleanupPlan => ({ kind: 'unused-files', count: 0, bytes: 0, held: { count: 0, bytes: 0 }, items: [], problem: null, warning: null, days: 7, keep: null, ...over })

const routes = {
  'GET /api/admin/storage': () => ({ json: report }),
  'GET /api/admin/storage/people': () => ({ json: { people, personMegabytes: 500 } }),
  'GET /api/admin/storage/files': () => ({ json: { rows: files, total: { count: 3, bytes: 13 * MB }, capped: false } }),
}

describe('Admin → Storage', () => {
  it('shows the disks, the one past the alert, what takes room, and what the app cannot see', async () => {
    fakeApi(admin, routes)
    renderApp('/admin/storage')
    expect(await screen.findByRole('heading', { name: 'Storage', level: 1 })).toBeInTheDocument()
    // The fullest disk is past the share: a warning that leads to the clean-ups.
    expect(await screen.findByText('/ is 90% full')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Open the clean-ups' })).toHaveAttribute('href', '/admin/storage?tab=cleanups')
    expect(screen.getAllByText(/On it: The model library; Backups/).length).toBeGreaterThan(0)
    expect(screen.getByText('full in about 50 days')).toBeInTheDocument()
    // Each kind of thing, and how it grew.
    const room = screen.getByRole('table', { name: 'What takes room' })
    expect(within(room).getByText('Database llmapp')).toBeInTheDocument()
    expect(within(room).getByText('+1.0 GiB')).toBeInTheDocument()
    expect(within(room).getByText(/Not mounted in the app/)).toBeInTheDocument()
    expect(within(room).getByText(/Kept 14 days/)).toBeInTheDocument()
    // A model nothing uses, and the rules in force.
    expect(within(screen.getByRole('table', { name: 'Model files' })).getByText('Nothing uses it')).toBeInTheDocument()
    expect(screen.getByText('Nobody is on legal hold.')).toBeInTheDocument()
    expect(screen.getByText('docker system df -v')).toBeInTheDocument()
  })

  it('Measure again measures the folders now', async () => {
    const calls = fakeApi(admin, routes)
    renderApp('/admin/storage')
    await screen.findByText('/ is 90% full')
    await userEvent.click(screen.getByRole('button', { name: 'Measure again' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/admin/storage?fresh=true')).toBe(true))
  })

  it('a part that cannot be measured says why, and the rest still shows', async () => {
    fakeApi(admin, {
      ...routes,
      'GET /api/admin/storage': () => ({
        json: { ...report, disks: { ...report.disks, problem: 'Prometheus did not answer, so the host\'s disks are missing.', list: [] }, databases: { list: [], tables: [], problem: 'The databases\' sizes could not be read: timeout' } },
      }),
    })
    renderApp('/admin/storage')
    expect(await screen.findByText(/Prometheus did not answer/)).toBeInTheDocument()
    expect(screen.getByText(/could not be read: timeout/)).toBeInTheDocument()
    expect(screen.getByText('No disk seen.')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Model library' })).toBeInTheDocument()
  })

  it('files are filtered on the server by person, where from, size, age and order', async () => {
    const calls = fakeApi(admin, routes)
    renderApp('/admin/storage?tab=files')
    expect(await screen.findByText('report.pdf')).toBeInTheDocument()
    expect(screen.getAllByText('in “Quarterly numbers”')).toHaveLength(2)
    expect(screen.getByText('legal hold')).toBeInTheDocument()
    const last = () => calls.filter((c) => c.path.startsWith('/api/admin/storage/files')).at(-1)!.path

    await userEvent.click(screen.getByRole('combobox', { name: 'Person' }))
    await userEvent.click(await screen.findByRole('option', { name: /^Ann/ }))
    await waitFor(() => expect(last()).toContain('person=p1'))
    await userEvent.click(screen.getByRole('combobox', { name: 'From' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Picture made' }))
    await waitFor(() => expect(last()).toContain('origin=picture'))
    await userEvent.click(screen.getByRole('combobox', { name: 'Size' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Over 10 MB' }))
    await waitFor(() => expect(last()).toContain(`min=${10 * MB}`))
    await userEvent.click(screen.getByRole('combobox', { name: 'Age' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Older than 30 days' }))
    await waitFor(() => expect(last()).toMatch(/before=\d{4}-\d\d-\d\dT/))
    await userEvent.click(screen.getByRole('combobox', { name: 'Order' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Newest first' }))
    await waitFor(() => expect(last()).toContain('sort=new'))
    await userEvent.type(screen.getByRole('searchbox', { name: 'Find files by name, person or chat' }), 'cat')
    await waitFor(() => expect(last()).toContain('q=cat'))

    await userEvent.click(screen.getByRole('button', { name: 'Clear filters' }))
    await waitFor(() => expect(last()).toBe('/api/admin/storage/files'))
  })

  it('deleting files asks first, says what a hold keeps, and cancelling sends nothing', async () => {
    const calls = fakeApi(admin, {
      ...routes,
      'POST /api/admin/storage/files/delete': () => ({ json: { deleted: { count: 2, bytes: 12 * MB }, held: { count: 1, bytes: MB } } }),
    })
    renderApp('/admin/storage?tab=files')
    expect(await screen.findByText('report.pdf')).toBeInTheDocument()
    for (const box of screen.getAllByRole('checkbox', { name: 'Select row' })) await userEvent.click(box)
    await userEvent.click(screen.getByRole('button', { name: /Delete 3 files/ }))
    let ask = await screen.findByRole('alertdialog', { name: /Delete 3 files/ })
    expect(ask).toHaveTextContent('cannot be undone')
    expect(ask).toHaveTextContent('2 files are in chats or assistants')
    expect(ask).toHaveTextContent('1 file of people on legal hold stays')
    await userEvent.click(within(ask).getByRole('button', { name: 'Cancel' }))
    expect(calls.some((c) => c.method === 'POST')).toBe(false)

    await userEvent.click(screen.getByRole('button', { name: /Delete 3 files/ }))
    ask = await screen.findByRole('alertdialog', { name: /Delete 3 files/ })
    await userEvent.click(within(ask).getByRole('button', { name: 'Delete' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ ids: ['f1', 'f2', 'f3'] }))
    expect(await screen.findByText('Deleted 2 files, 12.0 MiB freed')).toBeInTheDocument()
    expect(screen.getByText('1 file kept: legal hold.')).toBeInTheDocument()
  })

  it('a clean-up previews what would go and the room it frees, then runs with the age chosen after a confirmation', async () => {
    const calls = fakeApi(admin, {
      ...routes,
      'GET /api/admin/storage/cleanups/unused-files': (_b, _i, url) => {
        const days = Number(url.searchParams.get('days'))
        return {
          json: plan({
            days, count: days >= 30 ? 1 : 2, bytes: days >= 30 ? 2 * MB : 5 * MB, held: { count: 1, bytes: MB },
            items: [{ id: 'f2', name: 'cat.png', person: 'Ann', bytes: 2 * MB, at: '2026-08-01T00:00:00Z', note: null }],
          }),
        }
      },
      'POST /api/admin/storage/cleanups/unused-files': () => ({ json: { kind: 'unused-files', count: 1, bytes: 2 * MB, held: { count: 1, bytes: MB }, failed: [] } }),
    })
    renderApp('/admin/storage?tab=cleanups')
    const card = (await screen.findByText('Files in no chat')).closest('section') as HTMLElement
    const days = within(card).getByRole('textbox', { name: /older than, in days/ })
    expect(days).toHaveValue('7')
    await userEvent.click(within(card).getByRole('button', { name: 'Preview' }))
    expect(await within(card).findByText('5.0 MiB')).toBeInTheDocument()
    expect(within(card).getByText(/Legal hold keeps 1 file/)).toBeInTheDocument()

    await userEvent.clear(days)
    await userEvent.type(days, '30')
    await waitFor(() => expect(calls.some((c) => c.path === '/api/admin/storage/cleanups/unused-files?days=30')).toBe(true))
    expect(await within(card).findByText('2.0 MiB', { selector: 'span.font-medium' })).toBeInTheDocument()

    await userEvent.click(within(card).getByRole('button', { name: 'Clean up' }))
    const ask = await screen.findByRole('alertdialog', { name: /Files in no chat: remove 1 file/ })
    expect(ask).toHaveTextContent('1 file of people on legal hold stay')
    await userEvent.click(within(ask).getByRole('button', { name: 'Clean up' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ days: 30 }))
    expect(await screen.findByText('1 file removed, 2.0 MiB freed')).toBeInTheDocument()
  })

  it('a clean-up that cannot run says why, a wrong number is caught, and models are deleted only as chosen', async () => {
    const calls = fakeApi(admin, {
      ...routes,
      'GET /api/admin/storage/cleanups/old-backups': () => ({ json: plan({ kind: 'old-backups', days: null, keep: 14, problem: 'The app does not see the backups: /backups is not mounted.' }) }),
      'GET /api/admin/storage/cleanups/unused-models': () => ({
        json: plan({
          kind: 'unused-models', days: null, count: 2, bytes: 40 * GB, warning: 'Deleted, a model is gone from the library.',
          items: [
            { id: 'old/llama-70b.gguf', name: 'old/llama-70b.gguf', person: null, bytes: 32 * GB, at: null, note: 'chat' },
            { id: 'old/tiny.gguf', name: 'old/tiny.gguf', person: null, bytes: 8 * GB, at: null, note: 'chat' },
          ],
        }),
      }),
      'POST /api/admin/storage/cleanups/unused-models': () => ({ json: { kind: 'unused-models', count: 1, bytes: 32 * GB, held: { count: 0, bytes: 0 }, failed: [] } }),
    })
    renderApp('/admin/storage?tab=cleanups')
    const backups = (await screen.findByText('Old backups')).closest('section') as HTMLElement
    const keep = within(backups).getByRole('textbox', { name: /backups to keep/ })
    await waitFor(() => expect(keep).toHaveValue('14'))
    await userEvent.clear(keep)
    await userEvent.type(keep, 'x')
    expect(within(backups).getByText('A whole number, 1 or more.')).toBeInTheDocument()
    expect(within(backups).getByRole('button', { name: 'Preview' })).toBeDisabled()
    await userEvent.clear(keep)
    await userEvent.type(keep, '14')
    await userEvent.click(within(backups).getByRole('button', { name: 'Preview' }))
    expect(await within(backups).findByText(/is not mounted/)).toBeInTheDocument()
    expect(within(backups).queryByRole('button', { name: 'Clean up' })).not.toBeInTheDocument()

    const models = screen.getByText('Models nothing uses').closest('section') as HTMLElement
    await userEvent.click(within(models).getByRole('button', { name: 'Preview' }))
    expect(await within(models).findByText('Choose the models to delete.')).toBeInTheDocument()
    expect(within(models).getByRole('button', { name: 'Clean up' })).toBeDisabled()
    await userEvent.click(within(models).getByRole('checkbox', { name: 'Delete old/llama-70b.gguf' }))
    await userEvent.click(within(models).getByRole('button', { name: 'Clean up' }))
    const ask = await screen.findByRole('alertdialog', { name: /remove 1 model \(32.0 GiB\)/ })
    expect(ask).toHaveTextContent('gone from the library')
    await userEvent.click(within(ask).getByRole('button', { name: 'Clean up' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ only: ['old/llama-70b.gguf'] }))
  })

  it("each person's room: their own, no limit, or the company's again", async () => {
    const calls = fakeApi(admin, { ...routes, 'PUT /api/admin/storage/people/p1/quota': () => ({ status: 204 }) })
    renderApp('/admin/storage?tab=people')
    expect(await screen.findByText(/Each person's files may take 500.0 MiB/)).toBeInTheDocument()
    expect(screen.getByText('No limit')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Set the room of Ann' }))
    const dialog = await screen.findByRole('dialog', { name: "Room for Ann's files" })
    const input = within(dialog).getByRole('textbox', { name: 'Room (MB)' })
    await userEvent.type(input, 'lots')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    expect(await within(dialog).findByText(/A whole number of megabytes/)).toBeInTheDocument()
    expect(calls.some((c) => c.method === 'PUT')).toBe(false)
    await userEvent.clear(input)
    await userEvent.type(input, '2000')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ megabytes: 2000 }))
  })

  it('is for admins only', async () => {
    fakeApi(member, routes)
    renderApp('/admin/storage')
    expect(await screen.findByRole('heading', { name: 'Admins only' })).toBeInTheDocument()
  })
})

describe('storage elsewhere', () => {
  it('the overview shows the fullest disk and warns when it is past the share', async () => {
    fakeApi(admin, {
      'GET /api/admin/overview': () => ({
        json: {
          people: 3, admins: 1, spend: 4.2, overCredit: [], services: [], model: 'M', index: { configured: false, summary: null, error: null },
          storage: { alertPercent: 80, problem: null, disks: [{ id: 'd', name: '/', size: 100 * GB, free: 5 * GB, percent: 95, above: true, holds: ['Backups'] }] },
        },
      }),
    })
    renderApp('/admin')
    expect(await screen.findByText('Fullest disk')).toBeInTheDocument()
    expect(screen.getByText('/ is 95% full')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Open Storage' })).toHaveAttribute('href', '/admin/storage')
  })

  it("a person sees what their files take of their room on their account", async () => {
    fakeApi(member, { 'GET /api/account/data': () => ({ json: { retentionDays: null, files: { bytes: 600 * MB, limitBytes: 500 * MB } } }) })
    renderApp('/account')
    expect(await screen.findByText(/It is full: new files and pictures are refused/)).toBeInTheDocument()
    expect(screen.getByText('600.0 MiB')).toBeInTheDocument()
  })
})

describe('storage helpers', () => {
  it('filters become the query the API takes', () => {
    expect(fileSearch(noFilter)).toBe('')
    const now = Date.parse('2026-10-09T00:00:00Z')
    expect(fileSearch({ ...noFilter, q: ' cat ', origin: 'video', days: '30', sort: 'old' }, now)).toBe('?q=cat&origin=video&before=2026-09-09T00%3A00%3A00.000Z&sort=old')
  })

  it('rooms and growth read as the page says them', () => {
    expect(parseRoom('')).toBeNull()
    expect(parseRoom('0')).toBe(0)
    expect(parseRoom('12')).toBe(12)
    expect(parseRoom('1.5')).toBeUndefined()
    expect(roomOf(people[0]!, 500)).toBe(500 * MB)
    expect(roomOf(people[1]!, 500)).toBeNull()
    expect(roomOf(people[0]!, null)).toBeNull()
    expect(growth([[0, 10], [86_400_000, 30]])).toBe(20)
    expect(growth([[0, 10]])).toBeNull()
  })
})
