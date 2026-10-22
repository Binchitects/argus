import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, ChevronLeft, ChevronRight, Copy, ExternalLink, Play, Save, Settings2, Square, Trash2 } from 'lucide-react'
import { useId, useState } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { FlowCanvas } from './flow-canvas'
import { allSteps, insertStep, lastByStep, newStep, removeStep, stepAt, updateStep, valuesBefore, type Loc } from './flow-edit'
import { StepEditor, TriggerEditor } from './step-editor'
import {
  answerApproval, newSecret, removeWorkflow, runQuery, runsQuery, runWorkflow, saveWorkflow, stepLabels, stopRun, triggerLabels, workflowQuery,
  type Flow, type FlowField, type Reach, type Run, type RunStatus, type SharedWorkflow, type Workflow,
} from './studio-api'

const statusTone: Record<RunStatus, 'secondary' | 'outline' | 'destructive'> = { running: 'outline', waiting: 'outline', done: 'secondary', failed: 'destructive', stopped: 'outline' }
const statusLabels: Record<RunStatus, string> = { running: 'Running', waiting: 'Waiting for you', done: 'Done', failed: 'Failed', stopped: 'Stopped' }

const took = (from: string, to: string | null) => {
  if (!to) return ''
  const s = (new Date(to).getTime() - new Date(from).getTime()) / 1000
  return s < 60 ? `${s.toFixed(1)} s` : `${Math.round(s / 60)} min`
}

/** /studio/:id: the workflow on its canvas (its owner's), or its form (someone it is shared with). */
export function WorkflowPage() {
  const { id = '' } = useParams()
  const loaded = useQuery(workflowQuery(id))
  if (loaded.isPending) return <PageSkeleton />
  if (loaded.error) return <QueryError error={loaded.error} retry={() => loaded.refetch()} />
  return loaded.data.mine ? <Editor key={id} workflow={loaded.data} /> : <SharedForm workflow={loaded.data} />
}

