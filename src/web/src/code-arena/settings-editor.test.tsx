import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, onTestFinished, vi } from 'vitest'
import { makeQueryClient, Providers } from '@/app/providers'
import { fakeApi } from '@/test/utils'
import { EditorContext, type EditorApi } from './editor-state'
import { SettingsProvider } from './settings'
import SettingsEditor from './settings-editor'

/** The Settings editor alone, over the person's settings.json and the project's (null: none), as the server has them. */
function renderSettings(user: string, workspace: string | null) {
  const files: Record<string, string> = { 'settings.json': user }
  // Code Arena's options on this computer, as config.json and the environment have them.
  const options = [
    { variable: 'CODE_ARENA_BROWSER', key: 'browser', setting: 'codeArena.browser', kind: 'file', description: 'The browser the IDE\'s own window uses.', value: null as string | null, environment: null, file: '/home/ada/.config/code-arena/config.json' },
    { variable: 'CODE_ARENA_VIRSH', key: 'virsh', setting: 'codeArena.vm.virsh', kind: 'file', description: "libvirt's virsh, for the VM tool.", value: null, environment: '/usr/bin/virsh', file: '/home/ada/.config/code-arena/config.json' },
  ]
  const saved: { setting: string; value: string | null }[] = []
  fakeApi(null, {
    'GET /api/options': () => ({ json: { options } }),
    'POST /api/options': (body) => {
      const { setting, value } = body as { setting: string; value: string | null }
      saved.push({ setting, value })
      if (value === '/nope') return { status: 400, json: { status: 'invalid', error: 'There is no file /nope.' } }
      options.find((o) => o.setting === setting)!.value = value
      return { json: { options } }
    },
    'GET /api/user-files': (_, __, url) => {
      const name = url.searchParams.get('name')!
      return { json: { name, text: files[name] ?? '', version: 'u1', exists: true } }
    },
    'POST /api/user-files': (body) => {
      const { name, text } = body as { name: string; text: string }
      files[name] = text
      return { json: { name, version: 'u2' } }
    },
    'GET /api/file': () => (workspace === null ? { status: 404, json: { status: 'not_found' } } : { json: { path: '.vscode/settings.json', size: workspace.length, text: workspace, version: 'w1' } }),
    'GET /api/user-files/vscode': () => ({ json: { name: 'settings.json', text: '{ "editor.fontSize": 20 }', from: 'Code/User/settings.json' } }),
  })
  const client = makeQueryClient()
  client.setDefaultOptions({ queries: { ...client.getDefaultOptions().queries, retry: false } })
  const ed = { pageQuery: '', open: vi.fn(), refresh: vi.fn(async () => {}) } as unknown as EditorApi
  render(
    <Providers client={client}>
      <SettingsProvider>
        <EditorContext value={ed}>
          <SettingsEditor />
        </EditorContext>
      </SettingsProvider>
    </Providers>,
  )
  return { files, saved }
}

describe('the Settings editor', () => {
  it("shows Code Arena's options on this computer in the User tab, keeps a change in config.json and says when the environment wins", async () => {
    const { saved, files } = renderSettings('{}\n', null)
    const page = await screen.findByRole('region', { name: 'Settings' })
    await userEvent.type(within(page).getByRole('searchbox', { name: 'Search settings' }), 'browser window')
    expect(await within(page).findByRole('heading', { name: 'Code Arena on this computer' })).toBeInTheDocument()
    const box = within(page).getByLabelText(/Browser/)
    await userEvent.type(box, '/opt/chrome/chrome{Enter}')
    await waitFor(() => expect(saved).toEqual([{ setting: 'codeArena.browser', value: '/opt/chrome/chrome' }]))
    // Not settings.json's: Code Arena reads it itself.
    expect(files['settings.json']).toBe('{}\n')
    expect(within(page).getByText(/Kept in \/home\/ada\/\.config\/code-arena\/config\.json/)).toBeInTheDocument()

    await userEvent.clear(within(page).getByRole('searchbox', { name: 'Search settings' }))
    await userEvent.type(within(page).getByRole('searchbox', { name: 'Search settings' }), '@id:codeArena.vm.virsh')
    expect(await within(page).findByText('CODE_ARENA_VIRSH is set in the environment and wins: /usr/bin/virsh')).toBeInTheDocument()

    // The project's settings cannot name a program on this computer.
    await userEvent.click(within(page).getByRole('tab', { name: 'Workspace' }))
    expect(within(page).queryByRole('heading', { name: 'Code Arena on this computer' })).toBeNull()
  })

  it("shows the chosen file's own value, so a change in the User tab is seen though the project's sets the setting too", async () => {
    const { files } = renderSettings('{}\n', '{\n  "editor.formatOnSave": true,\n  "editor.tabSize": 3\n}\n')
    const page = await screen.findByRole('region', { name: 'Settings' })
    await userEvent.type(within(page).getByRole('searchbox', { name: 'Search settings' }), '@id:editor.formatOnSave,editor.tabSize')
    expect(await within(page).findAllByText('Also modified in: Workspace')).toHaveLength(2)
    // User: the person's (none: the defaults), not the project's over them.
    const box = within(page).getByRole('checkbox', { name: /Formats a file as you save it/ })
    expect(box).not.toBeChecked()
    expect(within(page).getByLabelText(/Tab Size/)).toHaveValue(4)
    await userEvent.click(box)
    await waitFor(() => expect(files['settings.json']).toBe('{\n    "editor.formatOnSave": true\n}\n'))
    await waitFor(() => expect(within(page).getByRole('checkbox', { name: /Formats a file as you save it/ })).toBeChecked())
    // Workspace: the project's.
    await userEvent.click(within(page).getByRole('tab', { name: 'Workspace' }))
    expect(within(page).getByLabelText(/Tab Size/)).toHaveValue(3)
    expect(within(page).getByRole('checkbox', { name: /Formats a file as you save it/ })).toBeChecked()
  })

  it("has an open settings.json tab show what From VS Code brought over, the file's comments kept", async () => {
    const { files } = renderSettings('{\n  // "editor.fontSize": 16\n}\n', null)
    const written = vi.fn()
    window.addEventListener('code-arena:settings-written', written)
    onTestFinished(() => window.removeEventListener('code-arena:settings-written', written))
    const page = await screen.findByRole('region', { name: 'Settings' })
    await userEvent.click(within(page).getByRole('button', { name: /From VS Code/ }))
    await userEvent.click(await screen.findByRole('button', { name: 'Bring them over' }))
    await waitFor(() => expect(written).toHaveBeenCalledTimes(1))
    expect((written.mock.calls[0]![0] as CustomEvent<string>).detail).toBe('user')
    expect(files['settings.json']).toBe('{\n  // "editor.fontSize": 16\n  "editor.fontSize": 20\n}\n')
  })
})
