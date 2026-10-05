import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fakeApi, renderApp } from '@/test/utils'

const assign = vi.fn()
beforeEach(() => {
  assign.mockReset()
  vi.stubGlobal('location', { ...window.location, assign, protocol: 'https:', host: 'llm.test', origin: 'https://llm.test' })
})

describe('sign-in', () => {
  it('signs in and goes where the person was headed', async () => {
    const calls = fakeApi(null, { 'POST /api/auth/login': () => ({ json: { status: 'ok', redirect: '/usage' } }) })
    renderApp('/login?rd=%2Fusage')
    await userEvent.type(await screen.findByLabelText('Username or email'), 'ada')
    await userEvent.type(screen.getByLabelText('Password'), 'correct horse')
    await userEvent.click(screen.getByRole('checkbox', { name: /Keep me signed in/ }))
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
    await waitFor(() => expect(assign).toHaveBeenCalledWith('/usage'))
    expect(calls.find((c) => c.path === '/api/auth/login')?.body).toEqual({ userName: 'ada', password: 'correct horse', remember: true, redirect: '/usage' })
  })

  it('says what is missing before sending anything', async () => {
    const calls = fakeApi(null)
    renderApp('/login')
    await userEvent.click(await screen.findByRole('button', { name: 'Sign in' }))
    expect(await screen.findByText('Enter your username or email.')).toBeInTheDocument()
    expect(screen.getByLabelText('Username or email')).toHaveAttribute('aria-invalid', 'true')
    expect(calls.some((c) => c.path === '/api/auth/login')).toBe(false)
  })

  it('shows the reason a sign-in failed', async () => {
    fakeApi(null, { 'POST /api/auth/login': () => ({ status: 400, json: { status: 'invalid', error: 'Wrong username or password.' } }) })
    renderApp('/login')
    await userEvent.type(await screen.findByLabelText('Username or email'), 'ada')
    await userEvent.type(screen.getByLabelText('Password'), 'nope')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Wrong username or password.')
  })

  it('asks for the second factor, and takes a recovery code', async () => {
    const calls = fakeApi(null, {
      'POST /api/auth/login': () => ({ json: { status: '2fa' } }),
      'POST /api/auth/login/2fa': () => ({ json: { status: 'ok', redirect: '/' } }),
    })
    renderApp('/login')
    await userEvent.type(await screen.findByLabelText('Username or email'), 'ada')
    await userEvent.type(screen.getByLabelText('Password'), 'pw')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(await screen.findByRole('heading', { name: 'Two-factor sign-in' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: /Use a recovery code/ }))
    await userEvent.type(screen.getByLabelText('Recovery code'), 'abcd-1234')
    await userEvent.click(screen.getByRole('button', { name: 'Verify' }))
    await waitFor(() => expect(assign).toHaveBeenCalledWith('/'))
    expect(calls.find((c) => c.path === '/api/auth/login/2fa')?.body).toMatchObject({ code: 'abcd-1234', recovery: true })
  })
})

describe('company sign-in', () => {
  it('offers the company button when it is set up, keeping where the person was headed', async () => {
    fakeApi(null, { 'GET /api/auth/company': () => ({ json: { label: 'Okta' } }) })
    renderApp('/login?rd=%2Fusage')
    const button = await screen.findByRole('link', { name: 'Sign in with Okta' })
    expect(button).toHaveAttribute('href', '/api/auth/company/start?rd=%2Fusage')
    await userEvent.click(screen.getByRole('checkbox', { name: /Keep me signed in/ }))
    expect(button).toHaveAttribute('href', '/api/auth/company/start?rd=%2Fusage&remember=true')
    // The password form stays, for local accounts.
    expect(screen.getByLabelText('Username or email')).toBeInTheDocument()
  })

  it('offers the same button for a SAML identity provider, which the app starts the same way', async () => {
    fakeApi(null, { 'GET /api/auth/company': () => ({ json: { label: 'Entra ID', protocol: 'saml' } }) })
    renderApp('/login?rd=%2Fchat')
    expect(await screen.findByRole('link', { name: 'Sign in with Entra ID' })).toHaveAttribute('href', '/api/auth/company/start?rd=%2Fchat')
    expect(screen.getByLabelText('Username or email')).toBeInTheDocument()
  })

  it('shows no company button while it is off', async () => {
    fakeApi(null, { 'GET /api/auth/company': () => ({ json: { label: null } }) })
    renderApp('/login')
    expect(await screen.findByLabelText('Username or email')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /Sign in with/ })).toBeNull()
  })

  it('words what the identity provider’s answer came to', async () => {
    fakeApi(null, { 'GET /api/auth/company': () => ({ json: { label: 'Okta' } }) })
    renderApp('/login?error=company_not_allowed&rd=%2Fchat')
    expect(await screen.findByRole('alert')).toHaveTextContent('Your company account is not allowed to sign in here. Ask an admin.')
    expect(await screen.findByRole('link', { name: 'Sign in with Okta' })).toHaveAttribute('href', '/api/auth/company/start?rd=%2Fchat')
  })
})
