import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'
import type { AccountingReport, AccountRow } from './accounting'

const row = (kind: AccountRow['kind'], id: string | null, name: string, share: number, cost: number, more: Partial<AccountRow> = {}): AccountRow => ({
  kind, id, name, email: kind === 'person' ? `${name.toLowerCase()}@example.test` : null, costCentre: null, people: 1, requests: 10, tokens: 5000, input: 1200, cached: 3000, output: 800, modelHours: 0.5, charged: 2 * share, share, cost, ...more,
})

const ann = row('person', 'p1', 'Ann', 0.75, 1175.19)
const zed = row('person', 'p2', 'Zed', 0.2, 313.38, { requests: 90 })

const report = (basis: AccountingReport['basis']): AccountingReport => ({
  from: '2026-10-09T00:00:00Z',
  to: '2026-11-08T00:00:00Z',
  basis,
  settings: { powerPricePerKwh: 0.2, pue: 1.4, otherPowerWatts: 300, hardwarePrice: 36000, hardwareMonths: 36, servicePerMonth: 500, personHourly: 50 },
  running: { hours: 720, gpuKwh: 100, otherKwh: 216, kwh: 442.4, power: 88.48, hardware: 985.63, service: 492.81, total: 1566.92 },
  used: { requests: 20, tokens: 10000, input: 2400, cached: 6000, output: 1600, modelHours: 1, charged: 2, people: 2 },
  unit: { perMillionTokens: 156692, perRequest: 78.35, perPerson: 783.46, perModelHour: 1566.5 },
  health: { gpuBusy: 0.25, idleCost: 1175.19, monthlyRunning: 1589.77, monthlyCharged: 2.03, recovered: 0.0013 },
  before: { from: '2026-09-09T00:00:00Z', to: '2026-10-09T00:00:00Z', total: 1400, used: { requests: 10, tokens: 8000, input: 2000, cached: 4800, output: 1200, modelHours: 0.5, charged: 1, people: 1 }, totalChange: 0.1192, chargedChange: 1, tokensChange: 0.25 },
  series: [{ at: '2026-11-01T00:00:00Z', power: 3, hardware: 32.85, service: 16.43, total: 52.28, charged: 0.5, tokens: 1000, requests: 3, kwh: 15 }],
  people: [ann, zed, row('person', null, '(attributed to nobody)', 0.05, 78.35)],
  groups: [row('group', 'g1', 'Research', 0.95, 1488.57, { people: 2, credit: 100, costCentre: 'CC-7', members: [ann, zed] }), row('group', null, '(in no group)', 0, 0, { members: [] })],
  costCentres: [],
  projects: [row('project', 'pr1', 'Atlas', 1, 1566.92)],
  models: [row('model', null, 'qwen3', 1, 1566.92)],
  sources: [row('source', null, 'Chat', 1, 1566.92)],
  kinds: [row('kind', null, 'Chat answers', 1, 1566.92)],
})

