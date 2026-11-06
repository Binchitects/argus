import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { makeQueryClient, Providers } from '@/app/providers'
import { fakeApi } from '@/test/utils'
import { CodeGoalLine } from './code-goal'
import type { ChatCodeGoal } from './code-goal-api'

function show(chat: string) {
  const client = makeQueryClient()
  client.setDefaultOptions({ queries: { ...client.getDefaultOptions().queries, retry: false } })
  return render(
    <Providers client={client}>
      <CodeGoalLine chat={chat} />
    </Providers>,
  )
}

const goal: ChatCodeGoal = {
  text: 'The tests pass', check: { kind: 'command', text: 'dotnet test', by: 'person' }, budget: { steps: 200, minutes: 60, tokens: 5_000_000 }, status: 'met',
  turns: 3, steps: 41, tokens: 812_000, ms: 420_000, passed: true, said: 'Exit code 0.', checkedAt: '2026-11-06T10:07:00Z',
  outcome: 'Goal met: `dotnet test` succeeded (exit code 0), after 3 turns.', startedAt: '2026-11-06T10:00:00Z', updatedAt: '2026-11-06T10:07:00Z',
}

describe("a Code Arena session's goal on the web", () => {
  it('shows what the agent works toward there, what it spent, its check and how it ended', async () => {
    fakeApi(null, { 'GET /api/chat/conversations/ca1/goal': () => ({ json: goal }) })
    show('ca1')
    const line = await screen.findByRole('region', { name: /^Goal, / })
    expect(line).toHaveTextContent('Goal, met: The tests pass')
    expect(line).toHaveTextContent('3 turns · 41 of 200 steps · 7 min 0 s of 60 min · 812k of 5M tokens · check: dotnet test')
    expect(line).toHaveTextContent('Goal met: `dotnet test` succeeded (exit code 0), after 3 turns.')
  })

  it('shows nothing for a chat with no goal', async () => {
    const calls = fakeApi(null, { 'GET /api/chat/conversations/ca2/goal': () => ({ status: 204 }) })
    show('ca2')
    await new Promise((r) => setTimeout(r, 50))
    expect(calls.some((c) => c.path === '/api/chat/conversations/ca2/goal')).toBe(true)
    expect(screen.queryByRole('region', { name: /^Goal, / })).toBeNull()
  })
})
