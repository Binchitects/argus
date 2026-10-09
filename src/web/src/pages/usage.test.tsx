import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, member, renderApp } from '@/test/utils'
import type { PromptListData, PromptRow } from './usage-prompts'

const mine = {
  intervalMs: 3_600_000,
  totals: { requests: 3, inputMiss: 900, inputHit: 400, output: 120, cost: 0.6 },
  series: [],
  models: [],
}

const row = (over: Partial<PromptRow>): PromptRow => ({
  id: 'r', at: '2026-10-05T10:00:00Z', source: 'chat', kind: 'chat', model: 'Qwen3.8-Flash-Next', prompt: 1000, cached: 400, completion: 200, cost: 0.000288, unpriced: false, ...over,
})

const prompts: PromptListData = {
  rows: [
    row({ id: 'a1', at: '2026-10-05T12:00:00Z', chatId: 'c1', title: 'Trip to Rome' }),
    row({ id: 'r2', at: '2026-10-05T11:00:00Z', source: 'api', kind: 'picture', model: 'FLUX.2-klein-4B', key: 'laptop', prompt: 0, cached: 0, completion: 0, cost: 0.01 }),
    row({ id: 'a3', at: '2026-10-04T09:00:00Z', chatId: 'c2', title: 'Old question', cost: 0, unpriced: true }),
  ],
  totals: { prompts: 3, prompt: 2000, cached: 800, completion: 400, cost: 0.010288, unpriced: 1, blind: 0 },
  capped: false,
  models: ['FLUX.2-klein-4B', 'Qwen3.8-Flash-Next'],
  problem: null,
}

