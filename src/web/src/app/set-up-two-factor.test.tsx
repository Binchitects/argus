import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

describe('two-factor sign-in the company requires', () => {
  it('is set up first, before anything else, and the app opens once its recovery codes are kept', async () => {
    let me = { ...member, mustSetUpTwoFactor: true }
    const calls = fakeApi(null, {
      'GET /api/auth/me': () => ({ json: me }),
      'GET /api/account/2fa': () => ({ json: { enabled: false, recoveryCodesLeft: 0, required: 'the company requires it of everyone' } }),
      'POST /api/account/2fa/setup': () => ({ json: { sharedKey: 'JBSW Y3DP', uri: 'otpauth://totp/LLM:mo?secret=JBSWY3DP' } }),
      'POST /api/account/2fa/enable': () => {
        me = { ...member, twoFactorEnabled: true, mustSetUpTwoFactor: false }
        return { json: { recoveryCodes: ['aaaa-1111', 'bbbb-2222'] } }
      },
    })
    renderApp('/chat')
    expect(await screen.findByRole('heading', { name: 'Set up two-factor sign-in' })).toBeInTheDocument()
    expect(screen.queryByRole('navigation')).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Set up' }))
    await userEvent.type(await screen.findByLabelText('Code'), '123456')
    await userEvent.click(screen.getByRole('button', { name: 'Turn on' }))
    // The codes stay until they are kept, though the person no longer must set it up.
    expect(await screen.findByText('aaaa-1111')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Set up two-factor sign-in' })).toBeInTheDocument()
    expect(calls.find((c) => c.path === '/api/account/2fa/enable')?.body).toEqual({ code: '123456' })

    await userEvent.click(screen.getByRole('button', { name: 'I have kept them: continue' }))
    await waitFor(() => expect(screen.queryByRole('heading', { name: 'Set up two-factor sign-in' })).not.toBeInTheDocument())
    expect(await screen.findByRole('navigation', { name: /main/i })).toBeInTheDocument()
  })

  it('cannot be turned off on Your account while it is required', async () => {
    fakeApi({ ...member, twoFactorEnabled: true }, {
      'GET /api/account/2fa': () => ({ json: { enabled: true, recoveryCodesLeft: 8, required: 'the group Finance requires it' } }),
    })
    renderApp('/account')
    expect(await screen.findByText(/It stays on: the group Finance requires it/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Turn off' })).not.toBeInTheDocument()
  })
})
