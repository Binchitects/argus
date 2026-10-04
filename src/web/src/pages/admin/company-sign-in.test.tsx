import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'
import type { CompanyStatus } from './company-api'
import type { SettingsData, SettingView } from './settings-model'

const setting = (over: Partial<SettingView>): SettingView => ({
  key: 'CompanySignIn:Issuer', group: 'Company sign-in', label: 'Identity provider', help: 'Its issuer.', type: 'url', scope: 'live',
  options: null, min: null, max: null, patternHelp: 'https://...', unit: null, optional: true, impact: null, dangerous: false, default: null,
  value: '', isSet: false, source: 'default', environmentValue: null, restartPending: false, ...over,
})

const settings: SettingsData = {
  groups: [
    {
      title: 'Company sign-in',
      settings: [setting({}), setting({ key: 'CompanySignIn:ClientId', label: 'Client ID', type: 'text', patternHelp: null })],
    },
  ],
  restartNeeded: false,
}

const status = (over: Partial<CompanyStatus> = {}): CompanyStatus => ({
  enabled: true, issuer: 'https://idp.example.test', label: 'Okta', adminGroup: 'llm-admins', requiredGroup: null,
  redirectUri: 'https://llm.test/api/auth/company/callback', people: 12,
  scim: { url: 'https://llm.test/scim/v2', tokenMadeAt: null, groups: 0 }, ...over,
})

describe('company sign-in settings', () => {
  it('shows the redirect URI, and tests the unsaved issuer', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/config': () => ({ json: settings }),
      'GET /api/admin/company-sign-in': () => ({ json: status() }),
      'POST /api/admin/company-sign-in/test': () => ({ json: { ok: true, message: 'Found https://idp.example.test: it signs with 2 keys.' } }),
    })
    renderApp('/admin/settings#company-sign-in')
    expect(await screen.findByText('https://llm.test/api/auth/company/callback')).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('Identity provider'), 'https://idp.example.test')
    await userEvent.click(screen.getByRole('button', { name: 'Test the identity provider' }))
    expect(await screen.findByText('Found https://idp.example.test: it signs with 2 keys.')).toBeInTheDocument()
    expect(calls.find((c) => c.path === '/api/admin/company-sign-in/test')?.body).toEqual({ 'CompanySignIn:Issuer': 'https://idp.example.test' })
  })

  it('makes a SCIM token, shows it once, and asks before replacing or revoking it', async () => {
    let made: string | null = null
    const calls = fakeApi(admin, {
      'GET /api/admin/config': () => ({ json: settings }),
      'GET /api/admin/company-sign-in': () => ({ json: status({ scim: { url: 'https://llm.test/scim/v2', tokenMadeAt: made, groups: 0 } }) }),
      'POST /api/admin/company-sign-in/scim-token': () => {
        made = new Date().toISOString()
        return { json: { token: 'scim_secret-token-value' } }
      },
      'DELETE /api/admin/company-sign-in/scim-token': () => {
        made = null
        return { status: 204 }
      },
    })
    renderApp('/admin/settings#company-sign-in')
    expect(await screen.findByText('none: SCIM is off')).toBeInTheDocument()
    expect(screen.getByText('https://llm.test/scim/v2')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Make a token' }))
    expect(await screen.findByText('Copy the token now')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Show SCIM token' }))
    expect(screen.getByLabelText('SCIM token')).toHaveTextContent('scim_secret-token-value')
    expect(await screen.findByText(/^made /)).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Make a new token' }))
    const replace = await screen.findByRole('alertdialog', { name: 'Make a new SCIM token?' })
    await userEvent.click(within(replace).getByRole('button', { name: 'Cancel' }))
    expect(calls.filter((c) => c.path === '/api/admin/company-sign-in/scim-token' && c.method === 'POST')).toHaveLength(1)

    await userEvent.click(screen.getByRole('button', { name: 'Turn off' }))
    const off = await screen.findByRole('alertdialog', { name: 'Turn SCIM off?' })
    await userEvent.click(within(off).getByRole('button', { name: 'Turn off' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE')).toBe(true))
    expect(await screen.findByText('none: SCIM is off')).toBeInTheDocument()
    expect(screen.queryByText('Copy the token now')).toBeNull()
  })
})

describe('company sign-in elsewhere', () => {
  it('the Sign-in page says how company sign-in and SCIM stand', async () => {
    fakeApi(admin, {
      'GET /api/admin/sign-in': () => ({ json: { ldap: false, ldapUrl: null, adminGroup: null, requiredGroup: null, syncMinutes: 15 } }),
      'GET /api/admin/company-sign-in': () => ({ json: status({ scim: { url: 'https://llm.test/scim/v2', tokenMadeAt: '2026-10-01T10:00:00Z', groups: 3 } }) }),
    })
    renderApp('/admin/sign-in')
    expect(await screen.findByText(/Sign in with Okta/)).toBeInTheDocument()
    expect(screen.getByText('https://idp.example.test')).toBeInTheDocument()
    expect(screen.getByText('everyone the provider lets through')).toBeInTheDocument()
    expect(screen.getByText('on: https://llm.test/scim/v2')).toBeInTheDocument()
    expect(screen.getAllByRole('link', { name: 'Configure' }).map((a) => a.getAttribute('href'))).toContain('/admin/settings#company-sign-in')
  })

  it('a SCIM group is listed as such, and its members are the provider’s to change', async () => {
    fakeApi(admin, {
      'GET /api/admin/groups': () => ({ json: [{ id: 'g1', name: 'Data Science', description: null, directory: null, scim: true, members: 1, createdAt: '' }] }),
      'GET /api/admin/groups/g1': () => ({
        json: { id: 'g1', name: 'Data Science', description: null, directory: null, scim: true, createdAt: '', members: [{ id: 'p1', userName: 'ann', displayName: 'Ann', email: 'ann@example.test', isDisabled: false }] },
      }),
    })
    const { router } = renderApp('/admin/groups')
    const row = (await screen.findByText('Data Science')).closest('tr')!
    expect(within(row).getByText('SCIM')).toBeInTheDocument()
    await router.navigate('/admin/groups/g1')
    expect(await screen.findByText(/Whoever the company's identity provider puts in this group/)).toBeInTheDocument()
    expect(screen.getByText('SCIM group')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Add people' })).toBeNull()
    expect(screen.queryByRole('button', { name: /Remove Ann/ })).toBeNull()
  })
})
