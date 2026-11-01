import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import { continueTo } from './continue'

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

  afterEach(() => vi.unstubAllGlobals())

  it('comes first on the way to another app\'s sign-in through this one, which goes on once it is accepted', async () => {
    const replace = vi.fn()
    vi.stubGlobal('location', { ...window.location, replace })
    let me = { ...member, mustAcceptNotice: true }
    fakeApi(null, {
      'GET /api/auth/me': () => ({ json: me }),
      'GET /api/account/notice': () => ({ json: { on: true, title: 'Acceptable use', text: 'Use it for work.', version: '3', acceptedVersion: null } }),
      'POST /api/account/notice/accept': () => {
        me = { ...member, mustAcceptNotice: false }
        return { status: 204 }
      },
    })
    const authorize = '/connect/authorize?client_id=argus&redirect_uri=https%3A%2F%2Fargus.llm.test%2Fsignin&response_type=code'
    renderApp(`/continue?rd=${encodeURIComponent(authorize)}`)
    await userEvent.click(await screen.findByRole('button', { name: 'I have read it and accept it' }))
    await waitFor(() => expect(replace).toHaveBeenCalledWith(authorize))
    expect(screen.queryByRole('navigation', { name: /main/i })).not.toBeInTheDocument()
  })

  it('goes on only to a path of this site', () => {
    const at = (search: string) => continueTo({ pathname: '/continue', search })
    expect(at('?rd=%2Fconnect%2Fauthorize%3Fx%3D1')).toBe('/connect/authorize?x=1')
    // A browser drops tabs and line breaks from an address before reading it: "/\t/evil.test" is another site to it.
    for (const evil of ['?rd=https%3A%2F%2Fevil.test', '?rd=%2F%2Fevil.test', '?rd=%2F%5Cevil.test', '?rd=%2F%09%2Fevil.test', '?rd=%2F%0A%2Fevil.test', '?rd=%2F%0D%5Cevil.test', ''])
      expect(at(evil)).toBe('/')
    expect(continueTo({ pathname: '/chat', search: '?rd=%2Fx' })).toBeNull()
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
