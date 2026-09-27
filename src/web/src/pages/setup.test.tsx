import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { member, fakeApi, renderApp } from '@/test/utils'

const config = (over: object = {}) => ({
  model: 'Big-Model',
  models: [
    { name: 'Big-Model', context: 262144, maxOutput: 32768, vision: false, tools: true, thinking: true, loaded: true },
    { name: 'Small-Model', context: 40960, maxOutput: 20480, vision: false, tools: true, thinking: true, loaded: false },
  ],
  argus: false,
  gitlabUrl: null,
  presets: [],
  tools: [],
  ...over,
})

describe('connect your tools', () => {
  it('gives a person their key, the address, their models and setups to paste', async () => {
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config() }),
      'GET /api/account/connect': () => ({ json: { certificate: true } }),
    })
    renderApp('/setup')
    expect(await screen.findByRole('heading', { name: 'Connect your tools' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /New key/ })).toBeInTheDocument()
    const gateway = `${window.location.protocol}//gateway.${window.location.host}`
    expect(await screen.findByText(`${gateway}/v1`)).toBeInTheDocument()
    expect(screen.getAllByText('Big-Model').length).toBeGreaterThan(0)
    expect(screen.getByText('Not loaded now')).toBeInTheDocument()
    // Claude Code first: the gateway speaks Anthropic's API, with the key from the environment.
    expect(screen.getByText(/export ANTHROPIC_BASE_URL=/)).toHaveTextContent(`ANTHROPIC_BASE_URL=${gateway}`)
    expect(screen.getByText(/export ANTHROPIC_BASE_URL=/)).toHaveTextContent('ANTHROPIC_AUTH_TOKEN="$LLM_SERVICE_API_KEY"')
    await userEvent.click(screen.getByRole('tab', { name: 'Qwen Code' }))
    expect(screen.getByText(/export OPENAI_BASE_URL=/)).toHaveTextContent('OPENAI_MODEL=Big-Model')
    // Argus is not offered to someone who may not use it; the certificate is there to download.
    expect(screen.queryByText('Argus, the code index')).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: /The certificate/ })).toHaveAttribute('href', '/api/account/certificate')
    expect(screen.getByRole('link', { name: /With the public CAs/ })).toHaveAttribute('href', '/api/account/certificate?bundle=true')
  })

  it('offers Argus over MCP, with a link to make a GitLab token', async () => {
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config({ argus: true, gitlabUrl: 'https://gitlab.example.com' }) }),
      'GET /api/account/connect': () => ({ json: { certificate: false } }),
    })
    renderApp('/setup')
    const argus = (await screen.findByText('Argus, the code index')).closest('section')!
    expect(within(argus).getByRole('link', { name: /Make a GitLab token/ })).toHaveAttribute('href', 'https://gitlab.example.com/-/user_settings/personal_access_tokens?name=Argus&scopes=read_api')
    expect(within(argus).getByText(/claude mcp add --transport http argus/)).toHaveTextContent(`${window.location.protocol}//argus.${window.location.host}/mcp`)
    expect(screen.queryByRole('link', { name: /The certificate/ })).not.toBeInTheDocument()
  })
})
