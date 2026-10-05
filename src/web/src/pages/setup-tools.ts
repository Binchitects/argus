/**
 * How each tool connects: where its settings live, what to put there, how to
 * start it, how to check it works, and Argus over MCP where the tool speaks it.
 * Filled in with this deployment's addresses and the chosen model, for the
 * person's system (a Unix shell, or Windows PowerShell), with the certificate
 * step each tool needs when the site's certificate comes from a private CA.
 * Each follows the tool's own documentation as of October 2026 (the formats
 * change: check its docs when one stops working).
 */

/** Where the API key is kept in each setup: an environment variable, not pasted into a file. Argus takes the same key. */
export const KEY = 'LLM_SERVICE_API_KEY'

/** The name the page saves the site's CA certificate under, in the person's home folder. */
export const CA_FILE = 'arena-ca.crt'

/** A build of Code Arena the app serves (/api/downloads/code-arena). */
export interface CodeArenaBuild {
  rid: string
  system: string
  fileName: string
  size: number
  sha256: string | null
}

export type System = 'unix' | 'windows'

export interface Context {
  /** The gateway without /v1 (Anthropic's API lives there). */
  root: string
  base: string
  model: string
  context: number
  maxOutput: number
  argusUrl: string
  /** The key the person just made, when they chose to see it filled in; else setups say <your API key>. */
  apiKey?: string
  /** This Arena's own address, https://DOMAIN. */
  origin: string
  /** Code Arena's builds; null when none could be listed. */
  codeArena: { version: string; builds: CodeArenaBuild[] } | null
  /** The system the commands are for: a Unix shell (Linux, macOS) or Windows PowerShell. */
  os: System
  /** True when the site's certificate comes from a private CA that tools must be told to trust. */
  ca: boolean
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
  group: 'Coding agents' | 'Agents' | 'Editors' | 'Code' | 'Pipelines'
  /** One line: what it is and how it talks to the gateway. */
  about: string
  steps: (c: Context) => Step[]
  /** How this tool trusts the site's private CA (only shown when the site has one). */
  trust: (c: Context) => Step[]
  /** One command or action that shows the setup works. */
  check: (c: Context) => Step
  /** Argus over MCP, with the person's API key (Argus answers as their GitLab account); a string when the tool cannot. */
  argus: ((c: Context) => Step[]) | string
}

const json = (v: unknown) => JSON.stringify(v, null, 2)
const megabytes = (bytes: number) => `${Math.round(bytes / 1024 / 1024)} MB`
const unix = (c: Context) => c.os === 'unix'

/** The key in a setup that cannot read it from the environment. */
const yourKey = (c: Context) => c.apiKey ?? '<your API key>'

/** A file under the home folder, as the person's system writes it. */
export const home = (c: Context, path: string) => (unix(c) ? `~/${path}` : `%USERPROFILE%\\${path.replaceAll('/', '\\')}`)

/** The saved CA certificate, for a shell command. */
export const caShell = (c: Context) => (unix(c) ? `$HOME/${CA_FILE}` : `$env:USERPROFILE\\${CA_FILE}`)

/** The saved CA certificate, for a settings file (which does not expand ~ or variables). */
const caLiteral = (c: Context) => (unix(c) ? `/home/you/${CA_FILE}` : `C:\\Users\\you\\${CA_FILE}`)

/** The Authorization header with the key from the environment, in a command. */
const bearer = (c: Context) => (unix(c) ? `"Authorization: Bearer $${KEY}"` : `"Authorization: Bearer $env:${KEY}"`)

/** A command continued on the next line. */
const more = (c: Context) => (unix(c) ? ' \\\n  ' : ' `\n  ')

/** Environment variables for this shell, then a command; null as a value is the API key. */
export function env(c: Context, vars: [string, string | null][], run?: string): string {
  const lines = vars.map(([name, value]) =>
    unix(c) ? `export ${name}=${value === null ? `"$${KEY}"` : value}` : `$env:${name} = ${value === null ? `$env:${KEY}` : `"${value}"`}`,
  )
  return [...lines, ...(run ? [run] : [])].join('\n')
}

