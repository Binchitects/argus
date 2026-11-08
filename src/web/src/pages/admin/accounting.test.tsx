import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'
import type { AccountingReport, AccountRow } from './accounting'

const row = (kind: AccountRow['kind'], id: string | null, name: string, share: number, cost: number): AccountRow => ({
  kind, id, name, email: kind === 'person' ? `${name.toLowerCase()}@example.test` : null, costCentre: null, people: 1, requests: 10, tokens: 5000, modelHours: 0.5, charged: 2 * share, share, cost,
})

const report = (basis: AccountingReport['basis']): AccountingReport => ({
  basis,
  settings: { powerPricePerKwh: 0.2, pue: 1.4, otherPowerWatts: 300, hardwarePrice: 36000, hardwareMonths: 36, servicePerMonth: 500 },
  running: { hours: 720, gpuKwh: 100, otherKwh: 216, kwh: 442.4, power: 88.48, hardware: 985.63, service: 492.81, total: 1566.92 },
  used: { requests: 20, tokens: 10000, modelHours: 1, charged: 2, people: 2 },
  unit: { perMillionTokens: 156692, perRequest: 78.35, perPerson: 783.46, perKwh: 0.2 },
  people: [row('person', 'p1', 'Ann', 0.75, 1175.19), row('person', null, '(attributed to nobody)', 0.25, 391.73)],
  groups: [row('group', 'g1', 'Research', 1, 1566.92)],
  costCentres: [],
  projects: [row('project', 'pr1', 'Atlas', 1, 1566.92)],
})

describe('accounting', () => {
  it('shows the running cost, what a unit costs and each one’s part, and shares it out by another basis on demand', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/accounting': (_b, _i, url) => ({ json: report((url.searchParams.get('basis') as AccountingReport['basis'] | null) ?? 'spend') }),
    })
    renderApp('/admin/accounting')
    const running = await screen.findByRole('region', { name: 'Running cost' })
    expect(within(running).getByText('$1,566.92')).toBeInTheDocument()
    expect(within(running).getByText(/the GPUs 100 kWh, the rest 216 kWh, × 1.4 overhead/)).toBeInTheDocument()
    expect(within(running).getByText('$36,000.00 over 36 months')).toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'What a unit costs' })).getByText('$78.35')).toBeInTheDocument()

    expect(within(screen.getByRole('table', { name: 'Groups' })).getByText('Research')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('tab', { name: 'People' }))
    const people = screen.getByRole('table', { name: 'People' })
    expect(within(people).getByRole('link', { name: 'Ann' })).toHaveAttribute('href', '/admin/people/p1')
    expect(within(people).getByText('75.0%')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('tab', { name: 'Cost centres' }))
    expect(screen.getByText(/No group has a cost centre/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('tab', { name: 'Projects' }))
    expect(screen.getByRole('link', { name: 'Atlas' })).toHaveAttribute('href', '/projects/pr1')

    await userEvent.click(screen.getByRole('combobox', { name: 'Share the cost out' }))
    await userEvent.click(await screen.findByRole('option', { name: 'By tokens' }))
    await waitFor(() => expect(new URL(calls.at(-1)!.path, 'https://x').searchParams.get('basis')).toBe('tokens'))
    const csv = new URL(screen.getByRole('link', { name: 'CSV' }).getAttribute('href')!, 'https://x')
    expect([csv.pathname, csv.searchParams.get('format'), csv.searchParams.get('basis')]).toEqual(['/api/admin/accounting', 'csv', 'tokens'])
    expect(screen.getByRole('link', { name: 'Costs' })).toHaveAttribute('href', '/admin/settings#costs')
  })
})
