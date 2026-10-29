import { useQuery } from '@tanstack/react-query'
import { Braces, Plus, Trash2, X } from 'lucide-react'
import { useId, useRef } from 'react'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { listQuery } from '../chat/api'
import { sourcesQuery } from '../data/data-api'
import { functionsQuery, gitlabEvents, opLabels, stepLabels, toolsQuery, triggerLabels, type FlowField, type FlowStep, type FlowTrigger, type TriggerKind } from './studio-api'

/** A field whose text may hold values of the run ({{s1.text}}): a menu puts one in where the cursor is. */
function Template({ label, value, onChange, values, rows = 3, hint }: { label: string; value: string; onChange: (v: string) => void; values: { label: string; value: string }[]; rows?: number; hint?: string }) {
  const area = useRef<HTMLTextAreaElement>(null)
  const put = (v: string) => {
    const el = area.current
    const at = el?.selectionStart ?? value.length
    const end = el?.selectionEnd ?? value.length
    onChange(value.slice(0, at) + v + value.slice(end))
    requestAnimationFrame(() => {
      el?.focus()
      el?.setSelectionRange(at + v.length, at + v.length)
    })
  }
  return (
    <Field label={label} hint={hint}>
      <div className="relative">
        <Textarea ref={area} rows={rows} value={value} onChange={(e) => onChange(e.target.value)} className="pr-9 font-mono text-xs" spellCheck={false} />
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button type="button" variant="ghost" size="icon-sm" className="absolute top-1 right-1" aria-label={`Put a value in ${label}`}>
              <Braces />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end" className="max-h-72 overflow-y-auto">
            {values.map((v) => (
              <DropdownMenuItem key={v.value} onSelect={() => put(v.value)}>
                <span className="flex-1">{v.label}</span>
                <code className="text-[0.7rem] text-muted-foreground">{v.value}</code>
              </DropdownMenuItem>
            ))}
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
    </Field>
  )
}