describe('usage: each prompt and what it cost', () => {
  it('a person sees their own prompts: answers by chat, API requests by key, with tokens, cost and totals', async () => {
    const calls = fakeApi(member, {
      'GET /api/usage/me': () => ({ json: mine }),
      'GET /api/usage/prompts': () => ({ json: prompts }),
    })
    renderApp('/usage')
    const table = await screen.findByRole('table', { name: 'prompts' })
    // The answer links to its chat; the API request names its key and what it made.
    expect(await within(table).findByRole('link', { name: 'Trip to Rome' })).toHaveAttribute('href', '/chat/c1')
    const api = within(table).getByText('laptop').closest('tr')!
    expect(api).toHaveTextContent('picture')
    expect(api).toHaveTextContent('$0.01')
    expect(within(table).getByText('Trip to Rome').closest('tr')).toHaveTextContent(/1 K.*400.*200.*\$0\.000288/)
    // One from before costs were kept is marked, and the note says why.
    expect(within(table).getByText('Old question').closest('tr')).toHaveTextContent('$0.00 *')
    expect(screen.getByText(/Parts of it ran before costs were kept/)).toBeInTheDocument()
    // Totals count every prompt the filters match.
    expect(screen.getByLabelText('Prompts')).toHaveTextContent('3')
    expect(screen.getByLabelText('Cached')).toHaveTextContent('800')
    // No person column, no admin's filters.
    expect(within(table).queryByRole('columnheader', { name: /Person/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('combobox', { name: 'Person' })).not.toBeInTheDocument()

    // Sorted by cost, the dearest comes first.
    await userEvent.click(within(table).getByRole('button', { name: /^Cost, sort/ }))
    await userEvent.click(within(table).getByRole('button', { name: /^Cost, sort/ }))
    expect(within(table).getAllByRole('row')[1]).toHaveTextContent('laptop')

    // Only the API keys' requests: asked of the server.
    await userEvent.click(screen.getByRole('radio', { name: 'API keys' }))
    await waitFor(() => expect(calls.some((c) => c.path.startsWith('/api/usage/prompts?') && new URLSearchParams(c.path.split('?')[1]).get('source') === 'api')).toBe(true))
    expect(calls.some((c) => c.path.startsWith('/api/admin/usage'))).toBe(false)
  })

  it('a comparison not voted on yet shows its side for its model, and its cost waits for the vote', async () => {
    fakeApi(member, {
      'GET /api/usage/me': () => ({ json: mine }),
      'GET /api/usage/prompts': () => ({
        json: {
          ...prompts,
          rows: [
            row({ id: 'b1', model: 'Model A', cost: null, chatId: 'c3', title: 'Which sort?' }),
            row({ id: 'b2', at: '2026-10-05T12:01:00Z', model: 'Model B', cost: null, chatId: 'c3', title: 'Which sort?' }),
          ],
          totals: { prompts: 2, prompt: 2000, cached: 800, completion: 400, cost: 0, unpriced: 0, blind: 2 },
          models: [],
        },
      }),
    })
    renderApp('/usage')
    const table = await screen.findByRole('table', { name: 'prompts' })
    await within(table).findAllByText('Which sort?')
    const rows = within(table).getAllByRole('row').slice(1)
    expect(rows.map((r) => r.textContent)).toEqual([expect.stringMatching(/Which sort\?.*Model B.*after the vote/), expect.stringMatching(/Which sort\?.*Model A.*after the vote/)])
    expect(screen.getByText('2 comparison answers counted after the vote')).toBeInTheDocument()
  })

  it("an admin sees everyone's prompts by person and group, with who asked and never a chat's title", async () => {
    const calls = fakeApi(admin, {
      'GET /api/dashboards/usage-by-user': () => ({ json: { uid: 'usage-by-user', title: 'Usage by person', time: { from: 'now-7d', to: 'now' }, panels: [], variables: [] } }),
      'GET /api/usage/me': () => ({ json: mine }),
      'GET /api/usage/prompts': () => ({ json: { ...prompts, rows: [] } }),
      'GET /api/admin/people': () => ({ json: { warning: null, people: [{ id: 'p1', userName: 'sam', displayName: 'Sam Smith', email: 'sam@example.test' }] } }),
      'GET /api/admin/groups': () => ({ json: [{ id: 'g1', name: 'Research', members: 1 }] }),
      'GET /api/admin/usage/prompts': () => ({
        json: { ...prompts, rows: [row({ id: 'a9', person: { id: 'p1', email: 'sam@example.test', name: 'Sam Smith' } })], totals: { ...prompts.totals, prompts: 1 } },
      }),
    })
    renderApp('/usage')
    await userEvent.click(await screen.findByRole('tab', { name: "Everyone's prompts" }))
    const table = await screen.findByRole('table', { name: 'prompts' })
    const sam = (await within(table).findByText('Sam Smith')).closest('tr')!
    expect(sam).toHaveTextContent('Chat')
    expect(within(table).queryByRole('link')).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('combobox', { name: 'Person' }))
    await userEvent.click(await screen.findByRole('option', { name: /Sam Smith/ }))
    await userEvent.click(screen.getByRole('combobox', { name: 'Group' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Research' }))
    await waitFor(() => {
      const asked = calls.filter((c) => c.path.startsWith('/api/admin/usage/prompts?')).at(-1)!
      const q = new URLSearchParams(asked.path.split('?')[1])
      expect([q.get('person'), q.get('group')]).toEqual(['p1', 'g1'])
    })
  })
})

describe('settings: past costs worked out again', () => {
  it('counts first, then recalculates only when confirmed', async () => {
    const counted = { requests: { rows: 12, before: 0, after: 0.42, unpriced: 3 }, answers: { rows: 5, before: 0, after: 0.01, unpriced: 0 }, applied: false }
    const calls = fakeApi(admin, {
      'GET /api/admin/config': () => ({
        json: {
          restartNeeded: false,
          groups: [{
            title: 'Prices',
            settings: [{
              key: 'Prices:InputPerMtok', group: 'Prices', label: 'Input, per million tokens', help: 'h', type: 'number', scope: 'live', options: null, min: 0, max: 1000,
              patternHelp: null, unit: '$ per 1M tokens', optional: false, impact: null, dangerous: false, default: '0.20', value: '0.20', isSet: true, source: 'default',
              environmentValue: null, restartPending: false,
            }],
          }],
        },
      }),
      'POST /api/admin/usage/recalculate': (body) => ({ json: { ...counted, applied: (body as { apply: boolean }).apply } }),
    })
    renderApp('/admin/settings#prices')
    // The price's unit is beside it.
    expect(await screen.findByText('$ per 1M tokens')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: /Count/ }))
    expect(await screen.findByText(/12 requests at the gateway: \$0\.00 → \$0\.42/)).toBeInTheDocument()
    expect(screen.getByText(/3 cannot be priced again/)).toBeInTheDocument()
    expect(calls.filter((c) => c.path === '/api/admin/usage/recalculate').map((c) => (c.body as { apply: boolean; onlyFree: boolean }))).toEqual([
      expect.objectContaining({ apply: false, onlyFree: true }),
    ])
    await userEvent.click(screen.getByRole('button', { name: 'Recalculate 17' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Recalculate' }))
    await waitFor(() => expect(calls.filter((c) => c.path === '/api/admin/usage/recalculate').at(-1)?.body).toMatchObject({ apply: true, onlyFree: true }))
    expect(await screen.findAllByText(/\(done\)/, { selector: 'li' })).toHaveLength(2)
  })
})
