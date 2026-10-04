import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, member, renderApp } from '@/test/utils'
import type { AnswerTrace, TraceSummary, TraceTokens } from './traces-api'

const tokens = (prompt: number, cached: number, completion: number): TraceTokens => ({ prompt, cached, completion, cacheShare: prompt ? cached / prompt : null })

const slowest = { kind: 'agent' as const, label: 'Sub-agent 2', ms: 518_000, share: 0.62, detail: 'Mostly the model: 2,900 tokens written at 5.6 a second, 84,000 read (80% from the cache).' }

const summary: TraceSummary = {
  id: 'a9', at: '2026-10-04T10:00:00Z', person: { id: 'p1', userName: 'grace', displayName: 'Grace Hopper' }, model: 'Flash-Next', status: 'complete',
  ms: 835_000, queueMs: 0, rounds: 2, toolCalls: 1, agents: 4, tools: [{ name: 'web_search', count: 4, ms: 20_000 }, { name: 'delegate', count: 1, ms: 520_000 }],
  tokens: tokens(14_000, 12_000, 2_100), agentTokens: tokens(300_000, 240_000, 9_000), slowest,
}

const trace: AnswerTrace = {
  id: 'a9', conversationId: 'c1', at: '2026-10-04T10:00:00Z', model: 'Flash-Next', status: 'complete', ms: 835_000, queueMs: 0, setupMs: 1_200,
  rounds: 2, toolCalls: 1, agents: 2, tokens: tokens(14_000, 12_000, 2_100), agentTokens: tokens(160_000, 128_000, 4_600),
  prompt: [{ kind: 'system', chars: 9_000, tokens: 2_500 }, { kind: 'tools', chars: 3_000, tokens: 900 }, { kind: 'you', chars: 400, tokens: 100 }],
  person: { id: 'p1', userName: 'grace', displayName: 'Grace Hopper' },
  slowest,
  steps: [
    { kind: 'setup', label: 'Getting ready', ms: 1_200, slowest: false, index: null, status: null, thinkingMs: null, firstTokenMs: null, tokens: null, readPerSecond: null, writePerSecond: null, speedFrom: null, name: null, resultChars: null, files: null, agents: null },
    {
      kind: 'round', label: 'Model, round 1', ms: 40_000, slowest: false, index: 1, status: 'complete', thinkingMs: 12_000, firstTokenMs: 9_000, tokens: tokens(7_000, 6_000, 300),
      readPerSecond: 160, writePerSecond: 9.8, speedFrom: 'engine', name: null, resultChars: null, files: null, agents: null,
    },
    {
      kind: 'agents', label: 'Sub-agents', ms: 520_000, slowest: false, index: null, status: 'complete', thinkingMs: null, firstTokenMs: null, tokens: null, readPerSecond: null,
      writePerSecond: null, speedFrom: null, name: 'delegate', resultChars: 6_000, files: 0,
      agents: [
        { index: 1, label: 'Sub-agent 1', ms: 255_000, model: 'Flash-Next', tokens: tokens(70_000, 56_000, 1_700), modelMs: 230_000, readPerSecond: 150, writePerSecond: 7.4, speedFrom: 'clock', failed: false, slowest: false, steps: [{ name: 'web_search', ms: 2_000, failed: false, resultChars: 3_000 }] },
        { index: 2, label: 'Sub-agent 2', ms: 518_000, model: 'Flash-Next', tokens: tokens(84_000, 67_000, 2_900), modelMs: 500_000, readPerSecond: 140, writePerSecond: 5.6, speedFrom: 'clock', failed: false, slowest: true, steps: [{ name: 'fetch_page', ms: 9_000, failed: false, resultChars: 4_000 }] },
      ],
    },
  ],
}

describe('traces', () => {
  it('lists the slowest answers across people and opens one, naming its slowest step', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/traces': () => ({ json: { answers: [summary] } }),
      'GET /api/admin/traces/a9': () => ({ json: trace }),
    })
    renderApp('/admin/traces')
    expect(await screen.findByText('Grace Hopper')).toBeInTheDocument()
    expect(screen.getByText('Web search ×4')).toBeInTheDocument()
    expect(screen.getAllByText(/Sub-agent 2/).length).toBeGreaterThan(0)
    // The last day by default, as a time range the server filters on.
    const asked = new URL(calls.find((c) => c.path.startsWith('/api/admin/traces?'))!.path, 'https://llm.test')
    expect(Date.parse(asked.searchParams.get('to')!) - Date.parse(asked.searchParams.get('from')!)).toBe(24 * 3600 * 1000)

    await userEvent.click(screen.getByRole('button', { name: /Open the trace of the 13 min 55 s answer/ }))
    const panel = await screen.findByRole('dialog', { name: 'Answer trace' })
    const named = await within(panel).findByRole('region', { name: 'Slowest step' })
    expect(named).toHaveTextContent('Slowest step: Sub-agent 2 · 8 min 38 s (62% of the answer)')
    expect(named).toHaveTextContent('Mostly the model: 2,900 tokens written at 5.6 a second')
    const timeline = within(panel).getByRole('list', { name: 'Timeline' })
    expect(within(timeline).getByRole('listitem', { name: 'Model, round 1' })).toHaveTextContent('reads 160 a second, writes 9.8 a second')
    expect(within(timeline).getByRole('listitem', { name: 'Sub-agent 2' })).toHaveTextContent('Slowest')
    expect(within(timeline).getByRole('listitem', { name: 'Sub-agent 1' })).toHaveTextContent('(worked out from the times)')
    expect(within(panel).getByRole('region', { name: 'The prompt by part' })).toHaveTextContent('Tool definitions')
  })

  it('a longer time range asks again', async () => {
    const calls = fakeApi(admin, { 'GET /api/admin/traces': () => ({ json: { answers: [] } }) })
    renderApp('/admin/traces')
    expect(await screen.findByText('No answers in this time')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('combobox', { name: 'Time range' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Last 7 days' }))
    await waitFor(() => expect(calls.filter((c) => c.path.startsWith('/api/admin/traces?')).length).toBe(2))
    const asked = new URL(calls.filter((c) => c.path.startsWith('/api/admin/traces?')).at(-1)!.path, 'https://llm.test')
    expect(Date.parse(asked.searchParams.get('to')!) - Date.parse(asked.searchParams.get('from')!)).toBe(7 * 24 * 3600 * 1000)
  })

  it('is for admins only', async () => {
    fakeApi(member)
    renderApp('/admin/traces')
    expect(await screen.findByRole('heading', { name: 'Admins only' })).toBeInTheDocument()
  })
})
