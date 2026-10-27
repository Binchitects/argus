import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Atom, ChartNoAxesColumn, CircleCheck, CircleHelp, CircleX, FlaskConical, Globe, MessageSquare, Sigma, type LucideIcon } from 'lucide-react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, useLocation, useNavigate, useParams } from 'react-router'
import { ChoiceCard } from '@/components/app/choice-card'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { EmptyState } from '@/components/ui/empty-state'
import { Skeleton } from '@/components/ui/skeleton'
import { Field } from '@/components/ui/field'
import { Textarea } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { conversationQuery, streamChat } from './chat/api'
import { Markdown } from './chat/markdown'
import type { Conversation, Message } from './chat/types'

/** A science of the lab, as the server says it: its tools are those the person may use. */
interface LabField {
  id: string
  title: string
  about: string
  tools: string[]
}

interface LabFields {
  available: boolean
  why: string | null
  fields: LabField[]
}

interface Notebook {
  id: string
  title: string
  field: string | null
  updatedAt: string
  checks: { proved: number; refuted: number; numeric: number }
}

const fieldIcons: Record<string, LucideIcon> = { mathematics: Sigma, physics: Atom, chemistry: FlaskConical, statistics: ChartNoAxesColumn, general: Globe }

const fieldsQuery = { queryKey: ['lab', 'fields'] as const, queryFn: ({ signal }: { signal: AbortSignal }) => api<LabFields>('/api/lab/fields', { signal }) }
const notebooksQuery = { queryKey: ['lab', 'notebooks'] as const, queryFn: ({ signal }: { signal: AbortSignal }) => api<Notebook[]>('/api/lab/notebooks', { signal }) }

/** "3 proved · 1 refuted · 2 only numerically", or nothing checked yet. */
function ChecksLine({ checks, className }: { checks: Notebook['checks']; className?: string }) {
  const parts = [
    checks.proved > 0 && { n: checks.proved, text: 'proved', tone: 'text-success-ink', Icon: CircleCheck },
    checks.refuted > 0 && { n: checks.refuted, text: 'refuted', tone: 'text-destructive-ink', Icon: CircleX },
    checks.numeric > 0 && { n: checks.numeric, text: 'only numerically', tone: 'text-warning-ink', Icon: CircleHelp },
  ].filter((p) => !!p)
  if (parts.length === 0) return <span className={cn('text-xs text-muted-foreground', className)}>Nothing checked yet</span>
  return (
    <span className={cn('flex flex-wrap items-center gap-x-3 gap-y-1 text-xs', className)}>
      {parts.map((p) => (
        <span key={p.text} className={cn('inline-flex items-center gap-1', p.tone)}>
          <p.Icon className="size-3.5" aria-hidden="true" />
          {p.n} {p.text}
        </span>
      ))}
    </span>
  )
}

/**
 * The science lab: a problem of a science worked by a model with the lab's tools, each step computed and checked by a tool
 * (SymPy, Pint, Python) rather than taken on trust, kept as a notebook. The notebooks here, newest first, with what was
 * checked in each.
 */
