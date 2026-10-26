import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

describe('the sign-in notice', () => {
  it('is accepted in its version before anything else, and the app opens after', async () => {
    let me = { ...member, mustAcceptNotice: true }
    const calls = fakeApi(null, {
      'GET /api/auth/me': () => ({ json: me }),
      'GET /api/account/notice': () => ({ json: { on: true, title: 'Acceptable use', text: 'Use it for **work**.', version: '3', acceptedVersion: '2' } }),
      'POST /api/account/notice/accept': () => {
        me = { ...member, mustAcceptNotice: false }
        return { status: 204 }
      },
    })
    renderApp('/chat')
    expect(await screen.findByRole('heading', { name: 'Acceptable use' })).toBeInTheDocument()
    expect(screen.getByText(/The notice changed since you last accepted it/)).toBeInTheDocument()
    expect(await screen.findByText('work')).toBeInTheDocument()
    expect(screen.queryByRole('navigation', { name: /main/i })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'I have read it and accept it' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/account/notice/accept')?.body).toEqual({ version: '3' }))
    expect(await screen.findByRole('navigation', { name: /main/i })).toBeInTheDocument()
  })

  it('is on the sign-in page for anyone to read', async () => {
    fakeApi(null, {
      'GET /api/info': () => ({ json: { name: 'Argus Arena', version: '1', notice: { title: 'Acceptable use', text: 'No customer data in prompts.', version: '1' } } }),
      'GET /api/auth/company': () => ({ json: { label: null } }),
    })
    renderApp('/login')
    expect(await screen.findByText('By signing in you accept: Acceptable use')).toBeInTheDocument()
    expect(await screen.findByText('No customer data in prompts.')).toBeInTheDocument()
  })
})
