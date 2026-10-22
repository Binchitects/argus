import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, member, renderApp } from '@/test/utils'
import { sourceUrl, type BusinessProfile, type BusinessSystem } from './api'

const checkout: BusinessSystem = {
  id: 'b1', project: 'shop/checkout', name: 'Checkout', domain: 'online orders and payments', summary: 'Takes customers’ orders and their payments.',
  status: 'ready', step: null, error: null, model: 'Main-Model', builtAt: '2026-10-23T08:00:00Z',
  counts: { capabilities: 1, entities: 1, rules: 2, processes: 1, integrations: 1, glossary: 1 },
}
const billing: BusinessSystem = { ...checkout, id: 'b2', project: 'shop/billing', name: 'billing', domain: null, summary: null, status: 'building', step: 'Reading the issues: refunds', counts: null }

const profile: BusinessProfile = {
  capabilities: [{ name: 'Take orders', description: 'Customers place orders.', sources: [{ kind: 'code', ref: 'src/orders.cs:10' }] }],
  entities: [{ name: 'Order', meaning: 'What a customer buys.', sources: [{ kind: 'code', ref: 'src/order.cs:3' }] }],
  rules: [
    { rule: 'Refunds round half up', why: 'Finance asked for it to match invoices.', sources: [{ kind: 'code', ref: 'src/refund.cs:22' }, { kind: 'issue', ref: '12' }] },
    { rule: 'Orders over 50 ship free', why: '', sources: [{ kind: 'wiki', ref: 'shipping' }] },
  ],
  processes: [{ name: 'A customer cancels an order', steps: ['The page calls cancel', 'Payment is refunded'], sources: [{ kind: 'mr', ref: '34' }] }],
  integrations: [{ system: 'Payments gateway', how: 'Charges and refunds cards.', sources: [{ kind: 'doc', ref: 'README.md' }] }],
  glossary: [{ term: 'Basket', meaning: 'What a customer is about to buy.', sources: [{ kind: 'wiki', ref: 'glossary' }] }],
}

const code = {
  'GET /api/code/reviews': () => ({ json: [] }),
  'GET /api/code/rules': () => ({ json: [] }),
  'GET /api/studio/workspaces': () => ({ json: { server: false, workspaces: [] } }),
}

