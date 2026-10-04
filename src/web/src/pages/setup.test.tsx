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

  it('offers Argus over MCP with the API key, not a GitLab token', async () => {
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config({ argus: true, gitlabUrl: 'https://gitlab.example.com' }) }),
    })
    renderApp('/setup')
    const argus = (await screen.findByText('Argus, the code index')).closest('section')!
    expect(within(argus).getByText('your API key', { selector: 'strong' })).toBeInTheDocument()
    expect(within(argus).queryByRole('link', { name: /GitLab token/ })).not.toBeInTheDocument()
    expect(within(argus).getByText(/"Authorization": "Bearer <your API key>"/)).toBeInTheDocument()
    // The chosen tool's steps carry Argus too, with this deployment's address and the key from the environment.
    const steps = screen.getByRole('region', { name: 'Setting up Claude Code' })
    const add = within(steps).getByText(/claude mcp add --transport http argus/)
    expect(add).toHaveTextContent(`${window.location.protocol}//argus.${window.location.host}/mcp`)
    expect(add).toHaveTextContent('--header "Authorization: Bearer $LLM_SERVICE_API_KEY"')
    expect(screen.queryByText(/GITLAB_TOKEN/)).not.toBeInTheDocument()
  })

  it('fills a key just made into the Argus setups, only when asked', async () => {
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config({ argus: true }) }),
      'POST /api/account/keys/rotate': () => ({ json: { apiKey: 'sk-just-made-key' } }),
    })
    renderApp('/setup')
    const argus = (await screen.findByText('Argus, the code index')).closest('section')!
    expect(screen.queryByRole('switch')).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: /New key/ }))
    await userEvent.click(within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Make a new key' }))
    const fill = await screen.findByRole('switch', { name: /Fill in my new key/ })
    // Still hidden until asked.
    expect(screen.queryByText(/sk-just-made-key/)).not.toBeInTheDocument()
    await userEvent.click(fill)
    expect(within(argus).getByText(/"Authorization": "Bearer sk-just-made-key"/)).toBeInTheDocument()
    // A setup that reads the environment keeps reading it; one that cannot gets the key.
    expect(screen.getByText(/claude mcp add --transport http argus/)).toHaveTextContent('$LLM_SERVICE_API_KEY')
    await userEvent.click(screen.getByRole('combobox', { name: 'Your tool' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Continue' }))
    expect(within(screen.getByRole('region', { name: 'Setting up Continue' })).getByText(/mcpServers:/)).toHaveTextContent('Authorization: Bearer sk-just-made-key')
    await userEvent.click(screen.getByRole('switch', { name: /Fill in my new key/ }))
    expect(screen.queryByText(/sk-just-made-key/)).not.toBeInTheDocument()
  })

  it('offers Arena MCP with the tools it serves and setups for Claude Code, Qwen Code and other clients', async () => {
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config() }),
      'GET /api/account/mcp': () => ({
        json: {
          enabled: true,
          url: 'https://llm.example.com/mcp',
          tools: [
            { id: 'argus', title: 'Argus', askFirst: false },
            { id: 'mcp:1', title: 'Jira', askFirst: true },
          ],
        },
      }),
    })
    renderApp('/setup')
    expect(await screen.findByText('Arena MCP (all your tools)')).toBeInTheDocument()
    expect(screen.getByText('https://llm.example.com/mcp')).toBeInTheDocument()
    const served = screen.getByRole('list', { name: 'Tools it serves you' })
    expect(within(served).getByText('Argus')).toBeInTheDocument()
    expect(within(served).getByText('Jira · asks first')).toBeInTheDocument()
    expect(screen.getByText(/let it ask, rather than trusting every tool/)).toBeInTheDocument()
    // Each with the person's key from the environment, never --trust (asking first is the client's job here).
    const claude = screen.getByRole('region', { name: 'Arena MCP in Claude Code' })
    expect(within(claude).getByText(/claude mcp add/)).toHaveTextContent(
      'claude mcp add --transport http arena https://llm.example.com/mcp \\ --header "Authorization: Bearer $LLM_SERVICE_API_KEY"',
    )
    const qwen = screen.getByRole('region', { name: 'Arena MCP in Qwen Code' })
    expect(within(qwen).getByText(/qwen mcp add/)).toHaveTextContent('qwen mcp add arena https://llm.example.com/mcp -t http \\ -H "Authorization: Bearer $LLM_SERVICE_API_KEY"')
    expect(within(qwen).getByText(/qwen mcp add/)).not.toHaveTextContent('--trust')
    const other = JSON.parse(within(screen.getByRole('region', { name: 'Arena MCP in Other MCP clients' })).getByText(/mcpServers/).textContent!)
    expect(other.mcpServers.arena).toEqual({ type: 'http', url: 'https://llm.example.com/mcp', headers: { Authorization: 'Bearer <your API key>' } })
  })

  it('leaves Arena MCP out when an admin turned it off', async () => {
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config() }),
      'GET /api/account/mcp': () => ({ json: { enabled: false, url: 'https://llm.example.com/mcp', tools: [] } }),
    })
    renderApp('/setup')
    expect(await screen.findByRole('heading', { name: 'Connect your tools' })).toBeInTheDocument()
    expect(await screen.findByText(/export ANTHROPIC_BASE_URL=/)).toBeInTheDocument()
    expect(screen.queryByText('Arena MCP (all your tools)')).not.toBeInTheDocument()
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
    expect(within(dsh).getByText(/- insert:/)).toHaveTextContent('process.env.LLM_SERVICE_API_KEY')
    // Codex: the Responses API. Aider: no MCP, and it says so.
    const codex = await pick('Codex CLI')
    expect(within(codex).getAllByText(/wire_api = "responses"/)[0]).toBeInTheDocument()
    const aider = await pick('Aider')
    expect(within(aider).getByText(/aider --model openai\/Big-Model/)).toBeInTheDocument()
    expect(within(aider).getByText(/Aider has no MCP/)).toBeInTheDocument()
  })

  it('GitLab CI: the variables to set, the template to include, and the CLI in a terminal', async () => {
    fakeApi(member, {
      'GET /api/chat/config': () => ({ json: config() }),
    })
    renderApp('/setup')
    const root = `${window.location.protocol}//gateway.${window.location.host}`
    await userEvent.click(await screen.findByRole('combobox', { name: 'Your tool' }))
    expect(screen.getByText('Pipelines')).toBeInTheDocument()
    await userEvent.click(await screen.findByRole('option', { name: 'GitLab CI' }))
    const ci = screen.getByRole('region', { name: 'Setting up GitLab CI' })
    // The gateway's address for ARENA_URL, without /v1 (the CLI adds it).
    expect(within(ci).getByText('ARENA_URL', { selector: 'p' })).toBeInTheDocument()
    expect(within(ci).getByText(root)).toBeInTheDocument()
    expect(within(ci).getByText(/ARENA_GITLAB_TOKEN \(a project access token, role Reporter, scope api/)).toBeInTheDocument()
    const include = within(ci).getByText(/include:/)
    expect(include).toHaveTextContent('file: clients/gitlab-ci/arena-review.yml')
    expect(include).toHaveTextContent('ARENA_CLI: ci/arena')
    expect(include).toHaveTextContent('ARENA_MODEL: Big-Model')
    expect(within(ci).getByText(/export ARENA_URL=/)).toHaveTextContent(`ARENA_URL=${root} export ARENA_KEY="$LLM_SERVICE_API_KEY"`)
  })
})
