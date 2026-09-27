import { useQuery } from '@tanstack/react-query'
import { Download, ExternalLink } from 'lucide-react'
import { useState } from 'react'
import { ApiKey } from '@/components/app/api-key'
import { CodeBlock } from '@/components/app/code-block'
import { PageHeader } from '@/components/app/page-header'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Field } from '@/components/ui/field'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { api, errorMessage } from '@/lib/api'
import { serviceUrl } from '@/app/nav'

interface ChatConfig {
  model: string | null
  models: { name: string; context: number | null; maxOutput: number | null; vision: boolean; tools: boolean; thinking: boolean; loaded: boolean }[]
  argus: boolean
  gitlabUrl: string | null
}

/** Where the key is kept in each snippet: an environment variable, never pasted into a file. */
const KEY = 'LLM_SERVICE_API_KEY'

/** Setups for each tool, from the gateway's address and the chosen model. */
function setups(root: string, model: string) {
  const base = `${root}/v1`
  return [
    {
      id: 'claude',
      title: 'Claude Code',
      note: 'The gateway speaks Anthropic\'s API too, so Claude Code runs on this model.',
      code: `export ANTHROPIC_BASE_URL=${root}
export ANTHROPIC_AUTH_TOKEN="$${KEY}"
export ANTHROPIC_MODEL=${model}
export ANTHROPIC_DEFAULT_HAIKU_MODEL=${model}
claude`,
    },
    {
      id: 'qwen',
      title: 'Qwen Code',
      note: 'Qwen Code reads the OpenAI variables.',
      code: `export OPENAI_BASE_URL=${base}
export OPENAI_API_KEY="$${KEY}"
export OPENAI_MODEL=${model}
qwen`,
    },
    {
      id: 'opencode',
      title: 'OpenCode',
      note: 'In opencode.json (the project) or ~/.config/opencode/opencode.json.',
      code: JSON.stringify(
        {
          $schema: 'https://opencode.ai/config.json',
          provider: {
            'llm-service': {
              npm: '@ai-sdk/openai-compatible',
              name: 'LLM Service',
              options: { baseURL: base, apiKey: `{env:${KEY}}` },
              models: { [model]: { name: model } },
            },
          },
          model: `llm-service/${model}`,
        },
        null,
        2,
      ),
    },
    {
      id: 'continue',
      title: 'Continue',
      note: 'In ~/.continue/config.yaml, with your key in place of the placeholder.',
      code: `models:
  - name: ${model}
    provider: openai
    model: ${model}
    apiBase: ${base}
    apiKey: <your API key>
    roles: [chat, edit, apply]`,
    },
    {
      id: 'python',
      title: 'Python',
      note: 'The OpenAI SDK, pointed at the gateway.',
      code: `import os
from openai import OpenAI

client = OpenAI(base_url="${base}", api_key=os.environ["${KEY}"])
reply = client.chat.completions.create(
    model="${model}",
    messages=[{"role": "user", "content": "Hello"}],
)
print(reply.choices[0].message.content)`,
    },
    {
      id: 'curl',
      title: 'curl',
      note: 'Any OpenAI-compatible client works the same way.',
      code: `curl ${base}/chat/completions \\
  -H "Authorization: Bearer $${KEY}" \\
  -H "Content-Type: application/json" \\
  -d '{"model": "${model}", "messages": [{"role": "user", "content": "Hello"}]}'`,
    },
  ]
}

/** Argus over MCP, for a coding agent: each person with their own GitLab token, so it sees what they may read. */
function argusSetups(url: string) {
  return [
    { id: 'claude', title: 'Claude Code', code: `claude mcp add --transport http argus ${url} \\\n  --header "Authorization: Bearer $GITLAB_TOKEN"` },
    { id: 'qwen', title: 'Qwen Code', code: `qwen mcp add argus ${url} -t http \\\n  -H "Authorization: Bearer $GITLAB_TOKEN" --trust` },
    {
      id: 'json',
      title: 'Other MCP clients',
      code: JSON.stringify({ mcpServers: { argus: { url, headers: { Authorization: 'Bearer <your GitLab token>' } } } }, null, 2),
    },
  ]
}

const tokens = (n: number | null) => (n ? n.toLocaleString('en-US') : '—')

/**
 * Connect your own tools: an API key, the gateway's address and your models,
 * setups to paste for the common coding agents and SDKs, Argus over MCP, and
 * the stack's certificate for tools that must be told to trust it.
 */
