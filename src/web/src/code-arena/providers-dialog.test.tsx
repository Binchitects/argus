import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { TooltipProvider } from '@/components/ui/tooltip'
import { fakeApi, member } from '@/test/utils'
import { ProvidersDialog, type ProvidersAnswer } from './providers-dialog'

const known: ProvidersAnswer['known'] = [
  { name: 'anthropic', kind: 'anthropic', url: 'https://api.anthropic.com', keyEnv: 'ANTHROPIC_API_KEY', about: 'Anthropic: Claude', keyInEnv: false },
  { name: 'deepseek', kind: 'openai', url: 'https://api.deepseek.com', keyEnv: 'DEEPSEEK_API_KEY', about: 'DeepSeek', keyInEnv: true },
]

function show() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={client}>
      <TooltipProvider>
        <ProvidersDialog onClose={() => {}} />
      </TooltipProvider>
    </QueryClientProvider>,
  )
}

describe('Other providers', () => {
  it('adds Anthropic with a key, lists its models and never shows the key again; removes one', async () => {
    const added: ProvidersAnswer = {
      allowed: true,
      known,
      providers: [{ name: 'anthropic', kind: 'anthropic', url: 'https://api.anthropic.com', key: 'config', keyEnv: 'ANTHROPIC_API_KEY', models: ['anthropic:claude-x'] }],
      said: 'anthropic is kept: 1 model (anthropic:claude-x).',
    }
    const calls = fakeApi(member, {
      'GET /api/providers': () => ({ json: { allowed: true, known, providers: [] } }),
      'POST /api/providers': () => ({ json: added }),
      'POST /api/providers/remove': () => ({ json: { allowed: true, known, providers: [] } }),
    })
    show()
    const dialog = await screen.findByRole('dialog', { name: 'Other providers' })
    const form = await within(dialog).findByRole('form', { name: 'Add a provider' })
    await userEvent.type(within(form).getByLabelText('API key'), 'sk-ant-test')
    await userEvent.click(within(form).getByRole('button', { name: 'Add and list its models' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.path === '/api/providers')?.body).toEqual({ name: 'anthropic', key: 'sk-ant-test' }))
    const list = await within(dialog).findByRole('list', { name: 'Your providers' })
    expect(within(list).getByText(/1 model: anthropic:claude-x/)).toBeInTheDocument()
    expect(within(list).getByText(/key kept here/)).toBeInTheDocument()
    expect(dialog.textContent).not.toContain('sk-ant-test')
    expect(within(form).getByLabelText('API key')).toHaveValue('')

    await userEvent.click(within(list).getByRole('button', { name: 'Remove anthropic' }))
    await waitFor(() => expect(calls.find((c) => c.path === '/api/providers/remove')?.body).toEqual({ name: 'anthropic' }))
  })

  it('adds an OpenAI-compatible API by its address, its key from the environment when there', async () => {
    const calls = fakeApi(member, {
      'GET /api/providers': () => ({ json: { allowed: true, known, providers: [] } }),
      'POST /api/providers': () => ({ json: { allowed: true, known, providers: [] } }),
    })
    show()
    const form = await screen.findByRole('form', { name: 'Add a provider' })
    await userEvent.selectOptions(within(form).getByLabelText('Provider'), '')
    await userEvent.type(within(form).getByLabelText(/Name/), 'local')
    await userEvent.type(within(form).getByLabelText('Address'), 'http://gpu:8000')
    await userEvent.click(within(form).getByRole('button', { name: 'Add and list its models' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ name: 'local', url: 'http://gpu:8000', kind: 'openai' }))

    await userEvent.selectOptions(within(form).getByLabelText('Provider'), 'deepseek')
    expect(within(form).getByLabelText('API key')).toHaveAttribute('placeholder', 'From DEEPSEEK_API_KEY (leave empty)')
  })

  it('says when Arena’s admins do not let Code Arena use other providers, and offers no form', async () => {
    fakeApi(member, { 'GET /api/providers': () => ({ json: { allowed: false, known, providers: [] } }) })
    show()
    expect(await screen.findByRole('status')).toHaveTextContent(/admins do not let Code Arena use other providers/)
    expect(screen.queryByRole('form', { name: 'Add a provider' })).not.toBeInTheDocument()
  })
})
