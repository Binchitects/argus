import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

const plan = {
  id: 'p1', title: 'Launch', goal: 'Live by December', conversationId: 'c1', updatedAt: '', done: 0,
  warnings: ['Step 2 starts 2026-11-05, before step 1 it waits for is due (2026-11-10).'],
  steps: [
    { number: 1, title: 'Design', notes: null, owner: 'Ann', status: 'todo', start: '2026-11-01', due: '2026-11-10', dependsOn: [] },
    { number: 2, title: 'Build', notes: null, owner: 'Ben', status: 'doing', start: '2026-11-05', due: '2026-11-25', dependsOn: [1] },
  ],
}

describe('plans', () => {
  it('shows a plan as a board, moves a step on, and as a timeline', async () => {
    const calls = fakeApi(member, {
      'GET /api/plans/p1': () => ({ json: plan }),
      'PATCH /api/plans/p1/steps/1': () => ({ json: { ...plan, done: 1, steps: [{ ...plan.steps[0], status: 'done' }, plan.steps[1]] } }),
    })
    renderApp('/plans/p1')
    const todo = await screen.findByRole('region', { name: 'To do' })
    expect(within(todo).getByText('1. Design')).toBeInTheDocument()
    expect(screen.getByText(/before step 1 it waits for/)).toBeInTheDocument()
    await userEvent.click(within(todo).getByRole('button', { name: 'Done' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PATCH')?.body).toEqual({ status: 'done' }))
    expect(within(await screen.findByRole('region', { name: 'Done' })).getByText('1. Design')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('radio', { name: 'Timeline' }))
    expect(within(screen.getByRole('list', { name: 'Timeline' })).getAllByRole('listitem')).toHaveLength(2)
  })
})
