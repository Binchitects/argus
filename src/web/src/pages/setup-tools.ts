/**
 * How each tool connects: where its settings live, what to put there, how to
 * start it, and Argus over MCP where the tool speaks it. Filled in with this
 * deployment's addresses and the chosen model. Each follows the tool's own
 * documentation (the formats change: check its docs when one stops working).
 */

/** Where the API key is kept in each setup: an environment variable, not pasted into a file. */
export const KEY = 'LLM_SERVICE_API_KEY'
/** And the person's GitLab token, for Argus. */
export const GITLAB = 'GITLAB_TOKEN'

/** A build of Arena Code the app serves (/api/downloads/arena-code). */
export interface ArenaCodeBuild {
  rid: string
  system: string
  fileName: string
  size: number
  sha256: string | null
}

export interface Context {
  /** The gateway without /v1 (Anthropic's API lives there). */
  root: string
  base: string
  model: string
  context: number
  maxOutput: number
  argusUrl: string
  /** This Arena's own address, https://DOMAIN. */
  origin: string
  /** Arena Code's builds; null when none could be listed. */
  arenaCode: { version: string; builds: ArenaCodeBuild[] } | null
}

export interface Step {
  text: string
  code?: string
  /** What the code is: a file's path, or "shell". */
  file?: string
  /** Files to download, one button each. */
  links?: { label: string; href: string; detail?: string }[]
}

export interface Tool {
  id: string
  title: string
  group: 'Coding agents' | 'Agents' | 'Editors' | 'Code'
  /** One line: what it is and how it talks to the gateway. */
  about: string
  steps: (c: Context) => Step[]
  /** Argus over MCP, with the person's own GitLab token; a string when the tool cannot. */
  argus: ((c: Context) => Step[]) | string
}

const json = (v: unknown) => JSON.stringify(v, null, 2)

const megabytes = (bytes: number) => `${Math.round(bytes / 1024 / 1024)} MB`