function Rooms({ value, onChange }: { value: string | null | undefined; onChange: (v: string) => void }) {
  const rooms = useQuery({ ...listQuery(''), select: (all) => all.filter((c) => c.isRoom) })
  return (
    <Field label="Room" hint={rooms.data?.length === 0 ? 'You are in no room yet.' : 'A room you write in.'}>
      <Select value={value ?? ''} onValueChange={onChange}>
        <SelectTrigger>
          <SelectValue placeholder="Choose a room" />
        </SelectTrigger>
        <SelectContent>
          {(rooms.data ?? []).map((r) => (
            <SelectItem key={r.id} value={r.id}>
              {r.title}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </Field>
  )
}

/** A list of names and their values (an http step's headers), each value a template. */
function Pairs({ label, value, onChange, values }: { label: string; value: Record<string, string>; onChange: (v: Record<string, string>) => void; values: { label: string; value: string }[] }) {
  const entries = Object.entries(value)
  const rename = (from: string, to: string) => onChange(Object.fromEntries(entries.map(([k, v]) => [k === from ? to : k, v])))
  return (
    <fieldset className="grid gap-2">
      <legend className="mb-1 text-sm font-medium">{label}</legend>
      {entries.map(([name, v], i) => (
        <div key={i} className="grid gap-1 rounded-lg border p-2">
          <div className="flex gap-2">
            <Input aria-label="Header name" value={name} placeholder="Authorization" className="font-mono text-xs" onChange={(e) => rename(name, e.target.value)} />
            <Button type="button" variant="ghost" size="icon-sm" aria-label={`Take out ${name}`} onClick={() => onChange(Object.fromEntries(entries.filter(([k]) => k !== name)))}>
              <X />
            </Button>
          </div>
          <Template label={`${name || 'Header'}'s value`} value={v} rows={1} values={values} onChange={(nv) => onChange({ ...value, [name]: nv })} />
        </div>
      ))}
      <Button type="button" size="sm" variant="outline" className="w-fit" onClick={() => onChange({ ...value, [`X-Header-${entries.length + 1}`]: '' })}>
        <Plus /> A header
      </Button>
    </fieldset>
  )
}

/** A step's settings, by its kind. */
export function StepEditor({ step, values, onChange, onRemove }: { step: FlowStep; values: { label: string; value: string }[]; onChange: (s: FlowStep) => void; onRemove: () => void }) {
  const uid = useId()
  const tools = useQuery(toolsQuery)
  const functions = useQuery({ ...functionsQuery(step.tool ?? ''), enabled: step.kind === 'tool' && !!step.tool })
  const sources = useQuery({ ...sourcesQuery, enabled: step.kind === 'data' })
  const set = (change: Partial<FlowStep>) => onChange({ ...step, ...change })
  const chosen = functions.data?.find((f) => f.name === step.function)
  return (
    <div className="grid gap-3">
      <div className="flex items-center gap-2">
        <h3 className="flex-1 text-sm font-semibold">{stepLabels[step.kind]}</h3>
        <code className="text-xs text-muted-foreground">{`{{${step.id}.text}}`}</code>
        <Button variant="ghost" size="icon-sm" aria-label="Take this step out" onClick={onRemove}>
          <Trash2 />
        </Button>
      </div>
      <Field label="Name">
        <Input value={step.name ?? ''} maxLength={100} placeholder={stepLabels[step.kind]} onChange={(e) => set({ name: e.target.value })} />
      </Field>
      {(step.kind === 'ask' || step.kind === 'agent') && (
        <>
          <Template label="Prompt" value={step.prompt ?? ''} onChange={(v) => set({ prompt: v })} values={values} rows={5} />
          {step.kind === 'ask' && (
            <>
              <Template label="Instructions (optional)" value={step.system ?? ''} onChange={(v) => set({ system: v })} values={values} rows={2} />
              <div className="flex items-center gap-2 text-sm">
                <Switch id={`${uid}-json`} checked={!!step.json} onCheckedChange={(on) => set({ json: on })} />
                <label htmlFor={`${uid}-json`}>Answer as JSON (read its parts as {`{{${step.id}.json.…}}`})</label>
              </div>
              <div className="flex items-center gap-2 text-sm">
                <Switch id={`${uid}-small`} checked={step.model === 'small'} onCheckedChange={(on) => set({ model: on ? 'small' : null })} />
                <label htmlFor={`${uid}-small`}>The model for small steps (quicker, cheaper)</label>
              </div>
            </>
          )}
          {step.kind === 'agent' && (
            <fieldset className="grid gap-1.5">
              <legend className="mb-1 text-sm font-medium">Its tools</legend>
              <div className="grid max-h-48 gap-1 overflow-y-auto rounded-lg border p-2">
                {(tools.data ?? []).map((t) => (
                  <div key={t.id} className="flex items-center gap-2 text-sm">
                    <Checkbox id={`${uid}-${t.id}`} checked={step.tools?.includes(t.id) ?? false} onCheckedChange={(on) => set({ tools: on ? [...(step.tools ?? []), t.id] : (step.tools ?? []).filter((x) => x !== t.id) })} />
                    <label htmlFor={`${uid}-${t.id}`}>{t.title}</label>
                  </div>
                ))}
              </div>
            </fieldset>
          )}
        </>
      )}
      {step.kind === 'tool' && (
        <>
          <Field label="Tool">
            <Select value={step.tool ?? ''} onValueChange={(v) => set({ tool: v, function: null, arguments: {} })}>
              <SelectTrigger>
                <SelectValue placeholder="Choose a tool" />
              </SelectTrigger>
              <SelectContent>
                {(tools.data ?? []).map((t) => (
                  <SelectItem key={t.id} value={t.id}>
                    {t.title}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
          {step.tool && (
            <Field label="Function" hint={chosen?.description ?? undefined}>
              <Select value={step.function ?? ''} onValueChange={(v) => set({ function: v, arguments: Object.fromEntries((functions.data?.find((f) => f.name === v)?.parameters ?? []).map((p) => [p.name, ''])) })}>
                <SelectTrigger>
                  <SelectValue placeholder={functions.isPending ? 'Reading its functions…' : 'Choose a function'} />
                </SelectTrigger>
                <SelectContent>
                  {(functions.data ?? []).map((f) => (
                    <SelectItem key={f.name} value={f.name}>
                      {f.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
          )}
          {Object.entries(step.arguments ?? {}).map(([name, value]) => (
            <Template
              key={name}
              label={name}
              hint={chosen?.parameters?.find((p) => p.name === name)?.description ?? undefined}
              value={value}
              rows={1}
              values={values}
              onChange={(v) => set({ arguments: { ...step.arguments, [name]: v } })}
            />
          ))}
        </>
      )}
      {step.kind === 'condition' && (
        <>
          <Template label="If this" value={step.left ?? ''} onChange={(v) => set({ left: v })} values={values} rows={1} />
          <Field label="Test">
            <Select value={step.op ?? 'contains'} onValueChange={(v) => set({ op: v })}>
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {Object.entries(opLabels).map(([k, label]) => (
                  <SelectItem key={k} value={k}>
                    {label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
          {!['empty', 'not_empty'].includes(step.op ?? '') && <Template label="This" value={step.right ?? ''} onChange={(v) => set({ right: v })} values={values} rows={1} />}
          <p className="text-xs text-muted-foreground">Words are compared without case; "is more than" and "is less than" compare numbers. Its Yes and No steps are under it on the canvas.</p>
        </>
      )}
      {step.kind === 'loop' && (
        <>
          <Template label="Go over" value={step.over ?? ''} onChange={(v) => set({ over: v })} values={values} rows={2} hint="A JSON list (a step's JSON), or lines: one item a line." />
          <Field label="At most">
            <Input type="number" min={1} max={50} value={step.most ?? 10} onChange={(e) => set({ most: Number(e.target.value) || 10 })} />
          </Field>
        </>
      )}
      {step.kind === 'approval' && <Template label="Ask" value={step.question ?? ''} onChange={(v) => set({ question: v })} values={values} hint="The run waits for your answer (a notification tells you); No ends it." />}
      {step.kind === 'room' && (
        <>
          <Rooms value={step.roomId} onChange={(v) => set({ roomId: v })} />
          <Template label="Say" value={step.text ?? ''} onChange={(v) => set({ text: v })} values={values} />
        </>
      )}
      {step.kind === 'email' && (
        <>
          <Template label="Subject" value={step.subject ?? ''} onChange={(v) => set({ subject: v })} values={values} rows={1} />
          <Template label="Text" value={step.text ?? ''} onChange={(v) => set({ text: v })} values={values} hint="To you, at your account's address." />
        </>
      )}
      {step.kind === 'file' && (
        <>
          <Template label="File name" value={step.fileName ?? ''} onChange={(v) => set({ fileName: v })} values={values} rows={1} />
          <Template label="Text" value={step.text ?? ''} onChange={(v) => set({ text: v })} values={values} rows={5} />
        </>
      )}
      {(step.kind === 'text' || step.kind === 'comment') && (
        <Template label="Text" value={step.text ?? ''} onChange={(v) => set({ text: v })} values={values} rows={5} hint={step.kind === 'comment' ? 'On the merge request or issue that started the run (a GitLab trigger).' : undefined} />
      )}
      {step.kind === 'text' && (
        <div className="flex items-center gap-2 text-sm">
          <Switch id={`${uid}-tjson`} checked={!!step.json} onCheckedChange={(on) => set({ json: on })} />
          <label htmlFor={`${uid}-tjson`}>It is JSON (read its parts as {`{{${step.id}.json.…}}`})</label>
        </div>
      )}
      {step.kind === 'knowledge' && (
        <Template label="Look for" value={step.prompt ?? ''} onChange={(v) => set({ prompt: v })} values={values} rows={2} hint="The company's documents you may read, nearest these words first: titles, links and passages." />
      )}
      {step.kind === 'data' && (
        <>
          <Field label="Data source" hint={sources.data?.length === 0 ? 'No data source is open to you.' : 'One you may ask (Workspace → Data).'}>
            <Select value={step.sourceId ?? ''} onValueChange={(v) => set({ sourceId: v })}>
              <SelectTrigger>
                <SelectValue placeholder="Choose a data source" />
              </SelectTrigger>
              <SelectContent>
                {(sources.data ?? []).map((s) => (
                  <SelectItem key={s.id} value={s.id}>
                    {s.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
          <Template label="Question" value={step.prompt ?? ''} onChange={(v) => set({ prompt: v })} values={values} rows={2} hint="In words: the model writes the SQL. Or give the SQL below, run as it is." />
          <Template label="SQL (optional)" value={step.sql ?? ''} onChange={(v) => set({ sql: v })} values={values} rows={3} hint={`Rows come back as {{${step.id}.json.rows}}, each by its columns' names.`} />
        </>
      )}
      {step.kind === 'http' && (
        <>
          <div className="grid gap-3 sm:grid-cols-[7rem_minmax(0,1fr)]">
            <Field label="Method">
              <Select value={step.method ?? 'GET'} onValueChange={(v) => set({ method: v as FlowStep['method'] })}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {['GET', 'POST', 'PUT', 'PATCH', 'DELETE'].map((m) => (
                    <SelectItem key={m} value={m}>
                      {m}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
            <Template label="Address" value={step.url ?? ''} onChange={(v) => set({ url: v })} values={values} rows={1} />
          </div>
          <Pairs label="Headers" value={step.arguments ?? {}} onChange={(v) => set({ arguments: v })} values={values} />
          {step.method && step.method !== 'GET' && <Template label="Body" value={step.text ?? ''} onChange={(v) => set({ text: v })} values={values} rows={4} hint="JSON goes as application/json, other text as text/plain." />}
          <p className="text-xs text-muted-foreground">Public addresses only (never the stack's or the office's), the chat's allowed sites, 30 seconds. Headers are kept in the workflow as written: whoever may change it reads them.</p>
        </>
      )}
      {step.kind === 'code' && (
        <Template label="Python" value={step.text ?? ''} onChange={(v) => set({ text: v })} values={values} rows={8} hint="In the sandbox, with no network, at most 2 minutes. The run's values are in input.json beside it; what it prints is its answer (JSON too)." />
      )}
      {step.kind === 'wait' && (
        <Field label="Minutes" hint="The run waits, then goes on (at most a week).">
          <Input type="number" min={1} max={10080} value={step.minutes ?? 60} onChange={(e) => set({ minutes: Math.max(1, Math.min(10080, Number(e.target.value) || 1)) })} />
        </Field>
      )}
      {step.kind === 'stop' && (
        <>
          <Field label="It ends">
            <Select value={step.outcome ?? 'done'} onValueChange={(v) => set({ outcome: v as FlowStep['outcome'] })}>
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="done">Done</SelectItem>
                <SelectItem value="failed">Failed (you are told)</SelectItem>
              </SelectContent>
            </Select>
          </Field>
          <Template label="Why (optional)" value={step.text ?? ''} onChange={(v) => set({ text: v })} values={values} rows={2} />
        </>
      )}
      {step.kind === 'notify' && (
        <>
          <Template label="Title" value={step.subject ?? ''} onChange={(v) => set({ subject: v })} values={values} rows={1} />
          <Template label="Text" value={step.text ?? ''} onChange={(v) => set({ text: v })} values={values} hint="To you, in the bell (and on your phone, where you turned that on)." />
        </>
      )}
      {step.kind === 'gitlab' && (
        <>
          <Field label="Action">
            <Select value={step.action ?? 'comment'} onValueChange={(v) => set({ action: v as FlowStep['action'] })}>
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="comment">Comment</SelectItem>
                <SelectItem value="label">Add labels</SelectItem>
                <SelectItem value="issue">Open an issue</SelectItem>
              </SelectContent>
            </Select>
          </Field>
          <div className="grid gap-3 sm:grid-cols-2">
            <Template label="Project (optional)" value={step.project ?? ''} onChange={(v) => set({ project: v })} values={values} rows={1} hint="Its path; empty: the event's." />
            {step.action !== 'issue' && <Template label="Merge request or issue (optional)" value={step.target ?? ''} onChange={(v) => set({ target: v })} values={values} rows={1} hint="!12 or #34; empty: the event's." />}
          </div>
          {step.action === 'issue' && <Template label="Title" value={step.subject ?? ''} onChange={(v) => set({ subject: v })} values={values} rows={1} />}
          {(step.action === 'label' || step.action === 'issue') && <Template label="Labels" value={step.labels ?? ''} onChange={(v) => set({ labels: v })} values={values} rows={1} hint="Comma separated." />}
          {step.action !== 'label' && <Template label={step.action === 'issue' ? 'Description' : 'Comment'} value={step.text ?? ''} onChange={(v) => set({ text: v })} values={values} rows={5} />}
          <p className="text-xs text-muted-foreground">With the app's GitLab bot (Settings → Scheduled tasks); each action is in the audit log.</p>
        </>
      )}
    </div>
  )
}

const zones = typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : ['UTC']

/** The trigger's settings. */
export function TriggerEditor({ trigger, onChange, hook, onSecret }: { trigger: FlowTrigger; onChange: (t: FlowTrigger) => void; hook?: string; onSecret?: () => void }) {
  const uid = useId()
  const set = (change: Partial<FlowTrigger>) => onChange({ ...trigger, ...change })
  const fields = trigger.fields ?? []
  const setField = (i: number, change: Partial<FlowField>) => set({ fields: fields.map((f, j) => (j === i ? { ...f, ...change } : f)) })
  return (
    <div className="grid gap-3">
      <h3 className="text-sm font-semibold">What starts it</h3>
      <Field label="Trigger">
        <Select value={trigger.kind} onValueChange={(v) => set({ kind: v as TriggerKind })}>
          <SelectTrigger>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {Object.entries(triggerLabels).map(([k, label]) => (
              <SelectItem key={k} value={k}>
                {label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </Field>
      {trigger.kind === 'schedule' && (
        <>
          <Field label="When (cron)" hint="Minute, hour, day of month, month, day of week: 0 8 * * 1-5 is weekdays at 8.">
            <Input value={trigger.cron ?? ''} onChange={(e) => set({ cron: e.target.value })} className="font-mono" placeholder="0 8 * * 1-5" />
          </Field>
          <Field label="Time zone">
            <Select value={trigger.timeZone ?? 'UTC'} onValueChange={(v) => set({ timeZone: v })}>
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent className="max-h-72">
                {['UTC', ...zones.filter((z) => z !== 'UTC')].map((z) => (
                  <SelectItem key={z} value={z}>
                    {z}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
        </>
      )}
      {trigger.kind === 'gitlab' && (
        <>
          <fieldset className="grid gap-1.5">
            <legend className="mb-1 text-sm font-medium">Events</legend>
            <div className="grid gap-1.5 sm:grid-cols-2">
              {gitlabEvents.map(([k, label]) => (
                <div key={k} className="flex items-center gap-2 text-sm">
                  <Checkbox id={`${uid}-${k}`} checked={trigger.events?.includes(k) ?? false} onCheckedChange={(on) => set({ events: on ? [...(trigger.events ?? []), k] : (trigger.events ?? []).filter((x) => x !== k) })} />
                  <label htmlFor={`${uid}-${k}`}>{label}</label>
                </div>
              ))}
            </div>
          </fieldset>
          <Field label="Branches (optional)" hint="Globs, comma separated: main, release/*. A merge request's target, a push's, a pipeline's, a job's or a deployment's branch. Empty: all.">
            <Input value={trigger.branches ?? ''} maxLength={500} onChange={(e) => set({ branches: e.target.value })} className="font-mono text-xs" placeholder="main, release/*" />
          </Field>
          <Field label="Labels (optional)" hint="A merge request or issue with one of these, comma separated. Empty: any.">
            <Input value={trigger.labels ?? ''} maxLength={500} onChange={(e) => set({ labels: e.target.value })} placeholder="needs-review, bug" />
          </Field>
          {trigger.events?.includes('comment') && (
            <Field label="A comment holds (optional)" hint="Words, comma separated, one of which a comment must hold: a mention such as @arena. The app's own comments never start it.">
              <Input value={trigger.keyword ?? ''} maxLength={200} onChange={(e) => set({ keyword: e.target.value })} placeholder="@arena" />
            </Field>
          )}
        </>
      )}
      {(trigger.kind === 'webhook' || trigger.kind === 'gitlab') && hook && (
        <div className="grid gap-1.5 rounded-lg border bg-muted/40 p-3 text-xs">
          <span className="font-medium">Its address</span>
          <code className="break-all">{hook}</code>
          <span className="text-muted-foreground">{trigger.kind === 'gitlab' ? "GitLab's webhook, its secret token the secret." : 'Posted with the secret in the X-Hook-Secret header.'}</span>
          {onSecret && (
            <Button type="button" size="sm" variant="outline" className="w-fit" onClick={onSecret}>
              A new secret
            </Button>
          )}
        </div>
      )}
      {trigger.kind === 'room' && (
        <>
          <Rooms value={trigger.roomId} onChange={(v) => set({ roomId: v })} />
          <Field label="The word a message starts with" hint="A message in the room that starts with it runs the workflow, the rest of it the input.">
            <Input value={trigger.keyword ?? ''} maxLength={40} onChange={(e) => set({ keyword: e.target.value })} placeholder="#triage" />
          </Field>
        </>
      )}
      {(trigger.kind === 'form' || trigger.kind === 'manual') && (
        <fieldset className="grid gap-2">
          <legend className="mb-1 text-sm font-medium">{trigger.kind === 'form' ? "The form's fields" : 'Fields to fill when run'}</legend>
          {fields.map((f, i) => (
            <div key={i} className="grid gap-2 rounded-lg border p-2">
              <div className="flex gap-2">
                <Input aria-label="Its label" value={f.label} placeholder="Label" onChange={(e) => setField(i, { label: e.target.value })} />
                <Input aria-label="Its name" value={f.name} placeholder="name" className="w-28 font-mono text-xs" onChange={(e) => setField(i, { name: e.target.value.replace(/[^A-Za-z0-9_]/g, '') })} />
                <Button type="button" variant="ghost" size="icon-sm" aria-label={`Take out ${f.label || f.name}`} onClick={() => set({ fields: fields.filter((_, j) => j !== i) })}>
                  <X />
                </Button>
              </div>
              <div className="flex flex-wrap items-center gap-2">
                <Select value={f.kind} onValueChange={(v) => setField(i, { kind: v as FlowField['kind'] })}>
                  <SelectTrigger className="w-32" aria-label="Its kind">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="text">A line</SelectItem>
                    <SelectItem value="long">Paragraphs</SelectItem>
                    <SelectItem value="choice">A choice</SelectItem>
                  </SelectContent>
                </Select>
                <span className="flex items-center gap-1.5 text-xs">
                  <Checkbox id={`${uid}-needed-${i}`} checked={!!f.required} onCheckedChange={(on) => setField(i, { required: !!on })} />
                  <label htmlFor={`${uid}-needed-${i}`}>Needed</label>
                </span>
              </div>
              {f.kind === 'choice' && (
                <Input aria-label="Its choices" value={(f.choices ?? []).join(', ')} placeholder="Choices, with commas" onChange={(e) => setField(i, { choices: e.target.value.split(',').map((c) => c.trim()).filter(Boolean) })} />
              )}
            </div>
          ))}
          <Button type="button" size="sm" variant="outline" className="w-fit" onClick={() => set({ fields: [...fields, { name: `field${fields.length + 1}`, label: '', kind: 'text' }] })}>
            <Plus /> A field
          </Button>
        </fieldset>
      )}
    </div>
  )
}
