import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'
import type { RulesResponse, SafeguardEvent } from './safeguards-api'

const rule = (id: string, name: string, extra: object = {}) => ({
  rule: {
    id, name, description: null, order: 10, enabled: true, monitor: false, detector: 'secrets', stages: ['question', 'file', 'api'], groups: [], people: [], models: [],
    action: 'block', message: null, rerouteModel: null, strike: false, kinds: [], patterns: null, topics: null, categories: [], domains: [], domainsAllowed: false,
    extensions: [], maxFileMb: null, modelCheck: false, updatedAt: '2026-10-21T00:00:00Z', ...extra,
  },
  week: 3,
})

const rules: RulesResponse = {
  rules: [rule('r1', 'Secrets in what people send'), rule('r2', 'Jailbreaks', { detector: 'injection', monitor: true, stages: ['question', 'api'] })] as RulesResponse['rules'],
  detectors: ['secrets', 'pii', 'words', 'topic', 'injection', 'links', 'files'],
  stages: ['question', 'file', 'answer', 'tool_call', 'tool_result', 'api'],
  actions: ['block', 'mask', 'warn', 'justify', 'log', 'reroute', 'allow'],
  categories: [{ key: 'weapons', description: 'weapons of mass harm' }],
  secretKinds: ['private key', 'GitHub token'],
  piiKinds: ['email', 'phone'],
}

const asked: SafeguardEvent = {
  id: 'e1', ruleId: 'r3', ruleName: 'Blocked words', stage: 'question', detector: 'words', outcome: 'blocked', monitor: false, detail: '"project falcon"',
  excerpt: 'What is the budget of Project Falcon?', justification: null, model: 'Main-Model', conversationId: 'c1', at: '2026-10-21T09:00:00Z',
  person: { id: 'p1', name: 'Rae' }, review: 'asked', reviewReason: 'I lead it.', reviewNote: null, reviewedAt: null, reviewedBy: null, allowedUntil: null,
}

function backend() {
  return fakeApi(admin, {
    'GET /api/admin/safeguards/rules': () => ({ json: rules }),
    'GET /api/admin/safeguards/templates': () => ({ json: [{ name: 'Personal data to cloud models', description: 'Masked for cloud models.', detector: 'pii', stages: ['question', 'api'], action: 'mask', models: ['@external'] }] }),
    'GET /api/admin/safeguards/stats': () => ({ json: { days: [{ day: '2026-10-21', outcomes: { blocked: 2 }, monitor: 1 }], rules: [], people: 1, waiting: 1 } }),
    'GET /api/admin/safeguards/events': (_b, _i, url) => ({ json: url.searchParams.get('review') === 'asked' ? [asked] : [asked] }),
    'GET /api/admin/groups': () => ({ json: [{ id: 'g1', name: 'Security' }] }),
    'GET /api/admin/people': () => ({ json: { warning: null, people: [] } }),
    'POST /api/admin/safeguards/rules': () => ({ status: 201, json: {} }),
    'POST /api/admin/safeguards/rules/order': () => ({ status: 204 }),
    'POST /api/admin/safeguards/test': () => ({ json: { outcome: 'blocked', text: '', reason: 'This message was not sent: it holds a private key.', reroute: null, hits: [{ ruleId: 'r1', rule: 'Secrets in what people send', detector: 'secrets', outcome: 'blocked', detail: 'a private key', monitor: false }] } }),
    'POST /api/admin/safeguards/events/e1/decide': () => ({ status: 204 }),
  })
}

describe('Admin → Safeguards', () => {
  it('lists the rules in order, moves one, and adds one from a template', async () => {
    const calls = backend()
    renderApp('/admin/safeguards')
    const list = await screen.findByRole('list', { name: 'Rules, in their order' })
    expect(within(list).getByText('Secrets in what people send')).toBeInTheDocument()
    expect(within(list).getByText('Monitor')).toBeInTheDocument()
    await userEvent.click(within(list).getByRole('button', { name: 'Move Jailbreaks up' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/admin/safeguards/rules/order')?.body).toEqual({ ids: ['r2', 'r1'] }))

    await userEvent.click(screen.getByRole('combobox', { name: 'Start from a template' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Personal data to cloud models' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByLabelText('Name')).toHaveValue('Personal data to cloud models')
    expect(within(dialog).getByLabelText(/^Models/)).toHaveValue('@external')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add the rule' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/admin/safeguards/rules')?.body).toMatchObject({ detector: 'pii', action: 'mask', models: ['@external'], groups: [] }))
  })

  it('tries a text on the bench and answers a review', async () => {
    const calls = backend()
    renderApp('/admin/safeguards')
    await userEvent.click(await screen.findByRole('tab', { name: /Test bench/ }))
    await userEvent.type(screen.getByLabelText('Text'), 'my key')
    await userEvent.click(screen.getByRole('button', { name: 'Try it' }))
    expect(await screen.findByText('This message was not sent: it holds a private key.')).toBeInTheDocument()
    expect(within(screen.getByRole('list', { name: 'Rules that fired' })).getByText('a private key')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('tab', { name: /Reviews/ }))
    expect(await screen.findByText(/I lead it\./)).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('A note for Rae'), 'Fine.')
    await userEvent.click(screen.getByRole('button', { name: 'Allow it' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/admin/safeguards/events/e1/decide')?.body).toEqual({ allow: true, note: 'Fine.' }))
  })
})
