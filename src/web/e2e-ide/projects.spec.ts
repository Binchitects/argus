import { AxeBuilder } from '@axe-core/playwright'
import { expect, test, type Page, type Route } from '@playwright/test'

const summary = (id: string, name: string, more: object = {}) => ({
  id, name, description: '', global: false, status: 'active', startsOn: null, dueOn: null, monthlyBudget: null, role: 'owner', owner: 'Ada Lovelace',
  mine: true, members: 2, chats: 1, goals: 2, goalsDone: 1, spentThisMonth: 2.5, updatedAt: '2026-11-01T10:00:00Z', ...more,
})

const project = {
  ...summary('8f1c2d3e-0000-4000-8000-000000000001', 'Payments', { monthlyBudget: 20, description: 'The new checkout.' }),
  alertPercent: 80, stopAtBudget: false, createdAt: '2026-10-01T10:00:00Z', ownerId: 'u1', isOwner: true,
  members: [{ id: 'x1', role: 'contributor', userId: 'u2', groupId: null, name: 'Ben Okafor', email: 'ben@example.test', addedAt: '2026-10-01T10:00:00Z' }],
  repositories: [{ path: 'payments/api', host: 'gitlab.corp', addedAt: '2026-10-01T10:00:00Z' }],
  items: [],
  chats: [{ id: 'c1', title: 'Refund flow', kind: 'session', place: '/home/ada/shop', updatedAt: '2026-11-01T10:00:00Z', mine: true }],
  otherChats: [],
  goals: [
    { id: 'g1', title: 'Card payments', description: null, status: 'doing', dueOn: '2026-11-30', doneAt: null, position: 0, humanHours: 8 },
    { id: 'g2', title: 'Launch', description: null, status: 'done', dueOn: null, doneAt: '2026-10-20T10:00:00Z', position: 1, humanHours: null },
  ],
  tasks: [{ id: 't1', title: 'Wire the gateway', goalId: 'g1', assigneeId: null, assignee: null, done: false, doneAt: null, createdAt: '2026-10-02T10:00:00Z', humanHours: null }],
}

const report = {
  from: '2026-10-08T00:00:00Z', to: '2026-11-07T00:00:00Z', intervalMs: 86_400_000,
  totals: { requests: 42, input: 30_000, cached: 10_000, output: 5_000, cost: 3.25, agentSeconds: 600, activeSeconds: 5_400, people: 2, messages: 17 },
  progress: { goals: 2, goalsDone: 1, tasks: 1, tasksDone: 0, tasksDoneInRange: 0, steps: 0, stepsDone: 0, chats: 0, rooms: 0, sessions: 1 },
  models: [{ name: 'model-a', requests: 42, tokens: 45_000, cost: 3.25 }],
  surfaces: [{ name: 'Code Arena', requests: 42, tokens: 45_000, cost: 3.25 }],
  series: [
    { at: '2026-10-30T00:00:00Z', input: 10_000, output: 2_000, cost: 1 },
    { at: '2026-11-01T00:00:00Z', input: 20_000, output: 3_000, cost: 2.25 },
  ],
  people: [{ id: 'u1', name: 'Ada Lovelace', email: 'ada@example.test', requests: 42, tokens: 45_000, cost: 3.25, agentSeconds: 600, activeSeconds: 5_400, messages: 17 }],
  peopleHidden: false,
  budget: { monthly: 20, spent: 3.25, percent: 16, alertPercent: 80, stopAtBudget: false },
  merged: [],
  mergedError: null,
  human: { estimated: 1, notEstimated: 1, byHandHours: 8, peopleHours: 1.5, modelHours: 0.17, hourlyRate: 50, byHandCost: 400, actualCost: 78.25, faster: 5, savedHours: 6.5, savedCost: 321.75 },
}

