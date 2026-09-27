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

export interface Context {
  /** The gateway without /v1 (Anthropic's API lives there). */
  root: string
  base: string
  model: string
  context: number
  maxOutput: number
  argusUrl: string
}

export interface Step {
  text: string
  code?: string
  /** What the code is: a file's path, or "shell". */
  file?: string
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
  /** How it is told to trust a private certificate. */
  certificate: 'node' | 'python' | 'curl' | string
}

const json = (v: unknown) => JSON.stringify(v, null, 2)

export const tools: Tool[] = [
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
    certificate: 'node',
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
name = "LLM Service"
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
    certificate: 'If it rejects the certificate, set SSL_CERT_FILE to the bundle (below) in the shell that starts it.',
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
    certificate: 'node',
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
              name: 'LLM Service',
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
    certificate: 'node',
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
    certificate: 'python',
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
    certificate: "Hermes trusts its own CA bundle: append the certificate (below) to it.",
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
    certificate: 'node',
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
    certificate: 'node',
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
        code: `name: LLM Service
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
    certificate: 'The editor runs it on Node: set NODE_EXTRA_CA_CERTS (below) where the editor is started.',
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
    certificate: 'VS Code runs it on Node: set NODE_EXTRA_CA_CERTS (below) where VS Code is started.',
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
    certificate: 'python',
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
    certificate: 'curl',
  },
]

/** The setting that makes each kind of tool trust the deployment's certificate. */
export const certificateHint: Record<'node' | 'python' | 'curl', string> = {
  node: 'It runs on Node: set NODE_EXTRA_CA_CERTS to the certificate (below) before starting it.',
  python: 'It runs on Python: set SSL_CERT_FILE to the bundle (below) before starting it.',
  curl: 'Give curl the bundle (below) with --cacert.',
}