export function LabPage() {
  const fields = useQuery(fieldsQuery)
  const notebooks = useQuery(notebooksQuery)
  const navigate = useNavigate()
  const [field, setField] = useState('mathematics')
  const [problem, setProblem] = useState('')
  const [starting, setStarting] = useState(false)
  const titles = useMemo(() => new Map(fields.data?.fields.map((f) => [f.id, f.title])), [fields.data])

  const start = async () => {
    setStarting(true)
    try {
      const made = await api<{ id: string }>('/api/chat/conversations', { body: { lab: field } })
      navigate(`/lab/${made.id}`, { state: { problem: problem.trim() } })
    } catch (e) {
      toast.error(errorMessage(e))
      setStarting(false)
    }
  }

  return (
    <>
      <PageHeader
        title="Science lab"
        description="A problem worked step by step by a model with the lab's tools: each computation done and each step checked by SymPy, Pint or Python, never taken on trust, and kept as a notebook."
      />
      {fields.error && <QueryError error={fields.error} retry={() => fields.refetch()} />}
      {fields.data && !fields.data.available && (
        <Alert variant="warning" className="mb-4" title="The lab is not yours yet">
          {fields.data.why}
        </Alert>
      )}
      {fields.data?.available && (
        <Card className="mb-6">
          <CardHeader>
            <CardTitle>A new problem</CardTitle>
            <CardDescription>Choose its science, write it as you would to a colleague, and the lab works it.</CardDescription>
          </CardHeader>
          <CardContent className="grid gap-4">
            <fieldset className="grid gap-2 sm:grid-cols-2 lg:grid-cols-5">
              <legend className="sr-only">Science</legend>
              {fields.data.fields.map((f) => (
                <ChoiceCard key={f.id} name="lab-field" checked={field === f.id} onChange={() => setField(f.id)} icon={fieldIcons[f.id] ?? FlaskConical} title={f.title} hint={f.about} />
              ))}
            </fieldset>
            <Field label="The problem">
              <Textarea
                dir="auto"
                className="min-h-28"
                maxLength={20000}
                value={problem}
                onChange={(e) => setProblem(e.target.value)}
                placeholder="e.g. Show that the integral of exp(-x^2) over the real line is sqrt(pi), and check each step."
              />
            </Field>
            <div>
              <Button onClick={() => void start()} loading={starting} disabled={problem.trim().length === 0}>
                <FlaskConical /> Work it
              </Button>
            </div>
          </CardContent>
        </Card>
      )}
      <h2 className="mb-2 text-sm font-medium">Notebooks</h2>
      {notebooks.isPending && <Skeleton className="h-20 w-full" />}
      {notebooks.error && <QueryError error={notebooks.error} retry={() => notebooks.refetch()} />}
      {notebooks.data && notebooks.data.length === 0 && (
        <EmptyState icon={FlaskConical} title="No notebooks yet">
          A problem you work here becomes a notebook: every step, what checked it and how it went.
        </EmptyState>
      )}
      {notebooks.data && notebooks.data.length > 0 && (
        <ul aria-label="Notebooks" className="grid gap-2">
          {notebooks.data.map((n) => (
            <li key={n.id}>
              <Link to={`/lab/${n.id}`} className="flex flex-wrap items-center gap-x-3 gap-y-1 rounded-lg border p-3 outline-none hover:bg-accent/50 focus-visible:ring-[3px] focus-visible:ring-ring">
                <span className="min-w-0 flex-1 truncate font-medium">{n.title}</span>
                {n.field && <Badge variant="outline">{titles.get(n.field) ?? n.field}</Badge>}
                <ChecksLine checks={n.checks} />
                <time dateTime={n.updatedAt} className="text-xs text-muted-foreground">
                  {new Date(n.updatedAt).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })}
                </time>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </>
  )
}

/** The messages of the notebook's current branch, oldest first. */
function branchOf(c: Conversation): Message[] {
  const byId = new Map(c.messages.map((m) => [m.id, m]))
  const out: Message[] = []
  for (let m = c.currentLeafId ? byId.get(c.currentLeafId) : c.messages.at(-1); m; m = m.parentId ? byId.get(m.parentId) : undefined) out.push(m)
  return out.reverse()
}

const parse = (text: string | null | undefined): Record<string, unknown> | null => {
  try {
    const v = JSON.parse(text ?? '') as unknown
    return v && typeof v === 'object' ? (v as Record<string, unknown>) : null
  } catch {
    return null
  }
}

type Verdict = 'proved' | 'refuted' | 'numeric' | 'other'

const verdictOf = (name: string, result: Record<string, unknown> | null): Verdict | null => {
  if (!result) return null
  if (name === 'math_check') return result.verdict === 'proved' ? 'proved' : result.verdict === 'refuted' ? 'refuted' : result.verdict === 'holds numerically' ? 'numeric' : 'other'
  if (name === 'math_solve') {
    const s = (result.solutions as { checked: boolean }[] | undefined) ?? []
    return s.length === 0 ? 'other' : s.every((x) => x.checked) ? 'proved' : 'refuted'
  }
  return null
}

const verdictLook: Record<Verdict, { label: string; tone: string; Icon: typeof CircleCheck }> = {
  proved: { label: 'Proved', tone: 'border-success/40 bg-success/10 text-success-ink', Icon: CircleCheck },
  refuted: { label: 'Refuted', tone: 'border-destructive/40 bg-destructive/10 text-destructive-ink', Icon: CircleX },
  numeric: { label: 'Holds numerically, not proved', tone: 'border-warning/40 bg-warning/10 text-warning-ink', Icon: CircleHelp },
  other: { label: 'Not shown', tone: 'border-border text-muted-foreground', Icon: CircleHelp },
}

const math = (latex: unknown) => (typeof latex === 'string' && latex.length > 0 ? `$$${latex}$$` : '')

/** One call of a tool in the notebook: what it was asked, what it gave, and (a check's) its verdict. */
function ToolCell({ name, args, result }: { name: string; args: Record<string, unknown>; result: Message | undefined }) {
  const failed = result?.status === 'failed' || (!!result && !parse(result.content) && name.startsWith('math_'))
  const r = parse(result?.content)
  const verdict = verdictOf(name, r)
  const look = verdict ? verdictLook[verdict] : null
  let body: string
  let title: string
  if (name === 'math_check') {
    title = 'Check'
    body = `${r?.left && r?.right ? `$$${String(r.left)} = ${String(r.right)}$$` : `${String(args.left ?? '')} = ${String(args.right ?? '')}`}\n\n${String(r?.detail ?? '')}`
  } else if (name === 'math_compute') {
    title = `Compute: ${String(args.operation ?? 'simplify')}`
    body = `\`${String(args.expression ?? '')}\`\n\n${math(r?.latex)}${r?.numeric ? `\n\n≈ ${String(r.numeric)}` : ''}`
  } else if (name === 'math_solve') {
    title = 'Solve'
    const solutions = (r?.solutions as { latex: string; checked: boolean }[] | undefined) ?? []
    body = `${((args.equations as string[] | undefined) ?? []).map((e) => `\`${e}\``).join(', ')}\n\n${solutions.map((s) => `${s.checked ? '✓' : '✗'} $${s.latex}$`).join('\n\n') || 'No solution.'}`
  } else if (name === 'units_convert') {
    title = 'Units'
    body = `${String(args.quantity ?? '')}${args.to ? ` → ${String(args.to)}` : ''}: **${String(r?.result ?? '')}**`
  } else if (name === 'run_python' || name === 'run_code') {
    title = name === 'run_python' ? 'Python' : 'Program'
    const out = r ? String(r.stdout ?? '') : (result?.content ?? '')
    body = `\`\`\`python\n${String(args.code ?? '')}\n\`\`\`\n${out ? `\`\`\`text\n${out.slice(0, 4000)}\n\`\`\`` : ''}`
  } else {
    title = name
    body = (result?.content ?? '').slice(0, 2000)
  }
  return (
    <section aria-label={title} className={cn('rounded-lg border p-3', look?.tone)}>
      <div className="mb-1 flex flex-wrap items-center gap-2 text-xs font-medium">
        {name.startsWith('math_') || name === 'units_convert' ? <Sigma className="size-3.5" aria-hidden="true" /> : null}
        <span>{title}</span>
        {look && (
          <span className="ml-auto inline-flex items-center gap-1">
            <look.Icon className="size-3.5" aria-hidden="true" />
            {look.label}
          </span>
        )}
        {!result && <span className="ml-auto text-muted-foreground">Working…</span>}
      </div>
      {failed ? <p className="text-sm text-destructive-ink">{result?.content}</p> : <Markdown text={body} />}
    </section>
  )
}

/**
 * A lab notebook: the problem, each step the model took (its words, each computation and each check with its verdict),
 * what was proved and what not on top; follow-ups below. It is a chat underneath (Open as a chat).
 */
export function LabNotebookPage() {
  const { id = '' } = useParams()
  const location = useLocation()
  const queryClient = useQueryClient()
  const notebook = useQuery(conversationQuery(id))
  const fields = useQuery(fieldsQuery)
  const [ask, setAsk] = useState('')
  const [working, setWorking] = useState(false)
  const [live, setLive] = useState('')
  const sent = useRef(false)

  const send = async (text: string) => {
    setWorking(true)
    setLive('')
    const stop = new AbortController()
    try {
      await streamChat(`/api/chat/conversations/${id}/messages`, { content: text, attachments: [] }, (e) => {
        if (e.type === 'content') setLive((t) => t + e.text)
        // Each step lands in the notebook as it is done.
        if (e.type === 'tool_result' || e.type === 'question' || e.type === 'done' || e.type === 'title') {
          if (e.type !== 'title') setLive('')
          void queryClient.invalidateQueries({ queryKey: conversationQuery(id).queryKey })
        }
        if (e.type === 'error') toast.error(e.message)
      }, stop.signal)
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setWorking(false)
      setLive('')
      void queryClient.invalidateQueries({ queryKey: conversationQuery(id).queryKey })
      void queryClient.invalidateQueries({ queryKey: notebooksQuery.queryKey })
    }
  }

  // The problem from the lab's page goes once, as the notebook's first message.
  const problem = (location.state as { problem?: string } | null)?.problem
  useEffect(() => {
    if (sent.current || !problem || !notebook.data || notebook.data.messages.length > 0) return
    sent.current = true
    void send(problem)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- once, when the notebook is read
  }, [notebook.data, problem])

  if (notebook.isPending) return <Skeleton className="h-60 w-full" />
  if (notebook.error) return <QueryError error={notebook.error} retry={() => notebook.refetch()} />
  const c = notebook.data
  const steps = branchOf(c)
  const results = new Map(steps.filter((m) => m.role === 'tool' && m.toolCallId).map((m) => [m.toolCallId!, m]))
  const verdicts = steps
    .filter((m) => m.role === 'tool' && m.toolName)
    .map((m) => verdictOf(m.toolName!, parse(m.content)))
    .filter((v): v is Verdict => !!v)
  const checks = { proved: verdicts.filter((v) => v === 'proved').length, refuted: verdicts.filter((v) => v === 'refuted').length, numeric: verdicts.filter((v) => v === 'numeric').length }
  const field = fields.data?.fields.find((f) => f.id === c.labField)
  let asked = 0

  return (
    <>
      <PageHeader
        title={c.title}
        description={
          <span className="flex flex-wrap items-center gap-3">
            {field && <Badge variant="outline">{field.title}</Badge>}
            <ChecksLine checks={checks} />
          </span>
        }
        actions={
          <Button asChild variant="outline">
            <Link to={`/chat/${id}`}>
              <MessageSquare /> Open as a chat
            </Link>
          </Button>
        }
      />
      <ol aria-label="Notebook" className="grid gap-3">
        {steps
          .filter((m) => m.role !== 'tool')
          .map((m) =>
            m.role === 'user' ? (
              <li key={m.id}>
                <section aria-label={asked++ === 0 ? 'The problem' : 'Asked'} className="rounded-lg bg-muted/50 p-3">
                  <p className="mb-1 text-xs font-medium text-muted-foreground">{asked === 1 ? 'The problem' : 'Asked'}</p>
                  {/* As written: x**2 is mathematics here, not Markdown. */}
                  <p dir="auto" className="text-sm break-words whitespace-pre-wrap">
                    {m.content}
                  </p>
                </section>
              </li>
            ) : (
              <li key={m.id} className="grid gap-2">
                {m.content.trim().length > 0 && <Markdown text={m.content} />}
                {m.toolCalls?.map((t) => <ToolCell key={t.id} name={t.function.name} args={parse(t.function.arguments) ?? {}} result={results.get(t.id)} />)}
                {m.error && <Alert variant="destructive">{m.error}</Alert>}
              </li>
            ),
          )}
        {working && (
          <li aria-live="polite" className="grid gap-2">
            {live ? <Markdown text={live} live /> : <p className="text-sm text-muted-foreground">Working…</p>}
          </li>
        )}
      </ol>
      <form
        className="mt-6 grid gap-2"
        onSubmit={(e) => {
          e.preventDefault()
          if (!ask.trim() || working) return
          const text = ask.trim()
          setAsk('')
          void send(text)
        }}
      >
        <Field label="Ask more">
          <Textarea dir="auto" value={ask} onChange={(e) => setAsk(e.target.value)} placeholder="A next step, another case, or why a step holds." />
        </Field>
        <div>
          <Button type="submit" disabled={working || !ask.trim()} loading={working}>
            Ask
          </Button>
        </div>
      </form>
    </>
  )
}