describe('business map', () => {
  it('opens each source where it was read in GitLab', () => {
    const web = 'https://gitlab.test/shop/checkout'
    expect(sourceUrl(web, { kind: 'code', ref: 'src/refund.cs:22' })).toBe(`${web}/-/blob/HEAD/src/refund.cs#L22`)
    expect(sourceUrl(web, { kind: 'doc', ref: 'docs/a b.md' })).toBe(`${web}/-/blob/HEAD/docs/a%20b.md`)
    expect(sourceUrl(web, { kind: 'issue', ref: '#12' })).toBe(`${web}/-/issues/12`)
    expect(sourceUrl(web, { kind: 'mr', ref: '34' })).toBe(`${web}/-/merge_requests/34`)
    expect(sourceUrl(web, { kind: 'wiki', ref: 'glossary' })).toBe(`${web}/-/wikis/glossary`)
    expect(sourceUrl(null, { kind: 'issue', ref: '1' })).toBeNull()
  })

  it('lists the mapped systems in Code Studio, with one being mapped, and maps a project', async () => {
    const calls = fakeApi(member, {
      ...code,
      'GET /api/code/business': () => ({ json: { argus: true, systems: [checkout, billing] } }),
      'POST /api/code/business': () => ({ status: 201, json: { ...billing, id: 'b3', project: 'shop/stock' } }),
      'GET /api/code/business/b3': () => ({ json: { system: { ...billing, id: 'b3', project: 'shop/stock' }, profile: { capabilities: [], entities: [], rules: [], processes: [], integrations: [], glossary: [] }, web: null } }),
    })
    const { router } = renderApp('/code')
    const section = await screen.findByRole('region', { name: 'Business map' })
    const systems = await within(section).findByRole('list', { name: 'Mapped systems' })
    expect(within(systems).getByRole('link', { name: /Checkout/ })).toHaveAttribute('href', '/code/business/b1')
    expect(within(systems).getByText('1 capabilities · 2 rules · 1 processes')).toBeInTheDocument()
    expect(within(systems).getByText('Reading the issues: refunds…')).toBeInTheDocument()

    expect(within(section).getByRole('button', { name: 'Map it' })).toBeDisabled()
    await userEvent.type(within(section).getByRole('textbox', { name: 'Project to map' }), 'shop/stock')
    await userEvent.click(within(section).getByRole('button', { name: 'Map it' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/code/business')?.body).toEqual({ project: 'shop/stock' }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/code/business/b3'))
  })

  it('shows a system part by part, each item with its sources, finds in it, and asks Argus about it', async () => {
    const calls = fakeApi(member, {
      'GET /api/code/business/b1': () => ({ json: { system: checkout, profile, web: 'https://gitlab.test/shop/checkout' } }),
      'POST /api/code/ask': () => ({ json: { id: 'c9', message: 'About the project shop/checkout:\n\nExplain' } }),
      'POST /api/code/business/b1/again': () => ({ json: { ...checkout, status: 'building', step: 'Starting' } }),
    })
    const { router } = renderApp('/code/business/b1')
    expect(await screen.findByRole('heading', { name: 'Checkout' })).toBeInTheDocument()
    expect(screen.getByText('Takes customers’ orders and their payments.')).toBeInTheDocument()
    const rules = screen.getByRole('region', { name: 'Rules' })
    expect(within(rules).getByText('Why: Finance asked for it to match invoices.')).toBeInTheDocument()
    expect(within(rules).getByRole('link', { name: 'src/refund.cs:22' })).toHaveAttribute('href', 'https://gitlab.test/shop/checkout/-/blob/HEAD/src/refund.cs#L22')
    expect(within(rules).getByRole('link', { name: '#12' })).toHaveAttribute('href', 'https://gitlab.test/shop/checkout/-/issues/12')
    expect(within(screen.getByRole('region', { name: 'Processes' })).getAllByRole('listitem').map((l) => l.textContent)).toContain('Payment is refunded')
    expect(within(screen.getByRole('region', { name: 'Glossary' })).getByText('Basket')).toBeInTheDocument()
    // Only admins remove a map.
    expect(screen.queryByRole('button', { name: 'Remove the map' })).toBeNull()

    await userEvent.type(screen.getByRole('textbox', { name: 'Find in the map' }), 'refund')
    expect(within(screen.getByRole('region', { name: 'Rules' })).getAllByRole('listitem').filter((l) => l.parentElement?.closest('[aria-label="Rules"]'))).toHaveLength(1)
    expect(screen.queryByRole('region', { name: 'Glossary' })).toBeNull()

    await userEvent.click(screen.getByRole('button', { name: 'Map again' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/code/business/b1/again')).toBe(true))
    await userEvent.click(screen.getByRole('button', { name: 'Ask Argus' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/code/ask')?.body).toMatchObject({ project: 'shop/checkout' }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/chat/c9'))
  })

  it('an admin removes a map', async () => {
    const calls = fakeApi(admin, {
      ...code,
      'GET /api/code/business/b1': () => ({ json: { system: checkout, profile, web: null } }),
      'DELETE /api/code/business/b1': () => ({ status: 204 }),
      'GET /api/code/business': () => ({ json: { argus: true, systems: [] } }),
    })
    const { router } = renderApp('/code/business/b1')
    await userEvent.click(await screen.findByRole('button', { name: 'Remove the map' }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Remove' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.path === '/api/code/business/b1')).toBe(true))
    await waitFor(() => expect(router.state.location.pathname).toBe('/code'))
    // Without GitLab's address, sources are words, not links.
  })
})