/** Node-based tools: they trust only their own bundle unless told about the CA (read when the program starts). */
const nodeTrust = (c: Context, tool: string): Step[] => [
  {
    text: `${tool} runs on Node.js, which does not read the system's certificates: give it the CA's file, in your shell profile${unix(c) ? ' (~/.bashrc or ~/.zshrc)' : ''} so it is set before ${tool} starts (Node reads it only then; a project's .env is too late).`,
    file: 'shell',
    code: unix(c) ? `echo 'export NODE_EXTRA_CA_CERTS="${caShell(c)}"' >> ~/.bashrc` : `setx NODE_EXTRA_CA_CERTS "%USERPROFILE%\\${CA_FILE}"`,
  },
]

/** Tools that read the system's certificates: installing the CA (the card above) is enough. */
const systemTrust = (tool: string): Step[] => [{ text: `${tool} trusts the system's certificates: once the CA is installed (the certificate card above), there is nothing else to do.` }]

/** "Reply with exactly: ok" as the question of a check. */
const OK = 'Reply with exactly: ok'

/**
 * Arena MCP: every chat tool of yours at one address, signed in with your API key. No
 * --trust for Qwen Code: a tool that asks first in the chat is marked for the client to ask.
 */
export function arenaMcp(url: string, os: System = 'unix'): (Step & { title: string })[] {
  const c = { os } as Context
  return [
    { title: 'Claude Code', text: 'Add it once, for every project (--scope user):', file: 'shell', code: `claude mcp add --transport http --scope user arena ${url}${more(c)}--header ${bearer(c)}` },
    {
      title: 'Qwen Code',
      text: 'Add it once (in single quotes, the setting keeps the variable, not the key, and Qwen Code reads it when it starts):',
      file: 'shell',
      code: `qwen mcp add arena ${url} -t http${more(c)}-H 'Authorization: Bearer $${KEY}'`,
    },
    {
      title: 'Other MCP clients',
      text: 'The address and the header are all a client needs (streamable HTTP). Clients name the transport differently: "http" in Claude Code, "streamableHttp" in Cline, "streamable-http" in Continue and OpenClaw, "remote" in OpenCode, and Qwen Code takes httpUrl in place of url.',
      file: 'mcp.json',
      code: json({ mcpServers: { arena: { type: 'http', url, headers: { Authorization: 'Bearer <your API key>' } } } }),
    },
  ]
}

