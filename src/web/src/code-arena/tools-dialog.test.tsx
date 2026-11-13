import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { TooltipProvider } from '@/components/ui/tooltip'
import { fakeApi, member } from '@/test/utils'
import { ToolsDialog, type ToolEntry, type ToolSettingsList } from './tools-dialog'

const tool = (over: Partial<ToolEntry>): ToolEntry => ({
  name: 't', group: null, groupTitle: 'Code Arena', server: null, kind: 'read', origin: null, description: '', arguments: [], enabled: true, asks: 'never',
  person: {}, here: {}, project: {}, allowLocked: null, ...over,
})

const listed = (): ToolSettingsList => ({
  tools: [
    tool({ name: 'run_shell', kind: 'shell', asks: 'mode', description: 'Run a shell command', arguments: [{ name: 'command', type: 'string', description: null }, { name: 'timeout_seconds', type: 'integer', description: null }] }),
    tool({ name: 'write_file', kind: 'edit', asks: null, enabled: false, project: { enabled: false } }),
    tool({ name: 'web_search', groupTitle: 'Web search', server: 'arena', kind: 'remote', asks: 'mode', allowLocked: 'Arena’s admins set it to ask before each call.' }),
    tool({ name: 'greet', groupTitle: 'Your own and the project’s', origin: '~/.code-arena/tools', description: 'Greets someone' }),
  ],
  own: [{ key: 'tool:deploy', name: 'deploy', description: 'Deploys a preview', source: 'project', origin: '.arena/tools', command: './deploy.sh', kind: 'shell', enabled: false, file: '.arena/tools/deploy.json' }],
  servers: [{ key: 'mcp:tickets', name: 'tickets', command: 'tickets-mcp --stdio', enabled: false, connected: false, file: '.mcp.json' }],
  projectFile: '.arena/settings.json',
})

function show() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={client}>
      <TooltipProvider>
        <ToolsDialog open onOpenChange={() => {}} />
      </TooltipProvider>
    </QueryClientProvider>,
  )
}

describe('Configure Tools', () => {
  it('turns a tool off, makes one ask beside its switch, fixes an argument under Advanced, and enables the project’s own for this project', async () => {
    const calls = fakeApi(member, {
      'GET /api/tools/settings': () => ({ json: listed() }),
      'POST /api/tools/settings': () => ({ json: listed() }),
      'POST /api/tools/enable': () => ({ json: { ...listed(), said: 'deploy is enabled for this project.' } }),
    })
    show()
    const dialog = await screen.findByRole('dialog', { name: 'Configure Tools' })
    const own = await within(dialog).findByRole('region', { name: 'The project’s own' })
    expect(within(own).getByText('./deploy.sh')).toBeInTheDocument()
    expect(within(own).getByText(/MCP server, each call asks/)).toBeInTheDocument()
    await userEvent.click(within(own).getByRole('switch', { name: 'Enable deploy for this project' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/tools/enable')?.body).toEqual({ key: 'tool:deploy', on: true }))

    // Off by the project: the person cannot turn it on. A group folds, and says how many of its are on.
    expect(within(dialog).getByRole('button', { name: /Code Arena\s*1 of 2 on/ })).toHaveAttribute('aria-expanded', 'true')
    const write = within(dialog).getByRole('listitem', { name: 'write_file' })
    expect(within(write).getByRole('switch', { name: 'write_file on' })).toBeDisabled()
    expect(within(write).getByText('Turned off by the project (.arena/settings.json).')).toBeInTheDocument()

    const shell = within(dialog).getByRole('listitem', { name: 'run_shell' })
    await userEvent.click(within(shell).getByRole('switch', { name: 'run_shell on' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/tools/settings')?.body).toEqual({ name: 'run_shell', level: 'person', settings: { enabled: false } }))

    // For this project only: asks each call (beside its switch), its timeout fixed and its words under Advanced.
    await userEvent.click(within(dialog).getByRole('switch', { name: 'Only in this project' }))
    await userEvent.selectOptions(within(shell).getByRole('combobox', { name: 'How run_shell asks' }), 'ask')
    await waitFor(() =>
      expect(calls.filter((c) => c.method === 'POST' && c.path === '/api/tools/settings').at(-1)?.body).toEqual({ name: 'run_shell', level: 'here', settings: { approval: 'ask' } }),
    )
    await userEvent.click(within(shell).getByRole('button', { name: 'Advanced settings of run_shell' }))
    const form = within(shell).getByRole('form', { name: 'Advanced settings of run_shell' })
    await userEvent.selectOptions(within(form).getByRole('combobox', { name: 'How timeout_seconds is given' }), 'fixed')
    await userEvent.type(within(form).getByRole('textbox', { name: 'The value of timeout_seconds' }), '300')
    await userEvent.type(within(form).getByRole('textbox', { name: 'What the model is told of it' }), 'Run make targets only.')
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }))
    await waitFor(() =>
      expect(calls.filter((c) => c.method === 'POST' && c.path === '/api/tools/settings').at(-1)?.body).toEqual({
        name: 'run_shell', level: 'here', settings: { description: 'Run make targets only.', fixed: { timeout_seconds: 300 } },
      }),
    )
  })

  it('never offers to let one of Arena’s that asks first run unasked', async () => {
    fakeApi(member, { 'GET /api/tools/settings': () => ({ json: listed() }) })
    show()
    const web = await screen.findByRole('listitem', { name: 'web_search' })
    expect(within(web).getByRole('option', { name: 'Never asks' })).toBeDisabled()
    expect(within(web).getByText('Arena’s admins set it to ask before each call.')).toBeInTheDocument()
    await userEvent.type(screen.getByRole('textbox', { name: 'Find a tool' }), 'greet')
    expect(screen.queryByRole('listitem', { name: 'web_search' })).toBeNull()
    expect(screen.getByRole('listitem', { name: 'greet' })).toBeInTheDocument()
  })
})