describe('accounting', () => {
  it('shows the running cost and its parts against the time before, what a unit costs and the idle GPUs, and shares it out by another basis on demand', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/accounting': (_b, _i, url) => ({ json: report((url.searchParams.get('basis') as AccountingReport['basis'] | null) ?? 'spend') }),
    })
    renderApp('/admin/accounting')
    const totals = await screen.findByRole('region', { name: 'The totals' })
    expect(within(totals).getByText('$1,566.92')).toBeInTheDocument()
    expect(within(totals).getByText(/11\.9% on the time before/)).toBeInTheDocument()
    expect(within(totals).getByText('$1,589.77')).toBeInTheDocument()
    expect(within(totals).getByText(/busy 25\.0% of the time/)).toBeInTheDocument()
    expect(within(totals).getByText('$78.35')).toBeInTheDocument()
    expect(screen.getByText(/the GPUs 100 kWh, the rest 216 kWh \(300 W\), × 1.4 overhead/)).toBeInTheDocument()
    expect(screen.getByText('$36,000.00 over 36 months')).toBeInTheDocument()
    // Every cost is set and the GPUs measured: nothing to warn about.
    expect(screen.queryByText(/Set the costs/)).not.toBeInTheDocument()
    const before = screen.getByRole('table', { name: 'Against the time before' })
    expect(within(within(before).getByText('Tokens').closest('tr')!).getByText('+25.0%')).toBeInTheDocument()
    // The tokens in, from the cache and out, each against the time before.
    expect(within(within(before).getByText('Input, cached').closest('tr')!).getByText('+25.0%')).toBeInTheDocument()
    expect(totals).toHaveTextContent(/2\.4 K in, 6 K cached, 1\.6 K out/)

    // A group opens to its people; its credit and cost centre show.
    const groups = screen.getByRole('table', { name: 'Groups' })
    expect(within(groups).getByText('CC-7')).toBeInTheDocument()
    expect(within(groups).getByText('$100.00')).toBeInTheDocument()
    await userEvent.click(within(groups).getByRole('button', { name: /Research/ }))
    expect(within(groups).getByRole('link', { name: 'Ann' })).toHaveAttribute('href', '/admin/people/p1')

    // People: sorted by any column, found by name; the unattributed row stays last.
    await userEvent.click(screen.getByRole('tab', { name: 'People' }))
    const people = screen.getByRole('table', { name: 'People' })
    const names = () => within(people).getAllByRole('row').slice(1).map((r) => within(r).getAllByRole('cell')[0]!.textContent)
    expect(names()).toEqual(['Ann', 'Zed', '(attributed to nobody)'])
    await userEvent.click(within(people).getByRole('button', { name: 'Requests' }))
    expect(names()).toEqual(['Zed', 'Ann', '(attributed to nobody)'])
    await userEvent.type(screen.getByRole('textbox', { name: 'Find a person' }), 'ann')
    expect(names()).toEqual(['Ann'])
    // Each person's tokens: in, from the cache, out.
    expect(within(people).getAllByRole('button').map((b) => b.textContent)).toEqual(expect.arrayContaining(['In', 'Cached', 'Out']))

    await userEvent.click(screen.getByRole('tab', { name: 'Cost centres' }))
    expect(screen.getByText(/No group has a cost centre/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('tab', { name: 'Projects' }))
    expect(screen.getByRole('link', { name: 'Atlas' })).toHaveAttribute('href', '/projects/pr1')
    await userEvent.click(screen.getByRole('tab', { name: 'What it was for' }))
    expect(within(screen.getByRole('table', { name: 'What it was for' })).getByText('Chat answers')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('combobox', { name: 'Share the cost out' }))
    await userEvent.click(await screen.findByRole('option', { name: 'By tokens' }))
    await waitFor(() => expect(new URL(calls.at(-1)!.path, 'https://x').searchParams.get('basis')).toBe('tokens'))
    await userEvent.click(screen.getByRole('button', { name: 'Export' }))
    const pdf = new URL((await screen.findByRole('menuitem', { name: 'PDF report' })).getAttribute('href')!, 'https://x')
    expect([pdf.pathname, pdf.searchParams.get('format'), pdf.searchParams.get('basis')]).toEqual(['/api/admin/accounting/export', 'pdf', 'tokens'])
  })

  it('says what is missing from the cost: the GPUs not measured, no hardware price, no service cost', async () => {
    const r = report('spend')
    fakeApi(admin, {
      'GET /api/admin/accounting': () => ({
        json: { ...r, settings: { ...r.settings, hardwarePrice: 0, servicePerMonth: 0 }, running: { ...r.running, gpuKwh: null }, health: { ...r.health, gpuBusy: null, idleCost: null }, before: null },
      }),
    })
    renderApp('/admin/accounting')
    expect(await screen.findByText(/The GPUs’ power was not measured in this time/)).toBeInTheDocument()
    expect(screen.getByText(/No hardware price is set\. No monthly service cost is set\./)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Set the costs' })).toHaveAttribute('href', '/admin/settings#costs')
    expect(screen.queryByRole('table', { name: 'Against the time before' })).not.toBeInTheDocument()
  })
})