/** code-arena web's API, with Arena's projects as it relays them: the session in Payments. */
async function backend(page: Page) {
  const state = {
    name: 'Code Arena', version: '7.8.2', license: 'AGPL-3.0-only', source: 'https://github.com/Binchitects/argus', manual: 'https://llm.test/help/code-arena',
    folder: '/home/ada/shop', project: 'shop', branch: 'main', model: 'model-a', context: 262144, contextUsed: 4200, compactAt: 80, compactTarget: 25,
    servers: [], jobs: [], queued: [], thinking: null, mode: 'ask', modes: [{ name: 'ask', description: 'edits and commands ask first' }],
    session: 's1', busy: false, arenaTools: false, tools: { local: 10, servers: [] }, chat: 'c1',
    workProject: { id: project.id, name: 'Payments' },
  }
  const answers: Record<string, unknown> = {
    '/api/state': state,
    '/api/chat/config': {
      model: 'model-a',
      models: [{ name: 'model-a', context: 262144, maxOutput: 32768, vision: false, tools: true, thinking: true, loaded: true, prices: { input: null, cachedInput: null, output: null } }],
      auto: null, presets: [], defaultThinking: null, argus: false, tools: [], gitlabUrl: null, maxUploadBytes: 0, imageTypes: [],
    },
    '/api/session': { id: 's1', messages: [], diffs: {}, busy: false },
    '/api/sessions': [],
    '/api/preferences': { layout: { chatOpen: false } },
    '/api/history': [],
    '/api/files': { path: '.', entries: [{ name: 'README.md', path: 'README.md', kind: 'file', size: 7 }] },
    '/api/files/all': { files: ['README.md'], truncated: false },
    '/api/workspace/state': {},
    '/api/backups': { backups: [] },
    '/api/changes': [],
    // Arena's, through code-arena with the person's key.
    '/api/projects': [summary(project.id, 'Payments', { monthlyBudget: 20 }), summary('8f1c2d3e-0000-4000-8000-000000000002', 'Company wiki', { global: true, mine: false, role: 'contributor' })],
    '/api/projects/may': { create: true, global: false },
    [`/api/projects/${project.id}`]: project,
    [`/api/projects/${project.id}/report`]: report,
    '/api/project': { projects: [{ id: project.id, name: 'Payments' }], suggested: project.id, problem: null },
    '/api/sharing/groups': [],
  }
  await page.route('**/api/**', (route: Route) => {
    const url = new URL(route.request().url())
    if (route.request().method() === 'POST') return ['/api/workspace/state', '/api/preferences', '/api/backups'].includes(url.pathname) ? route.fulfill({ json: {} }) : route.fulfill({ status: 404, json: { error: 'not_found', message: 'Not here.' } })
    const known = answers[url.pathname]
    return known !== undefined ? route.fulfill({ json: known }) : route.fulfill({ status: 404, json: { error: 'not_found', message: 'Not here.' } })
  })
}

/** What axe finds serious, as the other IDE specs read it (a tab strip owns its close buttons, as VS Code's). */
async function serious(page: Page) {
  const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).disableRules(['aria-required-children']).analyze()
  return axe.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(' ')).slice(0, 3).join(' | ')})`)
}

for (const scheme of ['light', 'dark'] as const) {
  test(`the Projects view lists Arena's projects and one opens in a tab as Arena's page (${scheme})`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: scheme })
    await backend(page)
    await page.goto('/code-arena.html')

    // The activity bar's Projects: the session's marked, Mine first, then Global.
    await page.getByRole('navigation', { name: 'Activity bar' }).getByRole('button', { name: 'Projects' }).click()
    const list = page.getByRole('list', { name: 'Projects' })
    await expect(list.getByRole('button', { name: 'Payments (this session, this folder\'s repository)' })).toBeVisible()
    await expect(list.getByText('50% of its goals')).toBeVisible()
    await expect(list.getByText('Company wiki')).toHaveCount(0)
    await page.getByRole('radio', { name: 'Global' }).click()
    await expect(list.getByText('Company wiki')).toBeVisible()
    await page.getByRole('radio', { name: 'Mine' }).click()
    expect(await serious(page)).toEqual([])

    // A project in an editor tab: its numbers over the window, the time window's control and the export.
    await list.getByRole('button', { name: /^Payments/ }).click()
    const tab = page.getByRole('tablist', { name: 'Open files', exact: true }).getByRole('tab', { name: 'Payments' })
    await expect(tab).toBeVisible()
    const shown = page.getByTestId('project-tab')
    await expect(shown.getByRole('heading', { name: /Payments/ })).toBeVisible()
    await expect(shown.getByText('Budget this month: $3.25 of $20.00')).toBeVisible()
    await expect(shown.getByRole('button', { name: 'Export' })).toBeVisible()
    expect(await serious(page)).toEqual([])

    await shown.getByRole('tab', { name: 'Goals and tasks' }).click()
    await expect(shown.getByRole('checkbox', { name: 'Wire the gateway: done' })).toBeVisible()
    await shown.getByRole('tab', { name: 'In it' }).click()
    await expect(shown.getByRole('button', { name: 'Take this session out' })).toBeVisible()
    await expect(shown.getByText('open now')).toBeVisible()
    expect(await serious(page)).toEqual([])
    await shown.getByRole('tab', { name: 'Settings' }).click()
    await expect(shown.getByLabel('Dollars a month')).toHaveValue('20')
    expect(await serious(page)).toEqual([])

    // The status bar's project opens the same tab again, from anywhere.
    await tab.click({ button: 'middle' })
    await expect(tab).toHaveCount(0)
    await page.getByRole('contentinfo', { name: 'Status bar' }).getByRole('button', { name: 'Project Payments: open it' }).click()
    await expect(page.getByRole('tablist', { name: 'Open files', exact: true }).getByRole('tab', { name: 'Payments' })).toBeVisible()
  })
}
