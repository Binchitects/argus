import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

const tool = (id: string, title: string, over: object = {}) => ({
  id, title, description: `${title} does things.`, icon: 'calculator', unavailable: null,
  setting: { enabled: true, audience: 'Everyone', onByDefault: true, askFirst: false, groups: [] }, server: null, ...over,
})

describe('admin tools', () => {
  it('a tool is turned off, given to a group, and set to ask first', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/tools': () => ({ json: [tool('calculator', 'Calculator'), tool('image', 'Image generation', { unavailable: 'The gateway serves no image model.' })] }),
      'PUT /api/admin/tools/calculator': () => ({ status: 204 }),
      'GET /api/admin/groups': () => ({ json: [{ id: 'g1', name: 'Finance', description: null, directory: null, members: 3, createdAt: '' }] }),
    })
    renderApp('/admin/tools')
    const card = (await screen.findByRole('heading', { name: /Calculator/ })).closest('section')!
    expect(screen.getByText('The gateway serves no image model.')).toBeInTheDocument()
    await userEvent.click(within(card).getByRole('switch', { name: 'Calculator on' }))
    await waitFor(() => expect(calls.filter((c) => c.method === 'PUT').at(-1)?.body).toMatchObject({ enabled: false }))

    // "Chosen groups" is only saved once a group is chosen.
    await userEvent.click(within(card).getByRole('combobox', { name: 'Who may use it' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Chosen groups' }))
    expect(calls.filter((c) => c.method === 'PUT')).toHaveLength(1)
    await userEvent.click(within(card).getByRole('button', { name: 'Choose groups' }))
    await userEvent.click(await screen.findByRole('checkbox', { name: /Finance/ }))
    await waitFor(() => expect(calls.filter((c) => c.method === 'PUT').at(-1)?.body).toMatchObject({ audience: 'Groups', groups: ['g1'] }))

    await userEvent.click(within(card).getByRole('switch', { name: /Ask before each call/ }))
    await waitFor(() => expect(calls.filter((c) => c.method === 'PUT').at(-1)?.body).toMatchObject({ askFirst: true }))
  })

  it('an MCP server is tested, then added', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/tools': () => ({ json: [] }),
      'POST /api/admin/tools/servers/test': () => ({ json: { ok: true, tools: [{ name: 'echo', description: 'Says it back' }] } }),
      'POST /api/admin/tools/servers': () => ({ status: 201, json: { id: 's1', toolId: 'mcp:s1' } }),
    })
    renderApp('/admin/tools')
    await userEvent.click(await screen.findByRole('button', { name: 'Add MCP server' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add an MCP server' })
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Echo desk')
    await userEvent.type(within(dialog).getByLabelText('Address'), 'https://tools.example.test/mcp')
    await userEvent.type(within(dialog).getByLabelText('Header name'), 'X-Api-Key')
    await userEvent.type(within(dialog).getByLabelText('Header value'), 'secret')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Test' }))
    expect(await within(dialog).findByText('Connected: 1 tools')).toBeInTheDocument()
    expect(within(dialog).getByText('echo')).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add server' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'POST' && c.path === '/api/admin/tools/servers')?.body).toMatchObject({ name: 'Echo desk', url: 'https://tools.example.test/mcp', headerName: 'X-Api-Key', headerValue: 'secret' }),
    )
  })
})
