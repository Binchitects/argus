import { useQuery } from '@tanstack/react-query'
import { ExternalLink } from 'lucide-react'
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
import { arenaMcp, GITLAB, KEY, tools, type Context, type Step, type Tool } from './setup-tools'

interface ChatConfig {
  model: string | null
  models: { name: string; context: number | null; maxOutput: number | null; vision: boolean; tools: boolean; thinking: boolean; loaded: boolean }[]
  argus: boolean
  gitlabUrl: string | null
}

/** Arena MCP for this person: whether it is on, its address, and the tools it serves them. */
interface McpInfo {
  enabled: boolean
  url: string
  tools: { id: string; title: string; askFirst: boolean }[]
}

const tokens = (n: number | null) => (n ? n.toLocaleString('en-US') : '—')

/**
 * Connect your own tools: an API key, the gateway's address and your models,
 * setups to paste for the common coding agents and SDKs, Arena MCP (every chat tool of
 * yours, with the same key) and Argus over MCP.
 */
export function ConnectPage() {
  const config = useQuery({ queryKey: ['chat', 'config'], queryFn: ({ signal }) => api<ChatConfig>('/api/chat/config', { signal }) })
  const mcp = useQuery({ queryKey: ['account', 'mcp'], queryFn: ({ signal }) => api<McpInfo>('/api/account/mcp', { signal }) })
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
            <Tutorial tool={tool} context={context} argus={config.data?.argus === true} />
          </CardContent>
        </Card>

        {mcp.data?.enabled && <ArenaMcp info={mcp.data} />}

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

/** Arena MCP: every chat tool of the person's, for their agent, at one address with their API key. */
function ArenaMcp({ info }: { info: McpInfo }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Arena MCP (all your tools)</CardTitle>
        <CardDescription>
          The tools you have in the chat, listed below, for your coding agent over MCP (Argus through it needs no GitLab token). It signs in with{' '}
          <strong>your API key</strong>, runs as you, and each call is in the audit log.
        </CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <CodeBlock code={info.url} label="Arena MCP's address" />
        {info.tools.length > 0 ? (
          <ul aria-label="Tools it serves you" className="flex flex-wrap gap-1.5">
            {info.tools.map((t) => (
              <li key={t.id}>
                <Badge variant="outline">
                  {t.title}
                  {t.askFirst ? ' · asks first' : ''}
                </Badge>
              </li>
            ))}
          </ul>
        ) : (
          <p className="text-sm text-muted-foreground">No tools are on for you at the moment.</p>
        )}
        {info.tools.some((t) => t.askFirst) && (
          <p className="text-sm text-muted-foreground">
            A tool that asks first in the chat is marked so your agent asks you before each call: let it ask, rather than trusting every tool.
          </p>
        )}
        {arenaMcp(info.url).map((s) => (
          <section key={s.title} aria-label={`Arena MCP in ${s.title}`} className="grid gap-1.5">
            <h3 className="text-sm font-medium">{s.title}</h3>
            <p className="text-sm text-muted-foreground">{s.text}</p>
            <CodeBlock code={s.code!} label={`the Arena MCP setup for ${s.title}`} />
          </section>
        ))}
      </CardContent>
    </Card>
  )
}

/** One tool's steps, numbered: its settings with this deployment filled in, and Argus where it speaks MCP. */
function Tutorial({ tool, context, argus }: { tool: Tool; context: Context; argus: boolean }) {
  const steps: Step[] = [...tool.steps(context)]
  const argusSteps = argus && typeof tool.argus === 'function' ? tool.argus(context) : []
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
              <CodeBlock code={s.code} label={`${tool}, step ${start + i}: ${s.file === 'shell' || !s.file ? 'the commands' : s.file}`} />
            </div>
          )}
        </li>
      ))}
    </ol>
  )
}
