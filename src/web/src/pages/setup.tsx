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
import { Select, SelectContent, SelectGroup, SelectItem, SelectLabel, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { api, errorMessage } from '@/lib/api'
import { serviceUrl } from '@/app/nav'
import { certificateHint, GITLAB, KEY, tools, type Context, type Step, type Tool } from './setup-tools'

interface ChatConfig {
  model: string | null
  models: { name: string; context: number | null; maxOutput: number | null; vision: boolean; tools: boolean; thinking: boolean; loaded: boolean }[]
  argus: boolean
  gitlabUrl: string | null
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
  const [toolId, setToolId] = useState(remembered)
  const root = serviceUrl('gateway').replace(/\/$/, '')
  const models = config.data?.models ?? []
  const model = chosen ?? config.data?.model ?? models[0]?.name ?? 'your-model'
  const current = models.find((m) => m.name === model)
  const argusUrl = `${serviceUrl('argus')}mcp`
  const tool = tools.find((t) => t.id === toolId) ?? tools[0]!
  const context: Context = { root, base: `${root}/v1`, model, context: current?.context ?? 32768, maxOutput: current?.maxOutput ?? 8192, argusUrl }
  const choose = (id: string) => {
    setToolId(id)
    try {
      localStorage.setItem(TOOL_KEY, id)
    } catch {
      // private window: the choice lasts for this page only
    }
  }
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
              Put your key in <code className="font-mono">{KEY}</code> first (<code className="font-mono">export {KEY}=…</code>), choose your tool, and follow its steps.
            </CardDescription>
          </CardHeader>
          <CardContent className="grid gap-5">
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="Your tool">
                <Select value={tool.id} onValueChange={choose}>
                  <SelectTrigger className="min-w-0 [&>span]:truncate">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {groups.map((g) => (
                      <SelectGroup key={g}>
                        <SelectLabel>{g}</SelectLabel>
                        {tools
                          .filter((t) => t.group === g)
                          .map((t) => (
                            <SelectItem key={t.id} value={t.id}>
                              {t.title}
                            </SelectItem>
                          ))}
                      </SelectGroup>
                    ))}
                  </SelectContent>
                </Select>
              </Field>
              {models.length > 1 && (
                <Field label="Model">
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
            </div>
            <Tutorial tool={tool} context={context} argus={config.data?.argus === true} certificate={connect.data?.certificate === true} />
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
              <p className="text-sm text-muted-foreground">
                Your tool's steps above include Argus. For another MCP client, the address and this header are all it needs; keep the token in{' '}
                <code className="font-mono">{GITLAB}</code>, not in a file you share.
              </p>
              <CodeBlock code={JSON.stringify({ mcpServers: { argus: { url: argusUrl, headers: { Authorization: 'Bearer <your GitLab token>' } } } }, null, 2)} label="the Argus setup for other MCP clients" />
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
                code={`# Node tools (Claude Code, Qwen Code, OpenCode, OpenClaw, DeepSeek Harness, editors)
export NODE_EXTRA_CA_CERTS=~/llm-service-ca.crt
# Python (the OpenAI SDK, Aider) and Codex
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

const TOOL_KEY = 'setup.tool'
const groups = [...new Set(tools.map((t) => t.group))]

function remembered(): string {
  try {
    return localStorage.getItem(TOOL_KEY) ?? 'claude'
  } catch {
    return 'claude'
  }
}

/** One tool's steps, numbered: its settings with this deployment filled in, Argus where it speaks MCP, and its certificate setting. */
function Tutorial({ tool, context, argus, certificate }: { tool: Tool; context: Context; argus: boolean; certificate: boolean }) {
  const steps: Step[] = [...tool.steps(context)]
  const argusSteps = argus && typeof tool.argus === 'function' ? tool.argus(context) : []
  const hint = certificateHint[tool.certificate as keyof typeof certificateHint] ?? tool.certificate
  return (
    <section aria-label={`Setting up ${tool.title}`} className="grid gap-4">
      <p className="text-sm text-muted-foreground">{tool.about}</p>
      <StepList steps={steps} tool={tool.title} start={1} />
      {argus && (
        <div className="grid gap-3 border-t pt-4">
          <h3 className="text-sm font-medium">Argus, with your GitLab token in {GITLAB}</h3>
          {argusSteps.length > 0 ? (
            <StepList steps={argusSteps} tool={tool.title} start={steps.length + 1} />
          ) : (
            <p className="text-sm text-muted-foreground">{tool.argus as string}</p>
          )}
        </div>
      )}
      {certificate && <p className="text-xs text-muted-foreground">If {tool.title} rejects the certificate: {hint.charAt(0).toLowerCase() + hint.slice(1)}</p>}
    </section>
  )
}

function StepList({ steps, tool, start }: { steps: Step[]; tool: string; start: number }) {
  return (
    <ol className="grid gap-4" start={start}>
      {steps.map((s, i) => (
        <li key={i} className="grid grid-cols-[1.5rem_minmax(0,1fr)] gap-x-2 gap-y-1.5">
          <span aria-hidden="true" className="flex size-6 items-center justify-center rounded-full bg-primary/10 text-xs font-medium text-primary-ink">
            {start + i}
          </span>
          <p className="text-sm">{s.text}</p>
          {s.code && (
            <div className="col-start-2 grid gap-1">
              {s.file && s.file !== 'shell' && <p className="font-mono text-xs text-muted-foreground [overflow-wrap:anywhere]">{s.file}</p>}
              <CodeBlock code={s.code} label={`${tool}: ${s.file === 'shell' || !s.file ? 'the commands' : s.file}`} />
            </div>
          )}
        </li>
      ))}
    </ol>
  )
}
