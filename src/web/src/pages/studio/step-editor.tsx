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
import { functionsQuery, opLabels, stepLabels, toolsQuery, triggerLabels, type FlowField, type FlowStep, type FlowTrigger, type TriggerKind } from './studio-api'

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

/** A step's settings, by its kind. */
export function StepEditor({ step, values, onChange, onRemove }: { step: FlowStep; values: { label: string; value: string }[]; onChange: (s: FlowStep) => void; onRemove: () => void }) {
  const uid = useId()
  const tools = useQuery(toolsQuery)
  const functions = useQuery({ ...functionsQuery(step.tool ?? ''), enabled: step.kind === 'tool' && !!step.tool })
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
        <fieldset className="grid gap-1.5">
          <legend className="mb-1 text-sm font-medium">Events</legend>
          {[['merge_request', 'A merge request opens or changes'], ['pipeline_failed', 'A pipeline fails'], ['issue', 'An issue opens or changes']].map(([k, label]) => (
            <div key={k} className="flex items-center gap-2 text-sm">
              <Checkbox id={`${uid}-${k}`} checked={trigger.events?.includes(k!) ?? false} onCheckedChange={(on) => set({ events: on ? [...(trigger.events ?? []), k!] : (trigger.events ?? []).filter((x) => x !== k) })} />
              <label htmlFor={`${uid}-${k}`}>{label}</label>
            </div>
          ))}
        </fieldset>
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