export function ConnectPage() {
  const config = useQuery({ queryKey: ['chat', 'config'], queryFn: ({ signal }) => api<ChatConfig>('/api/chat/config', { signal }) })
  const connect = useQuery({ queryKey: ['account', 'connect'], queryFn: ({ signal }) => api<{ certificate: boolean }>('/api/account/connect', { signal }) })
  const [chosen, setChosen] = useState<string | null>(null)
  const root = serviceUrl('gateway').replace(/\/$/, '')
  const models = config.data?.models ?? []
  const model = chosen ?? config.data?.model ?? models[0]?.name ?? 'your-model'
  const argusUrl = `${serviceUrl('argus')}mcp`
  return (
    <>
      <PageHeader
        title="Connect your tools"
        description="Use the models from your own tools: a coding agent, your editor, a script. They sign in with your API key and spend from your credit."
      />
      <div className="grid grid-cols-[minmax(0,1fr)] gap-6">
        <ApiKey />

        <Card>
          <CardHeader>
            <CardTitle>The address, and your models</CardTitle>
            <CardDescription>An OpenAI-compatible API (and Anthropic's), with the models you may use.</CardDescription>
          </CardHeader>
          <CardContent className="grid gap-4">
            <CodeBlock code={`${root}/v1`} label="the gateway's address" />
            {config.isPending && <Skeleton className="h-24" />}
            {config.error && <Alert variant="destructive">{errorMessage(config.error)}</Alert>}
            {models.length > 0 && (
              <ul className="grid gap-2 sm:grid-cols-2">
                {models.map((m) => (
                  <li key={m.name} className="grid gap-1 rounded-lg border px-3 py-2">
                    <span className="flex flex-wrap items-center gap-2">
                      <code className="font-mono text-sm font-medium [overflow-wrap:anywhere]">{m.name}</code>
                      {!m.loaded && <Badge variant="outline">Not loaded now</Badge>}
                    </span>
                    <span className="text-xs text-muted-foreground">
                      {tokens(m.context)} tokens of context · answers up to {tokens(m.maxOutput)}
                      {m.tools ? ' · tools' : ''}
                      {m.vision ? ' · images' : ''}
                      {m.thinking ? ' · thinking' : ''}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Set up your tool</CardTitle>
            <CardDescription>
              Put your key in <code className="font-mono">{KEY}</code> first (<code className="font-mono">export {KEY}=…</code>), then paste the setup.
            </CardDescription>
          </CardHeader>
          <CardContent className="grid gap-4">
            {models.length > 1 && (
              <Field label="Model" className="max-w-sm">
                <Select value={model} onValueChange={setChosen}>
                  <SelectTrigger className="min-w-0 [&>span]:truncate">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {models.map((m) => (
                      <SelectItem key={m.name} value={m.name}>
                        {m.name}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </Field>
            )}
            <Tabs defaultValue="claude" className="grid grid-cols-[minmax(0,1fr)]">
              <TabsList className="grid h-auto w-full grid-cols-3 gap-1 sm:inline-flex sm:h-9 sm:w-fit">
                {setups(root, model).map((s) => (
                  <TabsTrigger key={s.id} value={s.id} className="h-7">
                    {s.title}
                  </TabsTrigger>
                ))}
              </TabsList>
              {setups(root, model).map((s) => (
                <TabsContent key={s.id} value={s.id} className="grid gap-2">
                  <p className="text-sm text-muted-foreground">{s.note}</p>
                  <CodeBlock code={s.code} label={`the ${s.title} setup`} />
                </TabsContent>
              ))}
            </Tabs>
          </CardContent>
        </Card>

        {config.data?.argus && (
          <Card>
            <CardHeader>
              <CardTitle>Argus, the code index</CardTitle>
              <CardDescription>
                For coding agents over MCP. It signs in with <strong>your GitLab token</strong>, not the API key, so it answers only from the code you may read.
              </CardDescription>
            </CardHeader>
            <CardContent className="grid gap-4">
              <CodeBlock code={argusUrl} label="Argus's address" />
              {config.data.gitlabUrl && (
                <Button variant="outline" className="w-fit" asChild>
                  <a href={`${config.data.gitlabUrl}/-/user_settings/personal_access_tokens?name=Argus&scopes=read_api`} target="_blank" rel="noreferrer">
                    <ExternalLink /> Make a GitLab token (read_api)
                  </a>
                </Button>
              )}
              <Tabs defaultValue="claude" className="grid grid-cols-[minmax(0,1fr)]">
                <TabsList>
                  {argusSetups(argusUrl).map((s) => (
                    <TabsTrigger key={s.id} value={s.id}>
                      {s.title}
                    </TabsTrigger>
                  ))}
                </TabsList>
                {argusSetups(argusUrl).map((s) => (
                  <TabsContent key={s.id} value={s.id}>
                    <CodeBlock code={s.code} label={`the Argus setup for ${s.title}`} />
                  </TabsContent>
                ))}
              </Tabs>
            </CardContent>
          </Card>
        )}

        {connect.data?.certificate && (
          <Card>
            <CardHeader>
              <CardTitle>If your tool rejects the certificate</CardTitle>
              <CardDescription>A private deployment may use its own certificate. Download it and tell the tool to trust it.</CardDescription>
            </CardHeader>
            <CardContent className="grid gap-4">
              <div className="flex flex-wrap gap-2">
                <Button variant="outline" asChild>
                  <a href="/api/account/certificate" download="llm-service-ca.crt">
                    <Download /> The certificate
                  </a>
                </Button>
                <Button variant="outline" asChild>
                  <a href="/api/account/certificate?bundle=true" download="llm-service-bundle.crt">
                    <Download /> With the public CAs
                  </a>
                </Button>
              </div>
              <p className="text-sm text-muted-foreground">
                Node adds a certificate to the ones it trusts; Python and curl replace them, so they take the bundle (the public CAs as well).
              </p>
              <CodeBlock
                code={`# Node tools (Claude Code, Qwen Code, OpenCode)
export NODE_EXTRA_CA_CERTS=~/llm-service-ca.crt
# Python (the OpenAI SDK)
export SSL_CERT_FILE=~/llm-service-bundle.crt
# curl
curl --cacert ~/llm-service-bundle.crt …`}
                label="the certificate settings"
              />
            </CardContent>
          </Card>
        )}
      </div>
    </>
  )
}