/** The fields to fill to run it: its words, and its form's fields. */
function RunForm({ fields, onRun, running }: { fields: FlowField[]; onRun: (body: { text: string; fields: Record<string, string> }) => void; running: boolean }) {
  const [text, setText] = useState('')
  const [values, setValues] = useState<Record<string, string>>({})
  return (
    <form
      className="grid gap-3"
      onSubmit={(e) => {
        e.preventDefault()
        onRun({ text, fields: values })
      }}
    >
      {fields.map((f) => (
        <Field key={f.name} label={`${f.label || f.name}${f.required ? '' : ' (optional)'}`}>
          {f.kind === 'choice' ? (
            <Select value={values[f.name] ?? ''} onValueChange={(v) => setValues({ ...values, [f.name]: v })}>
              <SelectTrigger>
                <SelectValue placeholder="Choose" />
              </SelectTrigger>
              <SelectContent>
                {(f.choices ?? []).map((c) => (
                  <SelectItem key={c} value={c}>
                    {c}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          ) : f.kind === 'long' ? (
            <Textarea rows={4} value={values[f.name] ?? ''} onChange={(e) => setValues({ ...values, [f.name]: e.target.value })} />
          ) : (
            <Input value={values[f.name] ?? ''} onChange={(e) => setValues({ ...values, [f.name]: e.target.value })} />
          )}
        </Field>
      ))}
      {fields.length === 0 && (
        <Field label="Words to start from (optional)">
          <Textarea rows={3} value={text} onChange={(e) => setText(e.target.value)} placeholder="The input: {{input.text}} in its steps" />
        </Field>
      )}
      <Button type="submit" loading={running} className="w-fit" disabled={fields.some((f) => f.required && !values[f.name]?.trim())}>
        <Play /> Run it
      </Button>
    </form>
  )
}

/** A workflow shared with the person: its form; it runs as its owner, and they see how it went. */
function SharedForm({ workflow }: { workflow: SharedWorkflow }) {
  const [run, setRun] = useState<string | null>(null)
  const start = useMutation({ mutationFn: (body: { text: string; fields: Record<string, string> }) => runWorkflow(workflow.id, body), onSuccess: (r) => setRun(r.id), onError: (e) => toast.error(errorMessage(e)) })
  return (
    <div className="mx-auto grid max-w-2xl gap-4">
      <Button variant="ghost" size="sm" className="w-fit" asChild>
        <Link to="/studio">
          <ArrowLeft /> Agent Studio
        </Link>
      </Button>
      <div>
        <h1 className="text-xl font-semibold">{workflow.name}</h1>
        <p className="text-sm text-muted-foreground">
          By {workflow.owner ?? 'someone'}. {workflow.description} It runs as them: they read what it does.
        </p>
      </div>
      {!workflow.enabled && <Alert>It is off for now.</Alert>}
      <RunForm fields={workflow.fields} onRun={(b) => start.mutate(b)} running={start.isPending} />
      {run && <RunView runId={run} owner={false} />}
    </div>
  )
}

function Editor({ workflow }: { workflow: Workflow }) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const confirm = useConfirm()
  const [params, setParams] = useSearchParams()
  const [name, setName] = useState(workflow.name)
  const [description, setDescription] = useState(workflow.description)
  const [flow, setFlow] = useState<Flow>(workflow.flow)
  const [reach, setReach] = useState<Reach>(workflow.reach)
  const [groupIds, setGroupIds] = useState(workflow.groupIds)
  const [dirty, setDirty] = useState(false)
  const [selected, setSelected] = useState<Loc | 'trigger' | null>('trigger')
  const [dialog, setDialog] = useState<'run' | 'settings' | null>(null)
  const [secret, setSecret] = useState<string | null>(null)
  const tab = params.get('run') ? 'runs' : (params.get('tab') ?? 'flow')
  const change = (next: Flow) => {
    setFlow(next)
    setDirty(true)
  }
  const saved = (w: Workflow, s: string | null) => {
    queryClient.setQueryData(workflowQuery(w.id).queryKey, w)
    void queryClient.invalidateQueries({ queryKey: ['studio'], exact: true })
    setDirty(false)
    if (s) setSecret(s)
  }
  const save = useMutation({
    mutationFn: (enabled?: boolean) => saveWorkflow(workflow.id, { name: name.trim(), description: description.trim(), flow, reach, groupIds: reach === 'groups' ? groupIds : [], enabled }),
    onSuccess: (r) => saved(r.workflow, r.secret),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const run = useMutation({
    mutationFn: (body: { text: string; fields: Record<string, string> }) => runWorkflow(workflow.id, body),
    onSuccess: (r) => {
      setDialog(null)
      setParams({ run: r.id })
      void queryClient.invalidateQueries({ queryKey: runsQuery(workflow.id).queryKey })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const current = workflow
  const fields = (flow.trigger.fields ?? []).map((f) => f.name)
  const step = selected && selected !== 'trigger' ? stepAt(flow.steps, selected) : undefined
  return (
    <div className="grid gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <Button variant="ghost" size="icon-sm" asChild>
          <Link to="/studio" aria-label="Agent Studio">
            <ArrowLeft />
          </Link>
        </Button>
        <Input
          aria-label="The workflow's name"
          value={name}
          maxLength={100}
          onChange={(e) => {
            setName(e.target.value)
            setDirty(true)
          }}
          className="h-9 max-w-xs text-base font-semibold"
        />
        <label className="flex items-center gap-2 text-sm">
          <Switch checked={current.enabled} onCheckedChange={(on) => save.mutate(on)} aria-label="On" />
          {current.enabled ? 'On' : 'Off'}
        </label>
        {current.nextRunAt && current.enabled && <span className="text-xs text-muted-foreground">Next: {new Date(current.nextRunAt).toLocaleString()}</span>}
        <span className="flex-1" />
        <Button variant="outline" onClick={() => setDialog('settings')}>
          <Settings2 /> Settings
        </Button>
        <Button variant="outline" onClick={() => save.mutate(undefined)} loading={save.isPending && save.variables === undefined} disabled={!dirty || !name.trim()}>
          <Save /> {dirty ? 'Save' : 'Saved'}
        </Button>
        <Button onClick={() => (dirty ? toast.error('Save it first.') : setDialog('run'))}>
          <Play /> Run now
        </Button>
      </div>
      <Tabs value={tab} onValueChange={(v) => setParams(v === 'flow' ? {} : { tab: v })}>
        <TabsList>
          <TabsTrigger value="flow">Flow</TabsTrigger>
          <TabsTrigger value="runs">Runs</TabsTrigger>
        </TabsList>
        <TabsContent value="flow">
          <div className="grid gap-4 lg:grid-cols-[1fr_24rem]">
            <div className="min-h-[32rem] overflow-auto rounded-xl border bg-[radial-gradient(var(--border)_1px,transparent_1px)] [background-size:16px_16px]">
              <FlowCanvas
                flow={flow}
                selected={selected}
                onSelect={setSelected}
                onInsert={(lane, index, kind) => {
                  const made = newStep(kind, flow.steps)
                  const next = { ...flow, steps: insertStep(flow.steps, lane, index, made) }
                  change(next)
                  setSelected(allSteps(next.steps).find((s) => s.step.id === made.id)!.loc)
                }}
              />
            </div>
            <aside className="rounded-xl border bg-card p-4" aria-label="Settings of what is chosen">
              {selected === 'trigger' ? (
                <TriggerEditor
                  trigger={flow.trigger}
                  onChange={(t) => change({ ...flow, trigger: t })}
                  hook={current.hook}
                  onSecret={async () => {
                    if (await confirm({ title: 'A new secret?', description: 'The one in use stops working: give the new one where the events come from.', confirm: 'Make a new one' })) {
                      setSecret((await newSecret(current.id)).secret)
                    }
                  }}
                />
              ) : step && selected ? (
                <StepEditor
                  key={step.id}
                  step={step}
                  values={valuesBefore(flow.steps, selected, fields)}
                  onChange={(s) => change({ ...flow, steps: updateStep(flow.steps, selected, () => s) })}
                  onRemove={() => {
                    change({ ...flow, steps: removeStep(flow.steps, selected) })
                    setSelected('trigger')
                  }}
                />
              ) : (
                <p className="text-sm text-muted-foreground">Choose the trigger or a step to change it; press + between steps to add one.</p>
              )}
            </aside>
          </div>
        </TabsContent>
        <TabsContent value="runs">
          {params.get('run') ? (
            <div className="grid gap-3">
              <Button variant="ghost" size="sm" className="w-fit" onClick={() => setParams({ tab: 'runs' })}>
                <ArrowLeft /> All runs
              </Button>
              <RunView runId={params.get('run')!} owner />
            </div>
          ) : (
            <RunsList id={current.id} onOpen={(r) => setParams({ run: r })} />
          )}
        </TabsContent>
      </Tabs>
      <Dialog open={dialog !== null} onOpenChange={(o) => !o && setDialog(null)}>
        <DialogContent className="sm:max-w-lg">
          {dialog === 'run' && (
            <>
              <DialogHeader>
                <DialogTitle>Run {current.name} now</DialogTitle>
                <DialogDescription>It runs as you, in the background; its steps show as they go.</DialogDescription>
              </DialogHeader>
              <RunForm fields={flow.trigger.fields ?? []} onRun={(b) => run.mutate(b)} running={run.isPending} />
            </>
          )}
          {dialog === 'settings' && (
            <Settings
              description={description}
              reach={reach}
              groupIds={groupIds}
              onChange={(d, r, g) => {
                setDescription(d)
                setReach(r)
                setGroupIds(g)
                setDirty(true)
                setDialog(null)
              }}
              onDelete={async () => {
                if (await confirm({ title: `Delete ${current.name}?`, description: 'It and every run of it go.', confirm: 'Delete', destructive: true })) {
                  await removeWorkflow(current.id).catch((e: unknown) => toast.error(errorMessage(e)))
                  void queryClient.invalidateQueries({ queryKey: ['studio'] })
                  navigate('/studio')
                }
              }}
            />
          )}
        </DialogContent>
      </Dialog>
      <Dialog open={secret !== null} onOpenChange={(o) => !o && setSecret(null)}>
        <DialogContent className="sm:max-w-md">
          <DialogHeader>
            <DialogTitle>Its webhook's secret</DialogTitle>
            <DialogDescription>Shown this once: give it where the events come from ({flow.trigger.kind === 'gitlab' ? "GitLab's secret token" : 'the X-Hook-Secret header'}).</DialogDescription>
          </DialogHeader>
          <div className="flex items-center gap-2">
            <Input readOnly value={secret ?? ''} aria-label="The secret" className="font-mono text-xs" onFocus={(e) => e.target.select()} />
            <Button size="icon-sm" variant="ghost" aria-label="Copy the secret" onClick={() => void navigator.clipboard.writeText(secret ?? '').then(() => toast.success('Copied'))}>
              <Copy />
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  )
}

/** Its words, who may run it (by hand or its form, as its owner), and deleting it. */
function Settings({ description, reach, groupIds, onChange, onDelete }: { description: string; reach: Reach; groupIds: string[]; onChange: (d: string, r: Reach, g: string[]) => void; onDelete: () => void }) {
  const id = useId()
  const [words, setWords] = useState(description)
  const [who, setWho] = useState<Reach>(reach)
  const [groups, setGroups] = useState(groupIds)
  const mine = useQuery({ queryKey: ['sharing', 'groups'], queryFn: ({ signal }) => api<{ id: string; name: string }[]>('/api/sharing/groups', { signal }), enabled: who === 'groups' })
  return (
    <>
      <DialogHeader>
        <DialogTitle>Settings</DialogTitle>
      </DialogHeader>
      <Field label="What it does">
        <Textarea rows={2} maxLength={500} value={words} onChange={(e) => setWords(e.target.value)} />
      </Field>
      <Segmented label="Who may run it" value={who} onChange={setWho} options={[{ value: 'private', label: 'Only me' }, { value: 'groups', label: 'Some groups' }, { value: 'company', label: 'Everyone' }]} />
      <p className="text-xs text-muted-foreground">Others run it by hand or by its form, as you: with your tools and credit. They see how it went, not what it read.</p>
      {who === 'groups' && (
        <fieldset className="grid gap-1.5">
          <legend className="mb-1 text-sm font-medium">Groups</legend>
          {(mine.data ?? []).map((g) => (
            <div key={g.id} className="flex items-center gap-2 text-sm">
              <Checkbox id={`${id}-${g.id}`} checked={groups.includes(g.id)} onCheckedChange={(on) => setGroups(on ? [...groups, g.id] : groups.filter((x) => x !== g.id))} />
              <label htmlFor={`${id}-${g.id}`}>{g.name}</label>
            </div>
          ))}
        </fieldset>
      )}
      <DialogFooter className="sm:justify-between">
        <Button variant="ghost" className="text-destructive-ink" onClick={onDelete}>
          <Trash2 /> Delete
        </Button>
        <Button onClick={() => onChange(words, who, groups)} disabled={who === 'groups' && groups.length === 0}>
          Done
        </Button>
      </DialogFooter>
    </>
  )
}

function RunsList({ id, onOpen }: { id: string; onOpen: (run: string) => void }) {
  const runs = useQuery({ ...runsQuery(id), refetchInterval: (q) => (q.state.data?.some((r) => r.status === 'running') ? 2000 : false) })
  if (runs.data?.length === 0) return <p className="py-8 text-center text-sm text-muted-foreground">No runs yet: press Run now.</p>
  return (
    <ul className="grid gap-1.5" aria-label="Runs">
      {(runs.data ?? []).map((r) => (
        <li key={r.id}>
          <button type="button" onClick={() => onOpen(r.id)} className="flex w-full items-center gap-3 rounded-lg border bg-card px-3 py-2 text-left text-sm hover:bg-accent/40">
            <Badge variant={statusTone[r.status]}>{statusLabels[r.status]}</Badge>
            <span className="flex-1">{new Date(r.startedAt).toLocaleString()}</span>
            <span className="text-xs text-muted-foreground">
              {triggerLabels[r.trigger as keyof typeof triggerLabels] ?? r.trigger} · {r.steps} {r.steps === 1 ? 'step' : 'steps'}
              {r.finishedAt ? ` · ${took(r.startedAt, r.finishedAt)}` : ''}
            </span>
          </button>
        </li>
      ))}
    </ul>
  )
}

/**
 * A run: how it went, its steps as they went (each with what it was given and gave, for its owner), and a replay that walks
 * them one by one on the canvas. A run waiting for its owner's yes asks it here.
 */
export function RunView({ runId, owner }: { runId: string; owner: boolean }) {
  const queryClient = useQueryClient()
  const run = useQuery({ ...runQuery(runId), refetchInterval: (q) => (q.state.data?.status === 'running' || !q.state.data ? 1500 : false) })
  const [at, setAt] = useState<number | null>(null)
  const [view, setView] = useState<'canvas' | 'list'>('canvas')
  const changed = () => void queryClient.invalidateQueries({ queryKey: ['studio'] })
  const answer = useMutation({ mutationFn: (yes: boolean) => answerApproval(runId, yes), onSuccess: changed, onError: (e) => toast.error(errorMessage(e)) })
  const stop = useMutation({ mutationFn: () => stopRun(runId), onSuccess: changed, onError: (e) => toast.error(errorMessage(e)) })
  if (!run.data) return <PageSkeleton />
  const r: Run = run.data
  const steps = r.steps
  const index = at ?? steps.length - 1
  const shown = steps[index]
  const waiting = steps.find((s) => s.status === 'waiting')
  return (
    <section aria-label="The run" className="grid gap-3">
      <div className="flex flex-wrap items-center gap-2 text-sm">
        <Badge variant={statusTone[r.status]}>{statusLabels[r.status]}</Badge>
        <span className="text-muted-foreground">
          {triggerLabels[r.trigger as keyof typeof triggerLabels] ?? r.trigger} · {new Date(r.startedAt).toLocaleString()}
          {r.finishedAt ? ` · ${took(r.startedAt, r.finishedAt)}` : ''}
        </span>
        <span className="flex-1" />
        {owner && (r.status === 'running' || r.status === 'waiting') && (
          <Button size="sm" variant="outline" onClick={() => stop.mutate()} loading={stop.isPending}>
            <Square /> Stop
          </Button>
        )}
      </div>
      {r.error && <Alert variant={r.status === 'failed' ? 'destructive' : 'info'}>{r.error}</Alert>}
      {owner && waiting && r.status === 'waiting' && (
        <section className="grid gap-2 rounded-xl border border-warning/50 bg-warning/10 p-3" aria-label="It asks you">
          <p className="text-sm font-medium">It asks you</p>
          <p className="text-sm whitespace-pre-wrap">{waiting.input}</p>
          <div className="flex gap-2">
            <Button size="sm" onClick={() => answer.mutate(true)} loading={answer.isPending && answer.variables === true}>
              Yes, go on
            </Button>
            <Button size="sm" variant="outline" onClick={() => answer.mutate(false)} loading={answer.isPending && answer.variables === false}>
              No, stop it
            </Button>
          </div>
        </section>
      )}
      {owner && r.flow && <Segmented label="Shown as" value={view} onChange={setView} options={[{ value: 'canvas', label: 'Replay' }, { value: 'list', label: 'Timeline' }]} />}
      {view === 'canvas' && owner && r.flow && steps.length > 0 ? (
        <div className="grid gap-3 lg:grid-cols-[1fr_24rem]">
          <div className="max-h-[36rem] overflow-auto rounded-xl border">
            <FlowCanvas flow={r.flow} selected={null} ran={lastByStep(steps.slice(0, index + 1))} current={shown?.stepId} />
          </div>
          <div className="grid content-start gap-2 rounded-xl border bg-card p-3">
            <div className="flex items-center gap-1">
              <Button size="icon-sm" variant="outline" aria-label="The step before" disabled={index <= 0} onClick={() => setAt(Math.max(0, index - 1))}>
                <ChevronLeft />
              </Button>
              <span className="flex-1 text-center text-xs tabular-nums">
                Step {index + 1} of {steps.length}
              </span>
              <Button size="icon-sm" variant="outline" aria-label="The next step" disabled={index >= steps.length - 1} onClick={() => setAt(Math.min(steps.length - 1, index + 1))}>
                <ChevronRight />
              </Button>
            </div>
            {shown && <StepDetail step={shown} />}
          </div>
        </div>
      ) : (
        <ol className="grid gap-2" aria-label="Its steps">
          {steps.map((s) => (
            <li key={s.path}>
              <StepDetail step={s} />
            </li>
          ))}
        </ol>
      )}
    </section>
  )
}

function StepDetail({ step }: { step: Run['steps'][number] }) {
  return (
    <div className={cn('grid gap-1.5 rounded-lg border p-2 text-sm', step.status === 'failed' && 'border-destructive/50')}>
      <div className="flex items-center gap-2">
        <span className="font-medium">{step.name || stepLabels[step.kind]}</span>
        <code className="text-[0.7rem] text-muted-foreground">{step.path}</code>
        <span className="flex-1" />
        <span className="text-xs text-muted-foreground">
          {step.status}
          {step.finishedAt ? ` · ${took(step.startedAt, step.finishedAt)}` : ''}
        </span>
      </div>
      {step.input && (
        <details>
          <summary className="cursor-pointer text-xs text-muted-foreground">Given</summary>
          <pre className="mt-1 max-h-48 overflow-auto rounded bg-muted p-2 text-xs whitespace-pre-wrap">{step.input}</pre>
        </details>
      )}
      {step.output && <pre className="max-h-64 overflow-auto rounded bg-muted/50 p-2 text-xs whitespace-pre-wrap">{step.output}</pre>}
      {step.error && <p className="text-xs text-destructive-ink">{step.error}</p>}
      {step.link && (
        <a href={step.link} className="flex w-fit items-center gap-1 text-xs text-primary hover:underline" target={step.link.startsWith('/api/') ? undefined : '_self'}>
          <ExternalLink className="size-3" /> {step.link.startsWith('/chat/') ? 'Open its chat' : 'Open it'}
        </a>
      )}
    </div>
  )
}
