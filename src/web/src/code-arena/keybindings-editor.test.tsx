import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, onTestFinished, vi } from 'vitest'
import { makeQueryClient, Providers } from '@/app/providers'
import { fakeApi } from '@/test/utils'
import { register } from './commands'
import { EditorContext, type EditorApi } from './editor-state'
import KeybindingsEditor from './keybindings-editor'
import { keymap } from './keyboard'

/** The Keyboard Shortcuts editor alone, over the person's keybindings.json as the server has it. */
function renderShortcuts(keybindings: string) {
  const files: Record<string, string> = { 'keybindings.json': keybindings }
  fakeApi(null, {
    'GET /api/user-files': (_, __, url) => {
      const name = url.searchParams.get('name')!
      return { json: { name, text: files[name] ?? '', version: 'u1', exists: true } }
    },
    'POST /api/user-files': (body) => {
      const { name, text } = body as { name: string; text: string }
      files[name] = text
      return { json: { name, version: 'u2' } }
    },
  })
  const client = makeQueryClient()
  client.setDefaultOptions({ queries: { ...client.getDefaultOptions().queries, retry: false } })
  const ed = { pageQuery: '', open: vi.fn(), refresh: vi.fn(async () => {}) }
  render(
    <Providers client={client}>
      <EditorContext value={ed as unknown as EditorApi}>
        <KeybindingsEditor />
      </EditorContext>
    </Providers>,
  )
  return { files, ed }
}

describe('the Keyboard Shortcuts editor', () => {
  it('has an open keybindings.json tab show a key defined here, its commented-out rules kept', async () => {
    const { files, ed } = renderShortcuts('[\n    // { "key": "ctrl+b", "command": "x" }\n]')
    const page = await screen.findByRole('region', { name: 'Keyboard Shortcuts' })
    await userEvent.type(within(page).getByRole('searchbox', { name: 'Search keybindings' }), 'togglePanel')
    await userEvent.dblClick(await within(page).findByRole('button', { name: /^workbench\.action\.togglePanel: Ctrl\+J/ }))
    await screen.findByRole('dialog', { name: /Keys for workbench\.action\.togglePanel/ })
    await userEvent.keyboard('{Control>}{Alt>}j{/Alt}{/Control}')
    await userEvent.keyboard('{Enter}')
    await waitFor(() => expect(ed.refresh).toHaveBeenCalledWith('user:keybindings.json'))
    expect(files['keybindings.json']).toBe(
      '[\n    // { "key": "ctrl+b", "command": "x" }\n    {\n        "key": "ctrl+j",\n        "command": "-workbench.action.togglePanel"\n    },\n    {\n        "key": "ctrl+alt+j",\n        "command": "workbench.action.togglePanel"\n    }\n]',
    )
  })

  it('lists no row for a "-command" rule: the default it takes away is gone from the list', async () => {
    onTestFinished(register({ id: 'workbench.action.togglePanel', category: 'View', title: 'Toggle Panel Visibility', run: () => undefined }))
    renderShortcuts('[]')
    act(() => keymap.setUser([{ key: 'ctrl+j', command: '-workbench.action.togglePanel' }]))
    onTestFinished(() => keymap.setUser([]))
    const page = await screen.findByRole('region', { name: 'Keyboard Shortcuts' })
    await userEvent.type(within(page).getByRole('searchbox', { name: 'Search keybindings' }), 'togglePanel')
    expect(await within(page).findByRole('button', { name: 'View: Toggle Panel Visibility: no key. Double-click to change it' })).toBeInTheDocument()
    expect(within(page).queryAllByText('-workbench.action.togglePanel')).toHaveLength(0)
  })
})
