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

  it('Decide (Laya) is listed with its own icon, and says when its module is off', async () => {
    fakeApi(admin, {
      'GET /api/admin/tools': () => ({
        json: [tool('laya', 'Decide (Laya)', { icon: 'scale', unavailable: 'The Laya decision model does not run here (the laya module is off: COMPOSE_PROFILES=laya turns it on).' })],
      }),
    })
    renderApp('/admin/tools')
    const card = (await screen.findByRole('heading', { name: /Decide \(Laya\)/ })).closest('section')!
    expect(within(card).getByText(/COMPOSE_PROFILES=laya turns it on/)).toBeInTheDocument()
    expect(card.querySelector('svg.lucide-scale')).not.toBeNull()
  })

  it('an MCP server is tested, then added', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/tools': () => ({ json: [] }),
      'POST /api/admin/tools/servers/test': () => ({ json: { ok: true, tools: [{ name: 'echo', description: 'Says it back' }] } }),
      'POST /api/admin/tools/servers': () => ({ status: 201, json: { id: 's1', toolId: 'mcp:s1' } }),
    })
    renderApp('/admin/tools')
    await userEvent.click(await screen.findByRole('button', { name: 'Add a server or API' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a server or API' })
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

  it('an API is added by its OpenAPI document, its writes marked as asking first', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/tools': () => ({ json: [] }),
      'POST /api/admin/tools/servers/test': () => ({
        json: { ok: true, url: 'https://pets.example.test/v1', tools: [{ name: 'pets__list_pets', description: 'Lists the pets (GET /pets)', asksFirst: false }, { name: 'pets__add_pet', description: 'Adds a pet (POST /pets)', asksFirst: true }] },
      }),
      'POST /api/admin/tools/servers': () => ({ status: 201, json: { id: 's2', toolId: 'mcp:s2' } }),
    })
    renderApp('/admin/tools')
    await userEvent.click(await screen.findByRole('button', { name: 'Add a server or API' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a server or API' })
    await userEvent.click(within(dialog).getByRole('tab', { name: 'API (OpenAPI)' }))
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Pets')
    await userEvent.type(within(dialog).getByLabelText("Or the document's address"), 'https://pets.example.test/openapi.json')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Read it' }))
    expect(await within(dialog).findByText('2 operations at https://pets.example.test/v1')).toBeInTheDocument()
    expect(within(dialog).getAllByText('asks first')).toHaveLength(1)
    await userEvent.click(within(dialog).getByRole('button', { name: 'Add API' }))
    await waitFor(() =>
      expect(calls.find((c) => c.method === 'POST' && c.path === '/api/admin/tools/servers')?.body).toMatchObject({ name: 'Pets', url: '', spec: null, specUrl: 'https://pets.example.test/openapi.json' }),
    )
  })
})
