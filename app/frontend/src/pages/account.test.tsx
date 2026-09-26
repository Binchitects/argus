import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

describe('account', () => {
  it('a new API key needs confirming, then is shown once', async () => {
    const calls = fakeApi(member, {
      'GET /api/account/keys': () => ({ json: { keys: [{ alias: 'mo', preview: 'sk-...abcd', spend: 1, blocked: false, createdAt: '2026-09-01T10:00:00Z' }], spend: 1, budget: 10 } }),
      'POST /api/account/keys/rotate': () => ({ json: { apiKey: 'sk-secret-new-key' } }),
    })
    renderApp('/account')
    expect(await screen.findByText('sk-...abcd')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'New key' }))
    const dialog = await screen.findByRole('alertdialog')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    expect(calls.some((c) => c.path === '/api/account/keys/rotate')).toBe(false)

    await userEvent.click(screen.getByRole('button', { name: 'New key' }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Make a new key' }))
    const secret = await screen.findByLabelText('API key')
    expect(secret).not.toHaveTextContent('sk-secret-new-key')
    await userEvent.click(screen.getByRole('button', { name: 'Show API key' }))
    expect(secret).toHaveTextContent('sk-secret-new-key')
    expect(screen.getByRole('meter', { name: 'Credit used' })).toHaveAttribute('aria-valuenow', '10')
  })

  it('the new passwords must match, and a change is sent once they do', async () => {
    const calls = fakeApi(member, { 'POST /api/account/password': () => ({ status: 204 }) })
    renderApp('/account')
    await userEvent.type(await screen.findByLabelText('Current password'), 'old-password')
    await userEvent.type(screen.getByLabelText('New password'), 'a brand new passphrase')
    await userEvent.type(screen.getByLabelText('New password again'), 'something else entirely')
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }))
    expect(await screen.findByText('The new passwords differ.')).toBeInTheDocument()
    expect(calls.some((c) => c.path === '/api/account/password')).toBe(false)

    await userEvent.clear(screen.getByLabelText('New password again'))
    await userEvent.type(screen.getByLabelText('New password again'), 'a brand new passphrase')
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/account/password')?.body).toEqual({ current: 'old-password', next: 'a brand new passphrase' }))
  })

  it('directory accounts change their password in the directory', async () => {
    fakeApi({ ...member, source: 'ldap' })
    renderApp('/account')
    expect(await screen.findByText(/managed by the company directory/)).toBeInTheDocument()
    expect(screen.queryByLabelText('Current password')).not.toBeInTheDocument()
  })

  it('turning on two-factor shows the key and then the recovery codes', async () => {
    const calls = fakeApi(member, {
      'POST /api/account/2fa/setup': () => ({ json: { sharedKey: 'JBSW Y3DP', uri: 'otpauth://totp/LLM:mo?secret=JBSWY3DP' } }),
      'POST /api/account/2fa/enable': () => ({ json: { recoveryCodes: ['aaaa-1111', 'bbbb-2222'] } }),
    })
    renderApp('/account')
    await userEvent.click(await screen.findByRole('button', { name: 'Set up' }))
    expect(await screen.findByText('JBSW Y3DP')).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('Code'), '123456')
    await userEvent.click(screen.getByRole('button', { name: 'Turn on' }))
    expect(await screen.findByText('aaaa-1111')).toBeInTheDocument()
    expect(calls.find((c) => c.path === '/api/account/2fa/enable')?.body).toEqual({ code: '123456' })
  })

  it('the theme choice applies at once and is remembered', async () => {
    fakeApi(member)
    renderApp('/account')
    await userEvent.click(await screen.findByRole('radio', { name: 'Dark' }))
    expect(document.documentElement).toHaveClass('dark')
    expect(localStorage.getItem('theme')).toBe('dark')
  })
})
