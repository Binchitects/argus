import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

describe('joining a room with a link', () => {
  it('says where it leads and joins', async () => {
    const calls = fakeApi(member, {
      'GET /api/rooms/join/t0k': () => ({ json: { room: 'r1', title: 'Launch plans', owner: 'Olga', role: 'viewer', people: 3, inside: false } }),
      'POST /api/rooms/join/t0k': () => ({ json: { id: 'r1', joined: true } }),
      'GET /api/chat/conversations': () => ({ json: [] }),
    })
    renderApp('/rooms/join/t0k')
    expect(await screen.findByText('Launch plans')).toBeInTheDocument()
    expect(screen.getByText(/Olga's room, 3 people. You join as a viewer/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Join the room' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.path === '/api/rooms/join/t0k')).toBe(true))
  })

  it('says when the link no longer works', async () => {
    fakeApi(member, {
      'GET /api/rooms/join/old': () => ({ status: 404, json: { status: 'invite', error: 'This link does not work: it expired, was used up, or was stopped. Ask for a new one.' } }),
    })
    renderApp('/rooms/join/old')
    expect(await screen.findByText(/This link does not work/)).toBeInTheDocument()
  })
})
