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
    await userEvent.click(screen.getByRole('combobox', { name: 'Your tool' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Qwen Code' }))
    expect(screen.getByText(/export OPENAI_BASE_URL=/)).toHaveTextContent('OPENAI_MODEL=Big-Model')
    // Argus is not offered to someone who may not use it.
    expect(screen.queryByText('Argus, the code index')).not.toBeInTheDocument()
  })

  it('offers Argus over MCP, with a link to make a GitLab token', async () => {
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config({ argus: true, gitlabUrl: 'https://gitlab.example.com' }) }),
    })
    renderApp('/setup')
    const argus = (await screen.findByText('Argus, the code index')).closest('section')!
    expect(within(argus).getByRole('link', { name: /Make a GitLab token/ })).toHaveAttribute('href', 'https://gitlab.example.com/-/user_settings/personal_access_tokens?name=Argus&scopes=read_api')
    // The chosen tool's steps carry Argus too, with this deployment's address.
    const steps = screen.getByRole('region', { name: 'Setting up Claude Code' })
    expect(within(steps).getByText(/claude mcp add --transport http argus/)).toHaveTextContent(`${window.location.protocol}//argus.${window.location.host}/mcp`)
  })

  it('offers Arena Code to download for each system it was built for, with the login and the CA note', async () => {
    const build = (rid: string, system: string, fileName = 'arena-code') => ({ rid, system, fileName, size: 38 * 1024 * 1024, sha256: 'abc' })
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config({ argus: true }) }),
      'GET /api/downloads/arena-code': () => ({
        json: { version: '4.2.0', builds: [build('linux-x64', 'Linux (x64)'), build('osx-arm64', 'macOS (Apple silicon)'), build('win-x64', 'Windows (x64)', 'arena-code.exe')] },
      }),
    })
    renderApp('/setup')
    await userEvent.click(await screen.findByRole('combobox', { name: 'Your tool' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Arena Code (our own agent)' }))
    const steps = screen.getByRole('region', { name: 'Setting up Arena Code (our own agent)' })
    expect(within(steps).getByText(/version 4.2.0/)).toBeInTheDocument()
    expect(within(steps).getByRole('link', { name: /Linux \(x64\)/ })).toHaveAttribute('href', '/api/downloads/arena-code/linux-x64')
    expect(within(steps).getByRole('link', { name: /Windows \(x64\)/ })).toHaveAttribute('href', '/api/downloads/arena-code/win-x64')
    expect(within(steps).getByRole('link', { name: 'Download for macOS (Apple silicon), 38 MB' })).toHaveAttribute('download')
    expect(within(steps).queryByRole('link', { name: /Linux \(ARM64\)/ })).not.toBeInTheDocument()
    // Signing in names this Arena; the CA note gives the private CA's file.
    expect(within(steps).getByText(`arena-code login --url ${window.location.origin}`)).toBeInTheDocument()
    expect(within(steps).getByText(`arena-code login --url ${window.location.origin} --ca ca.crt`)).toBeInTheDocument()
    expect(within(steps).getByText(/ARENA_CA_CERT/)).toBeInTheDocument()
    // Argus comes through Arena's own tools: no GitLab token to add.
    expect(within(steps).getByText(/reaches Argus through Arena's own tools/)).toBeInTheDocument()
  })

  it('says how to add Arena Code when this Arena has no builds of it', async () => {
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config() }),
      'GET /api/downloads/arena-code': () => ({ json: { version: '4.2.0', builds: [] } }),
    })
    renderApp('/setup')
    await userEvent.click(await screen.findByRole('combobox', { name: 'Your tool' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Arena Code (our own agent)' }))
    const steps = screen.getByRole('region', { name: 'Setting up Arena Code (our own agent)' })
    expect(within(steps).getByText(/no builds of it yet/)).toHaveTextContent('tools/publish-arena-code.sh --offline')
    expect(within(steps).queryByRole('link')).not.toBeInTheDocument()
  })

  it('each tool has its own steps, filled in with the address, the model and its limits', async () => {
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config({ argus: true, gitlabUrl: null }) }),
    })
    renderApp('/setup')
    const base = `${window.location.protocol}//gateway.${window.location.host}/v1`
    const pick = async (name: string) => {
      await userEvent.click(await screen.findByRole('combobox', { name: 'Your tool' }))
      await userEvent.click(await screen.findByRole('option', { name }))
      return screen.getByRole('region', { name: `Setting up ${name}` })
    }
    // Hermes: its config file, the model with its window, and Argus over MCP.
    const hermes = await pick('Hermes')
    expect(within(hermes).getAllByText('~/.config/hermes/config.yaml', { selector: 'p' })).toHaveLength(2)
    expect(within(hermes).getAllByText(/base_url:/)[0]).toHaveTextContent(`base_url: ${base} api_key: <your API key> name: Big-Model context_length: 262144`)
    expect(within(hermes).getByText(/mcp_servers:/)).toBeInTheDocument()
    // OpenClaw: a provider and the default model, the key from the environment.
    const openclaw = await pick('OpenClaw')
    const claw = JSON.parse(within(openclaw).getAllByText(/"providers"/)[0]!.textContent!)
    expect(claw.agents.defaults.model.primary).toBe('llm-service/Big-Model')
    expect(claw.models.providers['llm-service']).toMatchObject({ baseUrl: base, apiKey: '${LLM_SERVICE_API_KEY}', api: 'openai-completions' })
    expect(claw.models.providers['llm-service'].models[0]).toMatchObject({ id: 'Big-Model', contextWindow: 262144, maxTokens: 32768 })
    // DeepSeek Harness: the profile patch, and Argus appended to it.
    const dsh = await pick('DeepSeek Harness')
    expect(within(dsh).getAllByText(/apiKeyEnv: LLM_SERVICE_API_KEY/)[0]).toHaveTextContent(`baseURL: ${base}`)
    expect(within(dsh).getByText(/- insert:/)).toHaveTextContent('process.env.GITLAB_TOKEN')
    // Codex: the Responses API. Aider: no MCP, and it says so.
    const codex = await pick('Codex CLI')
    expect(within(codex).getAllByText(/wire_api = "responses"/)[0]).toBeInTheDocument()
    const aider = await pick('Aider')
    expect(within(aider).getByText(/aider --model openai\/Big-Model/)).toBeInTheDocument()
    expect(within(aider).getByText(/Aider has no MCP/)).toBeInTheDocument()
  })
})