export const tools: Tool[] = [
  {
    id: 'code-arena',
    title: 'Code Arena (our own agent)',
    group: 'Coding agents',
    about:
      "Our own coding agent, in your terminal or your browser: one file with nothing to install, that talks only to this Arena (the model through the gateway, all of Arena's tools as you), with file, shell and git tools on your machine.",
    steps: (c) => {
      const builds = c.codeArena?.builds ?? []
      return [
        builds.length > 0
          ? {
              text: `Download it for your system (version ${c.codeArena!.version}):`,
              links: builds.map((b) => ({ label: b.system, href: `/api/downloads/code-arena/${b.rid}`, detail: megabytes(b.size) })),
            }
          : {
              text: "This Arena has no builds of it yet. An admin adds them once: put .NET's runtime packs in tools/offline-nuget (its README says how), then rebuild the app, or run tools/publish-code-arena.sh --offline and hand out the files in dist/code-arena.",
            },
        unix(c)
          ? {
              text: 'Make it runnable and put it on your PATH:',
              file: 'shell',
              code: `chmod +x code-arena
xattr -d com.apple.quarantine code-arena   # macOS only: the browser's download mark
mkdir -p ~/.local/bin && mv code-arena ~/.local/bin/`,
            }
          : { text: 'Put code-arena.exe in a folder on your PATH (or run it from where it is, as .\\code-arena.exe).' },
        {
          text: "Sign in with this Arena's address and your API key. It asks for the key, checks it with the gateway, and keeps it in your config folder, readable by you only.",
          file: 'shell',
          code: `code-arena login --url ${c.origin}`,
        },
        { text: 'Start it in your project (code-arena -p "…" answers once, for scripts):', file: 'shell', code: 'cd your-project\ncode-arena' },
        {
          text: "Or in your browser: code-arena web opens the same agent with the chat's look (its sessions, tool cards, diffs and approvals), served on your machine only.",
          file: 'shell',
          code: 'cd your-project\ncode-arena web',
        },
      ]
    },
    trust: (c) => [
      {
        text: "Sign in with the CA's file instead (or set ARENA_CA_CERT to it): Code Arena keeps it, for the gateway and for Arena MCP.",
        file: 'shell',
        code: `code-arena login --url ${c.origin} --ca ${caShell(c)}`,
      },
    ],
    check: () => ({ text: 'Check it answers:', file: 'shell', code: `code-arena -p "${OK}"` }),
    argus: "Nothing to add: Code Arena reaches Argus through Arena's own tools, as you, with the code you may read.",
  },
  {
    id: 'claude',
    title: 'Claude Code',
    group: 'Coding agents',
    about:
      "Anthropic's coding agent. The gateway speaks Anthropic's API, so it runs on these models; every model name it asks for (its sub-agents' too) is pointed at yours. Anthropic does not support it on other models, but it works.",
    steps: (c) => [
      {
        text: 'Point it at the gateway, then start it:',
        file: 'shell',
        code: env(
          c,
          [
            ['ANTHROPIC_BASE_URL', c.root],
            ['ANTHROPIC_AUTH_TOKEN', null],
            ['ANTHROPIC_MODEL', c.model],
            ['ANTHROPIC_DEFAULT_OPUS_MODEL', c.model],
            ['ANTHROPIC_DEFAULT_SONNET_MODEL', c.model],
            ['ANTHROPIC_DEFAULT_HAIKU_MODEL', c.model],
            ['CLAUDE_CODE_SUBAGENT_MODEL', c.model],
            ['CLAUDE_CODE_MAX_CONTEXT_TOKENS', String(c.context)],
            ['CLAUDE_CODE_MAX_OUTPUT_TOKENS', String(c.maxOutput)],
            ['CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC', '1'],
          ],
          'claude',
        ),
      },
      {
        text: "To keep it for every terminal (and its background agents), put the same values in its settings file's env block instead. The values there are taken as written: the key itself, not the variable.",
        file: home(c, '.claude/settings.json'),
        code: json({
          env: {
            ANTHROPIC_BASE_URL: c.root,
            ANTHROPIC_AUTH_TOKEN: yourKey(c),
            ANTHROPIC_MODEL: c.model,
            ANTHROPIC_DEFAULT_OPUS_MODEL: c.model,
            ANTHROPIC_DEFAULT_SONNET_MODEL: c.model,
            ANTHROPIC_DEFAULT_HAIKU_MODEL: c.model,
            CLAUDE_CODE_SUBAGENT_MODEL: c.model,
            CLAUDE_CODE_MAX_CONTEXT_TOKENS: String(c.context),
            CLAUDE_CODE_MAX_OUTPUT_TOKENS: String(c.maxOutput),
            CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1',
            ...(c.ca ? { NODE_EXTRA_CA_CERTS: caLiteral(c) } : {}),
          },
        }),
      },
    ],
    trust: (c) => [
      {
        text: "It trusts the system's certificates. Without the CA installed there, give it the file (in the settings file's env block above, or in the shell):",
        file: 'shell',
        code: env(c, [['NODE_EXTRA_CA_CERTS', caShell(c)]]),
      },
    ],
    check: () => ({ text: 'Check it answers (then /status inside it shows the gateway):', file: 'shell', code: `claude -p "${OK}"` }),
    argus: (c) => [
      { text: 'Add Argus once, for every project (--scope user):', file: 'shell', code: `claude mcp add --transport http --scope user argus ${c.argusUrl}${more(c)}--header ${bearer(c)}` },
      { text: 'Check it connects:', file: 'shell', code: 'claude mcp get argus' },
    ],
  },
  {
    id: 'codex',
    title: 'Codex CLI',
    group: 'Coding agents',
    about: "OpenAI's coding agent. It uses the Responses API, which the gateway serves for these models.",
    steps: (c) => [
      {
        text: "Add the gateway as a provider and make it the default (top-level keys before the first [table]; it must be this file, not a project's):",
        file: home(c, '.codex/config.toml'),
        code: `model = "${c.model}"
model_provider = "llm-service"
model_context_window = ${c.context}
web_search = "disabled"

[model_providers.llm-service]
name = "Argus Arena"
base_url = "${c.base}"
env_key = "${KEY}"
wire_api = "responses"`,
      },
      { text: 'Start it:', file: 'shell', code: 'codex' },
    ],
    trust: (c) => [
      {
        text: "Give it the CA's file (it is used for the gateway and for MCP servers too):",
        file: 'shell',
        code: env(c, [['CODEX_CA_CERTIFICATE', caShell(c)]]),
      },
    ],
    check: () => ({ text: 'Check it answers (codex doctor checks the network and certificates):', file: 'shell', code: `codex exec --skip-git-repo-check "${OK}"` }),
    argus: (c) => [
      {
        text: 'Add Argus in the same file:',
        file: home(c, '.codex/config.toml'),
        code: `[mcp_servers.argus]
url = "${c.argusUrl}"
bearer_token_env_var = "${KEY}"`,
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
        text: 'Point it at the gateway, then start it (--auth-type openai: a fresh install would otherwise ask how to sign in):',
        file: 'shell',
        code: env(c, [['OPENAI_BASE_URL', c.base], ['OPENAI_API_KEY', null], ['OPENAI_MODEL', c.model]], 'qwen --auth-type openai'),
      },
      {
        text: "To keep it, and give it the model's window, put it in its settings instead (the key stays in the environment):",
        file: home(c, '.qwen/settings.json'),
        code: json({
          modelProviders: {
            openai: [{ id: c.model, name: c.model, baseUrl: c.base, envKey: KEY, generationConfig: { contextWindowSize: c.context, samplingParams: { max_tokens: c.maxOutput } } }],
          },
          security: { auth: { selectedType: 'openai' } },
          model: { name: c.model },
        }),
      },
    ],
    trust: (c) => nodeTrust(c, 'Qwen Code'),
    check: () => ({ text: 'Check it answers (with the settings file, --auth-type is not needed):', file: 'shell', code: `qwen --auth-type openai -p "${OK}"` }),
    argus: (c) => [
      {
        text: 'Add Argus once (in single quotes, the setting keeps the variable, not the key; --trust runs its tools without asking each time):',
        file: 'shell',
        code: `qwen mcp add argus ${c.argusUrl} -t http${more(c)}-H 'Authorization: Bearer $${KEY}' --trust`,
      },
    ],
  },
  {
    id: 'opencode',
    title: 'OpenCode',
    group: 'Coding agents',
    about: 'An open-source coding agent, through its OpenAI-compatible provider (built in: nothing to install).',
    steps: (c) => [
      {
        text: 'Add the gateway as a provider (in the project, or for every project in this file):',
        file: home(c, '.config/opencode/opencode.json'),
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
          small_model: `llm-service/${c.model}`,
        }),
      },
      { text: 'Start it:', file: 'shell', code: 'opencode' },
    ],
    trust: (c) => nodeTrust(c, 'OpenCode'),
    check: (c) => ({ text: 'Check it answers:', file: 'shell', code: `opencode run -m llm-service/${c.model} "${OK}"` }),
    argus: (c) => [
      {
        text: 'Add Argus to the same file, beside "provider" (oauth off: it signs in with the key):',
        file: home(c, '.config/opencode/opencode.json'),
        code: json({ mcp: { argus: { type: 'remote', url: c.argusUrl, enabled: true, oauth: false, headers: { Authorization: `Bearer {env:${KEY}}` } } } }),
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
        text: "Tell it the model's window, so it neither warns nor cuts the chat short:",
        file: home(c, '.aider.model.metadata.json'),
        code: json({
          [`openai/${c.model}`]: {
            max_input_tokens: c.context, max_output_tokens: c.maxOutput, max_tokens: c.maxOutput,
            input_cost_per_token: 0, output_cost_per_token: 0, litellm_provider: 'openai', mode: 'chat',
          },
        }),
      },
      {
        text: 'Point it at the gateway, then start it with the model (the openai/ prefix says which API):',
        file: 'shell',
        code: env(c, [['OPENAI_API_BASE', c.base], ['OPENAI_API_KEY', null]], `aider --model openai/${c.model}`),
      },
    ],
    trust: (c) => [
      {
        text: "It does not read the system's certificates: point it at the CA's file (it talks only to the gateway, so the file alone is enough):",
        file: 'shell',
        code: env(c, [['SSL_CERT_FILE', caShell(c)], ['REQUESTS_CA_BUNDLE', caShell(c)]]),
      },
    ],
    check: (c) => ({ text: 'Check it answers:', file: 'shell', code: `aider --model openai/${c.model} --no-git --message "${OK}"` }),
    argus: 'Aider has no MCP, so Argus is not available in it.',
  },
  {
    id: 'hermes',
    title: 'Hermes',
    group: 'Agents',
    about: "Nous Research's agent. It needs a context window of at least 64,000 tokens.",
    steps: (c) => [
      {
        text: 'Set the model (the key stays in the environment, key_env):',
        file: unix(c) ? '~/.hermes/config.yaml' : '%LOCALAPPDATA%\\hermes\\config.yaml',
        code: `model:
  provider: custom
  default: ${c.model}
  base_url: ${c.base}
  key_env: ${KEY}
  context_length: ${c.context}`,
      },
    ],
    trust: (c) => [
      ...systemTrust('Hermes'),
      ...(c.ca ? [{ text: "Its MCP servers check certificates on their own: the Argus setup below gives them the CA's file (ssl_verify)." }] : []),
    ],
    check: () => ({ text: 'Check it answers (hermes doctor checks the setup):', file: 'shell', code: `hermes chat -q "${OK}"` }),
    argus: (c) => [
      {
        text: 'Add Argus to the same file:',
        file: unix(c) ? '~/.hermes/config.yaml' : '%LOCALAPPDATA%\\hermes\\config.yaml',
        code: `mcp_servers:
  argus:
    url: ${c.argusUrl}
    headers:
      Authorization: "Bearer \${${KEY}}"${c.ca ? `\n    ssl_verify: ${caLiteral(c)}` : ''}`,
      },
      { text: 'Check it connects:', file: 'shell', code: 'hermes mcp test argus' },
    ],
  },
  {
    id: 'openclaw',
    title: 'OpenClaw',
    group: 'Agents',
    about: 'The personal assistant agent, through its OpenAI-compatible provider.',
    steps: (c) => [
      {
        text: "Add the gateway as a provider and make the model the default (OpenClaw reads LLM_SERVICE_API_KEY from its gateway's environment, or from ~/.openclaw/.env; never from a workspace's .env):",
        file: home(c, '.openclaw/openclaw.json'),
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
      { text: `Restart OpenClaw's gateway so it reads the file. The model then answers as llm-service/${c.model}.` },
    ],
    trust: (c) => [
      {
        text: "It runs on Node.js: the CA's file must be in its gateway's environment when it starts (your shell profile, or the systemd/launchd service; ~/.openclaw/.env is read too late for this one).",
        file: 'shell',
        code: unix(c) ? `echo 'export NODE_EXTRA_CA_CERTS="${caShell(c)}"' >> ~/.bashrc` : `setx NODE_EXTRA_CA_CERTS "%USERPROFILE%\\${CA_FILE}"`,
      },
    ],
    check: (c) => ({ text: 'Check it answers:', file: 'shell', code: `openclaw agent exec "${OK}" --model llm-service/${c.model}` }),
    argus: (c) => [
      {
        text: 'Add Argus to the same file (the key from the environment, not written in):',
        file: home(c, '.openclaw/openclaw.json'),
        code: json({ mcp: { servers: { argus: { url: c.argusUrl, transport: 'streamable-http', headers: { Authorization: `Bearer \${${KEY}}` } } } } }),
      },
      { text: 'Check it connects:', file: 'shell', code: 'openclaw mcp doctor argus --probe' },
    ],
  },
  {
    id: 'dsh',
    title: 'DeepSeek Harness',
    group: 'Agents',
    about: "DeepSeek's agent harness (dsh). Its web UI can do the same: Settings → Models → Add model provider → Custom model API.",
    steps: (c) => [
      {
        text: 'Add the gateway as a provider, for every profile (a provider row replaces the whole llm-pi-ai config: if the web UI wrote one in a profile, it is this one that counts):',
        file: unix(c) ? '~/.dsh/cordis.patch.yml' : '%USERPROFILE%\\.dsh\\cordis.patch.yml',
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
    trust: (c) => nodeTrust(c, 'dsh'),
    check: () => ({ text: 'Check it answers:', file: 'shell', code: `dsh headless "${OK}"` }),
    argus: (c) => [
      {
        text: 'Add Argus to the same file, after the provider (insert: appends the MCP client; !!js reads the key from the environment):',
        file: unix(c) ? '~/.dsh/cordis.patch.yml' : '%USERPROFILE%\\.dsh\\cordis.patch.yml',
        code: `- insert:
    - id: mcp-argus
      name: '@deepseek-ai/dsh-mcp-client'
      config:
        serverName: argus
        transport: streamable-http
        url: ${c.argusUrl}
        headers:
          Authorization: !!js '\`Bearer \${process.env.${KEY}}\`'`,
      },
    ],
  },
  {
    id: 'continue',
    title: 'Continue',
    group: 'Editors',
    about: "The VS Code and JetBrains assistant, through its OpenAI-compatible provider. The editors cannot read your shell's variables, so the key goes in Continue's own secrets file.",
    steps: (c) => [
      { text: 'Keep the key in its secrets file (this line, with your key):', file: home(c, '.continue/.env'), code: `${KEY}=${yourKey(c)}` },
      {
        text: 'Add the model:',
        file: home(c, '.continue/config.yaml'),
        code: `name: Argus Arena
version: 0.0.1
schema: v1

models:
  - name: ${c.model}
    provider: openai
    model: ${c.model}
    apiBase: ${c.base}
    apiKey: \${{ secrets.${KEY} }}
    roles: [chat, edit, apply, summarize]
    capabilities: [tool_use]
    defaultCompletionOptions:
      contextLength: ${c.context}
      maxTokens: ${c.maxOutput}${c.ca ? `\n    requestOptions:\n      caBundlePath: ${caLiteral(c)}` : ''}`,
      },
    ],
    trust: () => [{ text: "The model's requestOptions.caBundlePath above gives it the CA's file (and the Argus setup below has the same line)." }],
    check: () => ({ text: "Check it answers: send any message in Continue's chat panel, or with its CLI:", file: 'shell', code: `cn -p "${OK}"` }),
    argus: (c) => [
      {
        text: 'Add Argus to the same file:',
        file: home(c, '.continue/config.yaml'),
        code: `mcpServers:
  - name: argus
    type: streamable-http
    url: ${c.argusUrl}
    requestOptions:
      headers:
        Authorization: Bearer \${{ secrets.${KEY} }}${c.ca ? `\n      caBundlePath: ${caLiteral(c)}` : ''}`,
      },
    ],
  },
  {
    id: 'cline',
    title: 'Cline',
    group: 'Editors',
    about: 'The VS Code and JetBrains agent.',
    steps: (c) => [
      { text: 'In its settings, choose the API provider "OpenAI Compatible", and fill in:', file: 'Base URL', code: c.base },
      { text: 'API Key: your API key. Model ID:', file: 'Model ID', code: c.model },
      { text: `Under Model Configuration, set the context window to ${c.context.toLocaleString('en-US')} tokens and Max Output Tokens to ${c.maxOutput.toLocaleString('en-US')}.` },
    ],
    trust: () => [
      {
        text: "VS Code trusts the system's certificates: install the CA (the card above). In JetBrains: Settings → Tools → Server Certificates. Cline's CLI runs on Node.js: set NODE_EXTRA_CA_CERTS to the CA's file.",
      },
    ],
    check: () => ({ text: 'Check it answers: ask it anything in its panel.' }),
    argus: (c) => [
      {
        text: 'MCP Servers → Configure → Configure MCP Servers, and add Argus (type streamableHttp, or it tries the old SSE transport; Cline does not read variables here, so the key is written in):',
        file: 'cline_mcp_settings.json',
        code: json({ mcpServers: { argus: { type: 'streamableHttp', url: c.argusUrl, headers: { Authorization: `Bearer ${yourKey(c)}` }, disabled: false, autoApprove: [] } } }),
      },
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
    trust: (c) => [
      {
        text: "The SDK (version 3 and later) trusts the system's certificates. With an older one, or without the CA installed, point it at the file:",
        file: 'shell',
        code: env(c, [['SSL_CERT_FILE', caShell(c)]]),
      },
    ],
    check: () => ({ text: 'Check it answers:', file: 'shell', code: 'python app.py' }),
    argus: 'Any MCP client library connects to Argus with the address and header below.',
  },
  {
    id: 'curl',
    title: 'curl',
    group: 'Code',
    about: 'Any OpenAI-compatible client works the same way.',
    steps: (c) =>
      unix(c)
        ? [
            {
              text: 'A chat completion:',
              file: 'shell',
              code: `curl${c.ca ? ` --cacert ${caShell(c)}` : ''} ${c.base}/chat/completions \\
  -H "Authorization: Bearer $${KEY}" \\
  -H "Content-Type: application/json" \\
  -d '{"model": "${c.model}", "messages": [{"role": "user", "content": "Hello"}]}'`,
            },
          ]
        : [
            {
              text: "A chat completion (PowerShell's own client: curl's JSON quoting does not survive PowerShell):",
              file: 'shell',
              code: `Invoke-RestMethod -Method Post -Uri "${c.base}/chat/completions" \`
  -Headers @{ Authorization = "Bearer $env:${KEY}" } -ContentType "application/json" \`
  -Body '{"model": "${c.model}", "messages": [{"role": "user", "content": "Hello"}]}'`,
            },
          ],
    trust: (c) =>
      unix(c)
        ? [{ text: "--cacert in the command above gives curl the CA's file (or install the CA in the system, and leave it out)." }]
        : [{ text: 'PowerShell trusts the Windows certificate store: install the CA there (the card above).' }],
    check: (c) => ({
      text: 'Check the key and the address (your models, listed):',
      file: 'shell',
      code: unix(c)
        ? `curl${c.ca ? ` --cacert ${caShell(c)}` : ''} ${c.base}/models -H "Authorization: Bearer $${KEY}"`
        : `Invoke-RestMethod -Uri "${c.base}/models" -Headers @{ Authorization = "Bearer $env:${KEY}" }`,
    }),
    argus: 'Argus speaks MCP over HTTP: a client library, not curl, is the way to call its tools.',
  },
  {
    id: 'gitlab-ci',
    title: 'GitLab CI',
    group: 'Pipelines',
    about: 'A review of each merge request as one comment, and an explanation when a pipeline fails, from jobs in your own pipeline (the arena CLI: Python 3, no packages).',
    steps: (c) => [
      {
        text: "In the project's Settings → CI/CD → Variables, add ARENA_KEY (an API key, masked), ARENA_GITLAB_TOKEN (a project access token, role Reporter, scope api, masked) and ARENA_URL:",
        file: 'ARENA_URL',
        code: c.root,
      },
      {
        text: 'Copy clients/arena/arena from the Argus Arena repository into yours, as ci/arena, and include the template from that repository (its path in your GitLab in place of platform/argus-arena):',
        file: '.gitlab-ci.yml',
        code: `include:
  - project: platform/argus-arena     # the Argus Arena repository in your GitLab
    ref: main                         # or the release tag you run
    file: clients/gitlab-ci/arena-review.yml

variables:
  ARENA_CLI: ci/arena                 # or ARENA_PROJECT: platform/argus-arena, when ARENA_GITLAB_TOKEN can read it
  ARENA_MODEL: ${c.model}
  ARENA_EXPLAIN_POST: "true"          # a failed pipeline's explanation on its merge request too`,
      },
      {
        text: 'Each merge request then gets a review, and a failed pipeline an explanation in its arena-explain job. The same CLI answers in a terminal:',
        file: 'shell',
        code: env(c, [['ARENA_URL', c.root], ['ARENA_KEY', null]], 'git diff main | python3 ci/arena ask "Review this change"'),
      },
    ],
    trust: () => [{ text: "Add a CI/CD variable ARENA_CA_CERT with the CA certificate's text (open the downloaded file: from -----BEGIN CERTIFICATE----- to its end)." }],
    check: () => ({ text: "Check it: open a merge request. Its pipeline has an arena-review job, and the review comes as a comment from the project token's bot." }),
    argus: 'The jobs call the gateway only: Argus is for agents, over MCP.',
  },
]