export const tools: Tool[] = [
  {
    id: 'arena-code',
    title: 'Arena Code (our own agent)',
    group: 'Coding agents',
    about:
      'Our own coding agent, in your terminal: one file with nothing to install, that talks only to this Arena (the model through the gateway, all of Arena\'s tools as you), with file, shell and git tools on your machine.',
    steps: (c) => {
      const builds = c.arenaCode?.builds ?? []
      return [
        builds.length > 0
          ? {
              text: `Download it for your system (version ${c.arenaCode!.version}):`,
              links: builds.map((b) => ({ label: b.system, href: `/api/downloads/arena-code/${b.rid}`, detail: megabytes(b.size) })),
            }
          : {
              text: 'This Arena has no builds of it yet. An admin adds them once: put .NET\'s runtime packs in tools/offline-nuget (its README says how), then rebuild the app, or run tools/publish-arena-code.sh --offline and hand out the files in dist/arena-code.',
            },
        {
          text: 'Make it runnable and put it on your PATH. On Windows, put arena-code.exe in a folder on your PATH instead.',
          file: 'shell',
          code: `chmod +x arena-code
xattr -d com.apple.quarantine arena-code   # macOS only: the browser's download mark
mkdir -p ~/.local/bin && mv arena-code ~/.local/bin/`,
        },
        {
          text: 'Sign in with this Arena\'s address and your API key. It asks for the key, checks it with the gateway, and keeps it in your config folder, readable by you only.',
          file: 'shell',
          code: `arena-code login --url ${c.origin}`,
        },
        {
          text: "If your Arena's certificate comes from your company's own CA, give that CA's file once (ask your admin for ca.crt), or set ARENA_CA_CERT:",
          file: 'shell',
          code: `arena-code login --url ${c.origin} --ca ca.crt`,
        },
        { text: 'Start it in your project (arena-code -p "…" answers once, for scripts):', file: 'shell', code: 'cd your-project\narena-code' },
      ]
    },
    argus: "Nothing to add: Arena Code reaches Argus through Arena's own tools, as you, with the code you may read.",
  },
  {
    id: 'claude',
    title: 'Claude Code',
    group: 'Coding agents',
    about: "Anthropic's coding agent. The gateway speaks Anthropic's API too, so it runs on these models.",
    steps: (c) => [
      {
        text: 'Point it at the gateway, then start it:',
        file: 'shell',
        code: `export ANTHROPIC_BASE_URL=${c.root}
export ANTHROPIC_AUTH_TOKEN="$${KEY}"
export ANTHROPIC_MODEL=${c.model}
export ANTHROPIC_DEFAULT_HAIKU_MODEL=${c.model}
claude`,
      },
    ],
    argus: (c) => [{ text: 'Add Argus once:', file: 'shell', code: `claude mcp add --transport http argus ${c.argusUrl} \\\n  --header "Authorization: Bearer $${GITLAB}"` }],
  },
  {
    id: 'codex',
    title: 'Codex CLI',
    group: 'Coding agents',
    about: "OpenAI's coding agent. It uses the Responses API, which the gateway serves for these models.",
    steps: (c) => [
      {
        text: 'Add the gateway as a provider and make it the default:',
        file: '~/.codex/config.toml',
        code: `model = "${c.model}"
model_provider = "llm-service"

[model_providers.llm-service]
name = "Argus Arena"
base_url = "${c.base}"
env_key = "${KEY}"
wire_api = "responses"`,
      },
      { text: 'Start it:', file: 'shell', code: 'codex' },
    ],
    argus: (c) => [
      {
        text: 'Add Argus in the same file:',
        file: '~/.codex/config.toml',
        code: `[mcp_servers.argus]
url = "${c.argusUrl}"
bearer_token_env_var = "${GITLAB}"`,
      },
    ],
  },
  {
    id: 'qwen',
    title: 'Qwen Code',
    group: 'Coding agents',
    about: "Qwen's coding agent. It reads the OpenAI variables.",
    steps: (c) => [
      {
        text: 'Point it at the gateway, then start it:',
        file: 'shell',
        code: `export OPENAI_BASE_URL=${c.base}
export OPENAI_API_KEY="$${KEY}"
export OPENAI_MODEL=${c.model}
qwen`,
      },
    ],
    argus: (c) => [{ text: 'Add Argus once (--trust runs its tools without asking each time):', file: 'shell', code: `qwen mcp add argus ${c.argusUrl} -t http \\\n  -H "Authorization: Bearer $${GITLAB}" --trust` }],
  },
  {
    id: 'opencode',
    title: 'OpenCode',
    group: 'Coding agents',
    about: 'An open-source coding agent, through its OpenAI-compatible provider.',
    steps: (c) => [
      {
        text: 'Add the gateway as a provider (in the project, or for every project in ~/.config/opencode/opencode.json):',
        file: 'opencode.json',
        code: json({
          $schema: 'https://opencode.ai/config.json',
          provider: {
            'llm-service': {
              npm: '@ai-sdk/openai-compatible',
              name: 'Argus Arena',
              options: { baseURL: c.base, apiKey: `{env:${KEY}}` },
              models: { [c.model]: { name: c.model, limit: { context: c.context, output: c.maxOutput } } },
            },
          },
          model: `llm-service/${c.model}`,
        }),
      },
      { text: 'Start it:', file: 'shell', code: 'opencode' },
    ],
    argus: (c) => [
      {
        text: 'Add Argus to the same file, beside "provider":',
        file: 'opencode.json',
        code: json({ mcp: { argus: { type: 'remote', url: c.argusUrl, enabled: true, headers: { Authorization: `Bearer {env:${GITLAB}}` } } } }),
      },
    ],
  },
  {
    id: 'aider',
    title: 'Aider',
    group: 'Coding agents',
    about: 'Pair programming in the terminal, through its OpenAI-compatible endpoint.',
    steps: (c) => [
      {
        text: 'Point it at the gateway, then start it with the model (the openai/ prefix says which API):',
        file: 'shell',
        code: `export OPENAI_API_BASE=${c.base}
export OPENAI_API_KEY="$${KEY}"
aider --model openai/${c.model}`,
      },
    ],
    argus: 'Aider has no MCP, so Argus is not available in it.',
  },
  {
    id: 'hermes',
    title: 'Hermes',
    group: 'Agents',
    about: "Nous Research's agent. It needs a context window of at least 64,000 tokens.",
    steps: (c) => [
      {
        text: 'Set the model (on Windows the file is %LOCALAPPDATA%\\hermes\\config.yaml):',
        file: '~/.config/hermes/config.yaml',
        code: `model:
  base_url: ${c.base}
  api_key: <your API key>
  name: ${c.model}
  context_length: ${c.context}`,
      },
      {
        text: 'Hermes remembers each model\'s window the first time it sees it. After the window changes, delete the model\'s line from context_length_cache.yaml beside the config, or it keeps the old number.',
      },
    ],
    argus: (c) => [
      {
        text: 'Add Argus to the same file:',
        file: '~/.config/hermes/config.yaml',
        code: `mcp_servers:
  argus:
    url: ${c.argusUrl}
    headers:
      Authorization: Bearer <your GitLab token>`,
      },
    ],
  },
  {
    id: 'openclaw',
    title: 'OpenClaw',
    group: 'Agents',
    about: 'The personal assistant agent, through its OpenAI-compatible provider.',
    steps: (c) => [
      {
        text: 'Add the gateway as a provider and make the model the default:',
        file: '~/.openclaw/openclaw.json',
        code: json({
          agents: { defaults: { model: { primary: `llm-service/${c.model}` } } },
          models: {
            providers: {
              'llm-service': {
                baseUrl: c.base,
                apiKey: `\${${KEY}}`,
                api: 'openai-completions',
                models: [{ id: c.model, name: c.model, input: ['text'], contextWindow: c.context, maxTokens: c.maxOutput }],
              },
            },
          },
        }),
      },
      { text: 'Restart OpenClaw so it reads the file. The model then answers as llm-service/' + c.model + '.' },
    ],
    argus: (c) => [
      {
        text: 'Add Argus to the same file:',
        file: '~/.openclaw/openclaw.json',
        code: json({ mcp: { servers: { argus: { url: c.argusUrl, transport: 'streamable-http', headers: { Authorization: 'Bearer <your GitLab token>' } } } } }),
      },
    ],
  },
  {
    id: 'dsh',
    title: 'DeepSeek Harness',
    group: 'Agents',
    about: "DeepSeek's agent harness (dsh). Its web UI can do the same: Settings → Models → Add model provider → Custom model API.",
    steps: (c) => [
      {
        text: 'Add the gateway as a provider, in the profile you run (web for dsh web):',
        file: '$DSH_HOME/profiles/web/cordis.patch.yml',
        code: `- id: llm-pi-ai
  config:
    providers:
      llm-service:
        apiKeyEnv: ${KEY}
        api: openai-completions
        baseURL: ${c.base}
        compat:
          supportsDeveloperRole: false
          maxTokensField: max_tokens
        models:
          - id: ${c.model}
            contextWindow: ${c.context}
            maxTokens: ${c.maxOutput}`,
      },
      { text: 'Start it, then pick the model in its model picker:', file: 'shell', code: 'dsh web' },
    ],
    argus: (c) => [
      {
        text: 'Add Argus to the same file, after the provider (insert: appends the MCP client; !!js reads the token from the environment):',
        file: '$DSH_HOME/profiles/web/cordis.patch.yml',
        code: `- insert:
  - name: '@deepseek-ai/dsh-mcp-client'
    config:
      serverName: argus
      transport: streamable-http
      url: ${c.argusUrl}
      headers:
        Authorization: !!js '\`Bearer \${process.env.${GITLAB}}\`'`,
      },
    ],
  },
  {
    id: 'continue',
    title: 'Continue',
    group: 'Editors',
    about: 'The VS Code and JetBrains assistant, through its OpenAI-compatible provider.',
    steps: (c) => [
      {
        text: 'Add the model, with your key in place of the placeholder:',
        file: '~/.continue/config.yaml',
        code: `name: Argus Arena
version: 0.0.1
schema: v1

models:
  - name: ${c.model}
    provider: openai
    model: ${c.model}
    apiBase: ${c.base}
    apiKey: <your API key>
    roles: [chat, edit, apply]`,
      },
    ],
    argus: (c) => [
      {
        text: 'Add Argus to the same file:',
        file: '~/.continue/config.yaml',
        code: `mcpServers:
  - name: argus
    type: streamable-http
    url: ${c.argusUrl}
    requestOptions:
      headers:
        Authorization: Bearer <your GitLab token>`,
      },
    ],
  },
  {
    id: 'cline',
    title: 'Cline',
    group: 'Editors',
    about: 'The VS Code agent (Roo Code is set up the same way).',
    steps: (c) => [
      { text: 'In its settings, choose the API provider "OpenAI Compatible", and fill in:', file: 'Base URL', code: c.base },
      { text: 'API Key: your API key. Model ID:', file: 'Model ID', code: c.model },
      { text: `Under the model's settings, set the context window to ${c.context.toLocaleString('en-US')} tokens.` },
    ],
    argus: (c) => [
      { text: 'Add a remote MCP server (MCP Servers → Remote Servers) named argus, with this address:', file: 'Server URL', code: c.argusUrl },
      { text: 'and the header Authorization: Bearer <your GitLab token> (edit it in the MCP settings file if the form has no headers).' },
    ],
  },
  {
    id: 'python',
    title: 'Python',
    group: 'Code',
    about: 'The OpenAI SDK, pointed at the gateway.',
    steps: (c) => [
      {
        text: 'With the key in the environment:',
        file: 'app.py',
        code: `import os
from openai import OpenAI

client = OpenAI(base_url="${c.base}", api_key=os.environ["${KEY}"])
reply = client.chat.completions.create(
    model="${c.model}",
    messages=[{"role": "user", "content": "Hello"}],
)
print(reply.choices[0].message.content)`,
      },
    ],
    argus: 'Any MCP client library connects to Argus with the address and header below.',
  },
  {
    id: 'curl',
    title: 'curl',
    group: 'Code',
    about: 'Any OpenAI-compatible client works the same way.',
    steps: (c) => [
      {
        text: 'A chat completion:',
        file: 'shell',
        code: `curl ${c.base}/chat/completions \\
  -H "Authorization: Bearer $${KEY}" \\
  -H "Content-Type: application/json" \\
  -d '{"model": "${c.model}", "messages": [{"role": "user", "content": "Hello"}]}'`,
      },
    ],
    argus: 'Argus speaks MCP over HTTP: a client library, not curl, is the way to call its tools.',
  },
]
