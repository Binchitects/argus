import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

const none = { check: null, files: [], checkResult: null, checkOutput: null, checkedAt: null }

const plan = {
  id: 'p1', title: 'Launch', goal: 'Live by December', conversationId: 'c1', source: 'chat', place: null, review: null, updatedAt: '', done: 0,
  warnings: ['Step 2 starts 2026-11-05, before step 1 it waits for is due (2026-11-10).'],
  steps: [
    { number: 1, title: 'Design', notes: null, owner: 'Ann', status: 'todo', start: '2026-11-01', due: '2026-11-10', dependsOn: [], ...none },
    { number: 2, title: 'Build', notes: null, owner: 'Ben', status: 'doing', start: '2026-11-05', due: '2026-11-25', dependsOn: [1], ...none },
  ],
}

const codePlan = {
  id: 'p2', title: 'Rename the parser', goal: 'parse_frame everywhere', conversationId: 'c2', source: 'code', place: 'codec', review: null, updatedAt: '', done: 1, warnings: [],
  steps: [
    { number: 1, title: 'Find the callers', notes: null, owner: null, status: 'done', start: null, due: null, dependsOn: [], ...none, check: 'grep -rn parse src', checkResult: 'passed', checkOutput: '0 matches' },
    { number: 2, title: 'Rename it', notes: 'four callers', owner: null, status: 'doing', start: null, due: null, dependsOn: [1], ...none, check: 'make test', files: ['src/parse.c', 'src/parse.h'], checkResult: 'failed', checkOutput: '1 failed' },
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

  it("shows Code Arena's working plan with what done means, its files and its checks, and changes nothing of it", async () => {
    const calls = fakeApi(member, { 'GET /api/plans/p2': () => ({ json: codePlan }) })
    renderApp('/plans/p2')
    expect(await screen.findByText("Code Arena's working plan")).toBeInTheDocument()
    expect(screen.getByText(/keeps this plan in codec/)).toBeInTheDocument()
    const doing = screen.getByRole('region', { name: 'Doing' })
    expect(within(doing).getByText('make test')).toBeInTheDocument()
    expect(within(doing).getByText('check failed')).toHaveAttribute('title', '1 failed')
    expect(within(within(doing).getByRole('list', { name: "Step 2's files" })).getAllByRole('listitem').map((li) => li.textContent)).toEqual(['src/parse.c', 'src/parse.h'])
    expect(within(screen.getByRole('region', { name: 'Done' })).getByText('check passed')).toBeInTheDocument()
    // Nothing to move or add: the agent keeps it.
    expect(within(doing).queryByRole('button', { name: 'Done' })).not.toBeInTheDocument()
    expect(screen.queryByRole('textbox', { name: 'A new step' })).not.toBeInTheDocument()
    expect(calls.some((c) => c.method !== 'GET')).toBe(false)
  })

  it("says a plan Code Arena's agent proposed waits for its review there", async () => {
    fakeApi(member, { 'GET /api/plans/p2': () => ({ json: { ...codePlan, review: 'proposed' } }) })
    renderApp('/plans/p2')
    expect(await screen.findByText('Proposed: waiting for its review in Code Arena')).toBeInTheDocument()
    expect(screen.getByText(/approve it to carry it out/)).toBeInTheDocument()
  })

  it('lists the plans in progress first, then those done', async () => {
    fakeApi(member, {
      'GET /api/plans': () => ({
        json: [
          { id: 'p1', title: 'Launch', conversationId: 'c1', source: 'chat', place: null, updatedAt: '', steps: 2, done: 2 },
          { id: 'p2', title: 'Rename the parser', conversationId: 'c2', source: 'code', place: 'codec', updatedAt: '', steps: 3, done: 1 },
        ],
      }),
    })
    renderApp('/plans')
    const going = await screen.findByRole('list', { name: 'Plans in progress' })
    expect(within(going).getByRole('link', { name: /Rename the parser/ })).toHaveTextContent('1 of 3 done')
    expect(within(going).getByText('codec')).toBeInTheDocument()
    expect(within(screen.getByRole('list', { name: 'Plans done' })).getByRole('link', { name: /Launch/ })).toBeInTheDocument()
  })
})
