import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

const line = {
  running: [{ id: 't1', person: { id: 'p1', userName: 'ann', displayName: 'Ann' }, model: 'Qwen3-4B', kind: 'chat', since: '', seconds: 42, position: null, priority: null }],
  waiting: [
    { id: 't2', person: { id: 'p2', userName: 'ben', displayName: 'Ben' }, model: 'Qwen3-4B', kind: 'api', since: '', seconds: 3, position: 1, priority: 5 },
    { id: 't3', person: { id: 'p3', userName: 'cy', displayName: null }, model: 'Qwen3-4B', kind: 'chat', since: '', seconds: 1, position: 2, priority: 0 },
  ],
  places: { 'Qwen3-4B': 1 },
  answersAtOnce: 0,
  answersPerPerson: 1,
  queueLength: 20,
  queueTimeoutMinutes: 10,
}

describe('line', () => {
  it('shows who runs and who waits, in the order they will be served, chat or API', async () => {
    fakeApi({ ...member, permissions: ['usage.read'] }, { 'GET /api/admin/line': () => ({ json: line }) })
    renderApp('/admin/line')
    const waiting = await screen.findByRole('table', { name: 'Waiting' })
    const rows = within(waiting).getAllByRole('row').slice(1)
    expect(rows.map((r) => within(r).getAllByRole('cell').map((c) => c.textContent))).toEqual([
      ['1', 'Ben (ben)', 'Qwen3-4B', 'API', '3.0 s', '5'],
      ['2', 'cy', 'Qwen3-4B', 'Chat', '1.0 s', '0'],
    ])
    expect(within(screen.getByRole('table', { name: 'Running' })).getByRole('link', { name: 'Ann (ann)' })).toHaveAttribute('href', '/admin/people/p1')
    expect(screen.getByText(/at most 20 waiting, the next refused as busy/)).toBeInTheDocument()
    expect(screen.getByText(/: 1 at once/)).toBeInTheDocument()
    // Only with usage.line may one take a request out.
    expect(screen.queryByRole('button', { name: /^(Remove|Stop) / })).not.toBeInTheDocument()
  })

  it('takes a waiting request out of the line, or stops a running one, with a reason the person is told', async () => {
    const calls = fakeApi({ ...member, permissions: ['usage.read', 'usage.line'] }, {
      'GET /api/admin/line': () => ({ json: { ...line, replica: 'app-1-a1b2c3' } }),
      'POST /api/admin/line/t2/remove': () => ({ json: { was: 'waiting' } }),
      'POST /api/admin/line/t1/remove': () => ({ status: 404, json: { status: 'not_in_line', error: 'That request is no longer in the line: it may have just ended.' } }),
    })
    renderApp('/admin/line')
    expect(await screen.findByText(/the line of replica/)).toHaveTextContent('app-1-a1b2c3')

    await userEvent.click(screen.getByRole('button', { name: 'Remove Ben’s request on Qwen3-4B' }))
    const dialog = await screen.findByRole('dialog', { name: 'Take Ben’s request out of the line?' })
    // The reason is required.
    await userEvent.click(within(dialog).getByRole('button', { name: 'Take it out' }))
    expect(await within(dialog).findByText('Say why: they are told.')).toBeInTheDocument()
    expect(calls.some((c) => c.method === 'POST')).toBe(false)
    // A reason often given fills the box, to say more after it.
    await userEvent.click(within(dialog).getByRole('button', { name: 'The model is needed for others now' }))
    await userEvent.type(within(dialog).getByLabelText('Reason'), ' (the review at noon)')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Take it out' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST')).toMatchObject({ path: '/api/admin/line/t2/remove', body: { reason: 'The model is needed for others now (the review at noon)' } }))
    expect(await screen.findByText('Taken out of the line')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())

    // One that ended meanwhile: said so, and the dialog closes.
    await userEvent.click(screen.getByRole('button', { name: 'Stop Ann’s request on Qwen3-4B' }))
    const stop = await screen.findByRole('dialog', { name: 'Stop Ann’s request?' })
    await userEvent.type(within(stop).getByLabelText('Reason'), 'A runaway agent')
    await userEvent.click(within(stop).getByRole('button', { name: 'Stop it' }))
    expect(await screen.findByText('It had ended already')).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })
})
