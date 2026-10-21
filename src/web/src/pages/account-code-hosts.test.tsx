import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

describe('code hosts', () => {
  it('links a GitHub token and shows who it is, never the token', async () => {
    let linked = false
    const calls = fakeApi(member, {
      'GET /api/account/code-hosts': () => ({
        json: [
          { kind: 'github', title: 'GitHub', linked, account: linked ? 'octocat' : null, linkedAt: null },
          { kind: 'gitea', title: 'Gitea', linked: false, account: null, linkedAt: null },
        ],
      }),
      'PUT /api/account/code-hosts/github': () => {
        linked = true
        return { json: { kind: 'github', account: 'octocat' } }
      },
    })
    renderApp('/account')
    const list = await screen.findByRole('list', { name: 'Code hosts' })
    await userEvent.type(within(list).getByLabelText('GitHub token'), 'ghp-secret')
    await userEvent.click(within(list).getAllByRole('button', { name: 'Link' })[0]!)
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ token: 'ghp-secret' }))
    expect(await within(list).findByText('as octocat')).toBeInTheDocument()
    expect(within(list).getByLabelText('GitHub token')).toHaveValue('')
  })
})
