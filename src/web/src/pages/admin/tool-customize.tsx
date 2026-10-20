import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Plus, Trash2 } from 'lucide-react'
import { useId, useState, type ReactNode } from 'react'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { groupsQuery } from './groups-api'
import type { CustomToolRow, ToolUsage } from './tool-customize-api'

/** "120 calls this week · 3 failed · 0.4 s": a tool's use, in a line. */
export function UsageLine({ usage }: { usage: ToolUsage | undefined }) {
  if (!usage) return <p className="text-xs text-muted-foreground">No calls this week.</p>
  return (
    <p className="text-xs text-muted-foreground" title={usage.lastError?.error ?? undefined}>
      {usage.calls.toLocaleString()} call{usage.calls === 1 ? '' : 's'} this week · {usage.failed} failed · {(usage.averageMs / 1000).toFixed(1)} s on average
      {usage.lastError && <span className="text-destructive-ink"> · last error {new Date(usage.lastError.at).toLocaleDateString()}</span>}
    </p>
  )
}

/** A tick box and its words, joined for screen readers. */
function Tick({ checked, onChange, children, toggle }: { checked: boolean; onChange: (on: boolean) => void; children: ReactNode; toggle?: boolean }) {
  const id = useId()
  return (
    <div className="flex items-center gap-2 text-sm">
      {toggle ? <Switch id={id} checked={checked} onCheckedChange={onChange} /> : <Checkbox id={id} checked={checked} onCheckedChange={(on) => onChange(on === true)} />}
      <label htmlFor={id}>{children}</label>
    </div>
  )
}

interface Customization {
  id: string
  title: string
  description: string
  problem: string | null
  functions: { name: string; description: string | null; parameters: { name: string; description: string | null }[] }[]
  setting: {
    title: string | null
    description: string | null
    instructions: string | null
    replaceInstructions: boolean
    disabledFunctions: string[]
    askFirstFunctions: string[]
    callsPerDay: number | null
    functions: Record<string, { description?: string; fixed?: Record<string, unknown>; defaults?: Record<string, unknown> }>
  }
  groups: { groupId: string; name: string | null; onByDefault: boolean | null; askFirst: boolean | null; callsPerDay: number | null }[]
}

const asJson = (v: Record<string, unknown> | undefined) => (v && Object.keys(v).length > 0 ? JSON.stringify(v) : '')

/** Admin → Tools → a tool → Customize: what people see, what the model is told, each function, calls a day, and per group. */
export function CustomizeDialog({ toolId, onClose }: { toolId: string; onClose: () => void }) {
  const queryClient = useQueryClient()
  const data = useQuery({ queryKey: ['admin', 'tools', toolId, 'customize'], queryFn: ({ signal }) => api<Customization>(`/api/admin/tools/${encodeURIComponent(toolId)}/customize`, { signal }) })
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>Customize {data.data?.title ?? 'the tool'}</DialogTitle>
          <DialogDescription>Its name and words for people and the model, each function on or off, asking first, fixed arguments, and calls a day; for everyone or per group.</DialogDescription>
        </DialogHeader>
        {data.error ? (
          <Alert variant="destructive">{errorMessage(data.error)}</Alert>
        ) : !data.data ? (
          <Skeleton className="h-64" />
        ) : (
          <CustomizeForm data={data.data} onDone={() => {
            void queryClient.invalidateQueries({ queryKey: ['admin', 'tools'] })
            onClose()
          }} />
        )}
      </DialogContent>
    </Dialog>
  )
}

function CustomizeForm({ data, onDone }: { data: Customization; onDone: () => void }) {
  const s = data.setting
  const [title, setTitle] = useState(s.title ?? '')
  const [description, setDescription] = useState(s.description ?? '')
  const [instructions, setInstructions] = useState(s.instructions ?? '')
  const [replace, setReplace] = useState(s.replaceInstructions)
  const [off, setOff] = useState(s.disabledFunctions)
  const [ask, setAsk] = useState(s.askFirstFunctions)
  const [calls, setCalls] = useState(s.callsPerDay ? String(s.callsPerDay) : '')
  const [fns, setFns] = useState<Record<string, { description: string; fixed: string; defaults: string }>>(() =>
    Object.fromEntries(data.functions.map((f) => [f.name, { description: s.functions[f.name]?.description ?? '', fixed: asJson(s.functions[f.name]?.fixed), defaults: asJson(s.functions[f.name]?.defaults) }])),
  )
  const [error, setError] = useState<string | null>(null)
  const [trying, setTrying] = useState<{ fn: string; args: string; result: string | null }>({ fn: data.functions[0]?.name ?? '', args: '{}', result: null })
  const save = useMutation({
    mutationFn: (body: object) => api(`/api/admin/tools/${encodeURIComponent(data.id)}/customize`, { method: 'PUT', body }),
    onSuccess: () => {
      toast.success('Saved', { description: 'The next answers use it.' })
      onDone()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const tryIt = useMutation({
    mutationFn: () => api<{ text: string; isError: boolean }>(`/api/admin/tools/${encodeURIComponent(data.id)}/try`, { method: 'POST', body: { function: trying.fn, arguments: JSON.parse(trying.args || '{}') } }),
    onSuccess: (r) => setTrying((t) => ({ ...t, result: (r.isError ? 'Error: ' : '') + r.text })),
    onError: (e) => setTrying((t) => ({ ...t, result: errorMessage(e) })),
  })
  const submit = () => {
    setError(null)
    const functions: Record<string, object> = {}
    for (const [name, f] of Object.entries(fns)) {
      try {
        const entry = { ...(f.description.trim() ? { description: f.description.trim() } : {}), ...(f.fixed.trim() ? { fixed: JSON.parse(f.fixed) } : {}), ...(f.defaults.trim() ? { defaults: JSON.parse(f.defaults) } : {}) }
        if (Object.keys(entry).length > 0) functions[name] = entry
      } catch {
        return setError(`${name}: fixed and default arguments are JSON objects, like {"limit": 10}.`)
      }
    }
    const perDay = calls.trim() ? Number(calls) : null
    if (perDay !== null && !(Number.isInteger(perDay) && perDay >= 0)) return setError('Calls a day are a whole number; empty: no limit.')
    save.mutate({ title, description, instructions, replaceInstructions: replace, disabledFunctions: off, askFirstFunctions: ask, functions, callsPerDay: perDay })
  }
  return (
    <div className="grid gap-4">
      {error && <Alert variant="destructive">{error}</Alert>}
      {data.problem && <Alert variant="warning">Its functions could not be read now: {data.problem}</Alert>}
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label="Name people see" hint={`Empty: ${data.title}.`}>
          <Input value={title} onChange={(e) => setTitle(e.target.value)} maxLength={100} />
        </Field>
        <Field label="Calls a day, per person" hint="Empty: no limit.">
          <Input inputMode="numeric" value={calls} onChange={(e) => setCalls(e.target.value)} placeholder="no limit" />
        </Field>
      </div>
      <Field label="Description people see" hint={`Empty: ${data.description}`}>
        <Input value={description} onChange={(e) => setDescription(e.target.value)} maxLength={500} />
      </Field>
      <Field label="Words to the model about it" hint="When to use it, what to avoid, house rules. Added to the tool's own, unless they replace them.">
        <Textarea value={instructions} onChange={(e) => setInstructions(e.target.value)} rows={3} maxLength={4000} />
      </Field>
      <Tick checked={replace} onChange={setReplace}>
        In place of the tool&apos;s own words
      </Tick>
      <fieldset className="grid gap-3">
        <legend className="mb-1 text-sm font-medium">Functions</legend>
        {data.functions.length === 0 && <p className="text-sm text-muted-foreground">No functions to show.</p>}
        {data.functions.map((f) => (
          <div key={f.name} className="grid gap-2 rounded-lg border p-3">
            <div className="flex flex-wrap items-center gap-3">
              <span className="font-mono text-sm">{f.name}</span>
              <Tick checked={!off.includes(f.name)} onChange={(on) => setOff((x) => (on ? x.filter((n) => n !== f.name) : [...x, f.name]))} toggle>
                Offered
              </Tick>
              <Tick checked={ask.includes(f.name)} onChange={(on) => setAsk((x) => (on ? [...x, f.name] : x.filter((n) => n !== f.name)))}>
                Asks first
              </Tick>
            </div>
            <p className="text-xs text-muted-foreground">{f.description}</p>
            {!off.includes(f.name) && (
              <div className="grid gap-2 sm:grid-cols-3">
                <Input aria-label={`${f.name}: description for the model`} placeholder="Its description, for the model" value={fns[f.name]?.description ?? ''} onChange={(e) => setFns((x) => ({ ...x, [f.name]: { ...x[f.name]!, description: e.target.value } }))} />
                <Input aria-label={`${f.name}: fixed arguments`} placeholder='Fixed: {"project": "ops"}' className="font-mono" value={fns[f.name]?.fixed ?? ''} onChange={(e) => setFns((x) => ({ ...x, [f.name]: { ...x[f.name]!, fixed: e.target.value } }))} />
                <Input aria-label={`${f.name}: default arguments`} placeholder='Defaults: {"limit": 10}' className="font-mono" value={fns[f.name]?.defaults ?? ''} onChange={(e) => setFns((x) => ({ ...x, [f.name]: { ...x[f.name]!, defaults: e.target.value } }))} />
              </div>
            )}
            {f.parameters.length > 0 && <p className="text-xs text-muted-foreground">Arguments: {f.parameters.map((p) => p.name).join(', ')}</p>}
          </div>
        ))}
      </fieldset>
      <GroupSettings toolId={data.id} groups={data.groups} />
      {data.functions.length > 0 && (
        <fieldset className="grid gap-2 rounded-lg border border-dashed p-3">
          <legend className="text-sm font-medium">Try a function</legend>
          <p className="text-xs text-muted-foreground">As you, with the saved settings (fixed arguments too).</p>
          <div className="grid gap-2 sm:grid-cols-[12rem_1fr_auto]">
            <Select value={trying.fn} onValueChange={(fn) => setTrying((t) => ({ ...t, fn }))}>
              <SelectTrigger aria-label="Function to try">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {data.functions.map((f) => (
                  <SelectItem key={f.name} value={f.name}>
                    {f.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <Input aria-label="Its arguments (JSON)" className="font-mono" value={trying.args} onChange={(e) => setTrying((t) => ({ ...t, args: e.target.value }))} />
            <Button variant="outline" onClick={() => tryIt.mutate()} loading={tryIt.isPending}>
              Try it
            </Button>
          </div>
          {trying.result !== null && <pre className="max-h-48 overflow-auto rounded-md bg-muted p-2 text-xs whitespace-pre-wrap">{trying.result}</pre>}
        </fieldset>
      )}
      <DialogFooter>
        <Button onClick={submit} loading={save.isPending}>
          Save
        </Button>
      </DialogFooter>
    </div>
  )
}

const triState = (v: boolean | null) => (v === null ? 'company' : v ? 'yes' : 'no')
const fromTri = (v: string) => (v === 'company' ? null : v === 'yes')

/** A group's settings for the tool, over the company's: on in new chats, asking first, calls a day. */
function GroupSettings({ toolId, groups }: { toolId: string; groups: Customization['groups'] }) {
  const queryClient = useQueryClient()
  const all = useQuery(groupsQuery)
  const [rows, setRows] = useState(groups)
  const [adding, setAdding] = useState('')
  const save = useMutation({
    mutationFn: (r: Customization['groups'][number]) => api(`/api/admin/tools/${encodeURIComponent(toolId)}/groups`, { method: 'PUT', body: { groupId: r.groupId, onByDefault: r.onByDefault, askFirst: r.askFirst, callsPerDay: r.callsPerDay } }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['admin', 'tools', toolId, 'customize'] }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const change = (i: number, patch: Partial<Customization['groups'][number]>) => {
    const next = rows.map((r, j) => (j === i ? { ...r, ...patch } : r))
    setRows(next)
    save.mutate(next[i]!)
  }
  return (
    <fieldset className="grid gap-2">
      <legend className="mb-1 text-sm font-medium">For some groups</legend>
      <p className="text-xs text-muted-foreground">Over the company&apos;s settings for their members: on in new chats if a group says so, asking first only if all their groups do, the most calls a day of their groups.</p>
      {rows.map((r, i) => (
        <div key={r.groupId} className="grid items-center gap-2 rounded-md border p-2 text-sm sm:grid-cols-[1fr_9rem_9rem_7rem_auto]">
          <span className="font-medium">{r.name ?? all.data?.find((g) => g.id === r.groupId)?.name}</span>
          {(['onByDefault', 'askFirst'] as const).map((k) => (
            <Select key={k} value={triState(r[k])} onValueChange={(v) => change(i, { [k]: fromTri(v) })}>
              <SelectTrigger aria-label={`${r.name}: ${k === 'onByDefault' ? 'on in new chats' : 'asks first'}`}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="company">{k === 'onByDefault' ? 'New chats: as the company' : 'Asks: as the company'}</SelectItem>
                <SelectItem value="yes">{k === 'onByDefault' ? 'On in new chats' : 'Asks first'}</SelectItem>
                <SelectItem value="no">{k === 'onByDefault' ? 'Off in new chats' : 'Does not ask'}</SelectItem>
              </SelectContent>
            </Select>
          ))}
          <Input aria-label={`${r.name}: calls a day`} inputMode="numeric" placeholder="calls a day" defaultValue={r.callsPerDay ?? ''} onBlur={(e) => change(i, { callsPerDay: e.target.value.trim() ? Number(e.target.value) : null })} />
          <Button variant="ghost" size="icon-sm" aria-label={`Remove ${r.name}'s settings`} onClick={() => {
            setRows(rows.filter((_, j) => j !== i))
            save.mutate({ ...r, onByDefault: null, askFirst: null, callsPerDay: null })
          }}>
            <Trash2 />
          </Button>
        </div>
      ))}
      <div className="flex gap-2">
        <Select value={adding} onValueChange={setAdding}>
          <SelectTrigger className="w-60" aria-label="A group to set">
            <SelectValue placeholder="A group…" />
          </SelectTrigger>
          <SelectContent>
            {(all.data ?? []).filter((g) => rows.every((r) => r.groupId !== g.id)).map((g) => (
              <SelectItem key={g.id} value={g.id}>
                {g.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Button variant="outline" disabled={!adding} onClick={() => {
          const g = all.data?.find((x) => x.id === adding)
          if (g) setRows([...rows, { groupId: g.id, name: g.name, onByDefault: null, askFirst: null, callsPerDay: null }])
          setAdding('')
        }}>
          <Plus /> Add
        </Button>
      </div>
    </fieldset>
  )
}

/** A tool made without code: an HTTP request to an address you fix, or a prompt run on a model; tried before it is saved. */
export function CustomToolDialog({ tool, onClose }: { tool: CustomToolRow | null; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [kind, setKind] = useState<'http' | 'prompt'>(tool?.kind ?? 'http')
  const [name, setName] = useState(tool?.name ?? '')
  const [title, setTitle] = useState(tool?.title ?? '')
  const [description, setDescription] = useState(tool?.description ?? '')
  const [params, setParams] = useState(tool?.parameters ?? [])
  const [method, setMethod] = useState(tool?.method ?? 'GET')
  const [url, setUrl] = useState(tool?.urlTemplate ?? '')
  const [headers, setHeaders] = useState<{ name: string; value: string }[]>(tool?.headers.map((h) => ({ name: h, value: '' })) ?? [])
  const [body, setBody] = useState(tool?.bodyTemplate ?? '')
  const [path, setPath] = useState(tool?.resultPath ?? '')
  const [askFirst, setAskFirst] = useState(tool?.askFirst ?? false)
  const [model, setModel] = useState(tool?.model ?? '')
  const [prompt, setPrompt] = useState(tool?.promptTemplate ?? '')
  const [system, setSystem] = useState(tool?.systemPrompt ?? '')
  const [args, setArgs] = useState('{}')
  const [result, setResult] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const request = () => ({
    name, kind, title, description, parameters: params, askFirst,
    ...(kind === 'http'
      ? { method, urlTemplate: url, headers: Object.fromEntries(headers.filter((h) => h.name.trim()).map((h) => [h.name.trim(), h.value])), bodyTemplate: body || null, resultPath: path || null }
      : { model, promptTemplate: prompt, systemPrompt: system || null }),
  })
  const save = useMutation({
    mutationFn: () => api(tool ? `/api/admin/tools/custom/${tool.id}` : '/api/admin/tools/custom', { method: tool ? 'PUT' : 'POST', body: request() }),
    onSuccess: () => {
      toast.success(tool ? 'Tool saved' : 'Tool made', { description: 'Turn it on for people on its card.' })
      void queryClient.invalidateQueries({ queryKey: ['admin', 'tools'] })
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const tryIt = useMutation({
    mutationFn: () => api<{ text: string; isError: boolean }>('/api/admin/tools/custom/try', { method: 'POST', body: { tool: request(), arguments: JSON.parse(args || '{}'), id: tool?.id } }),
    onSuccess: (r) => setResult((r.isError ? 'Error: ' : '') + r.text),
    onError: (e) => setResult(errorMessage(e)),
  })
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{tool ? `Change ${tool.name}` : 'New tool'}</DialogTitle>
          <DialogDescription>A tool without code: an HTTP request to an address you fix (the model fills only its path, query or body), or a prompt run on a model you choose.</DialogDescription>
        </DialogHeader>
        <div className="grid gap-3">
          {error && <Alert variant="destructive">{error}</Alert>}
          <div className="grid gap-3 sm:grid-cols-3">
            <Field label="Kind">
              <Select value={kind} onValueChange={(v) => setKind(v as 'http' | 'prompt')}>
                <SelectTrigger aria-label="Kind">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="http">HTTP request</SelectItem>
                  <SelectItem value="prompt">A prompt on a model</SelectItem>
                </SelectContent>
              </Select>
            </Field>
            <Field label="Name the model calls" hint="lookup_order">
              <Input value={name} onChange={(e) => setName(e.target.value)} className="font-mono" />
            </Field>
            <Field label="Name people see">
              <Input value={title} onChange={(e) => setTitle(e.target.value)} />
            </Field>
          </div>
          <Field label="What it does" hint="The model reads it to know when to call it.">
            <Textarea value={description} onChange={(e) => setDescription(e.target.value)} rows={2} />
          </Field>
          <fieldset className="grid gap-2">
            <legend className="mb-1 text-sm font-medium">Parameters</legend>
            {params.map((p, i) => (
              <div key={i} className="grid items-center gap-2 sm:grid-cols-[8rem_7rem_1fr_auto_auto]">
                <Input aria-label="Parameter name" value={p.name} onChange={(e) => setParams(params.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))} className="font-mono" />
                <Select value={p.type} onValueChange={(type) => setParams(params.map((x, j) => (j === i ? { ...x, type } : x)))}>
                  <SelectTrigger aria-label="Parameter type">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="string">text</SelectItem>
                    <SelectItem value="number">number</SelectItem>
                    <SelectItem value="boolean">yes or no</SelectItem>
                  </SelectContent>
                </Select>
                <Input aria-label="Parameter description" placeholder="What it is" value={p.description ?? ''} onChange={(e) => setParams(params.map((x, j) => (j === i ? { ...x, description: e.target.value } : x)))} />
                <Tick checked={p.required} onChange={(required) => setParams(params.map((x, j) => (j === i ? { ...x, required } : x)))}>
                  Needed
                </Tick>
                <Button variant="ghost" size="icon-sm" aria-label="Remove the parameter" onClick={() => setParams(params.filter((_, j) => j !== i))}>
                  <Trash2 />
                </Button>
              </div>
            ))}
            <Button variant="outline" size="sm" className="w-fit" onClick={() => setParams([...params, { name: '', type: 'string', description: '', required: true }])}>
              <Plus /> A parameter
            </Button>
          </fieldset>
          {kind === 'http' ? (
            <>
              <div className="grid gap-3 sm:grid-cols-[7rem_1fr]">
                <Field label="Method">
                  <Select value={method} onValueChange={setMethod}>
                    <SelectTrigger aria-label="Method">
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
                <Field label="Address" hint="Its host is fixed; parameters go in its path or query as {name}: https://orders.example.com/api/orders/{order_id}">
                  <Input value={url} onChange={(e) => setUrl(e.target.value)} className="font-mono" />
                </Field>
              </div>
              <fieldset className="grid gap-2">
                <legend className="mb-1 text-sm font-medium">Headers</legend>
                <p className="text-xs text-muted-foreground">Kept encrypted and never shown again; leave a saved one empty to keep it.</p>
                {headers.map((h, i) => (
                  <div key={i} className="grid gap-2 sm:grid-cols-[12rem_1fr_auto]">
                    <Input aria-label="Header name" value={h.name} onChange={(e) => setHeaders(headers.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))} placeholder="Authorization" />
                    <Input aria-label="Header value" type="password" value={h.value} onChange={(e) => setHeaders(headers.map((x, j) => (j === i ? { ...x, value: e.target.value } : x)))} placeholder={tool?.headers.includes(h.name) ? 'saved' : 'Bearer …'} />
                    <Button variant="ghost" size="icon-sm" aria-label="Remove the header" onClick={() => setHeaders(headers.filter((_, j) => j !== i))}>
                      <Trash2 />
                    </Button>
                  </div>
                ))}
                <Button variant="outline" size="sm" className="w-fit" onClick={() => setHeaders([...headers, { name: '', value: '' }])}>
                  <Plus /> A header
                </Button>
              </fieldset>
              {method !== 'GET' && (
                <Field label="Body (JSON)" hint='{name} inside its strings: {"text": "{message}"}'>
                  <Textarea value={body} onChange={(e) => setBody(e.target.value)} rows={3} className="font-mono" />
                </Field>
              )}
              <div className="grid gap-3 sm:grid-cols-2">
                <Field label="Keep only (optional)" hint="A part of a JSON answer: data.items">
                  <Input value={path} onChange={(e) => setPath(e.target.value)} className="font-mono" />
                </Field>
                <div className="grid content-end pb-2">
                  <Tick checked={askFirst} onChange={setAskFirst}>
                    Each call asks the person first (it changes something)
                  </Tick>
                </div>
              </div>
            </>
          ) : (
            <>
              <Field label="Model" hint="Its name, as in Admin → Models.">
                <Input value={model} onChange={(e) => setModel(e.target.value)} />
              </Field>
              <Field label="System prompt (optional)">
                <Textarea value={system} onChange={(e) => setSystem(e.target.value)} rows={2} />
              </Field>
              <Field label="Prompt" hint="The parameters in it as {name}.">
                <Textarea value={prompt} onChange={(e) => setPrompt(e.target.value)} rows={4} />
              </Field>
            </>
          )}
          <fieldset className="grid gap-2 rounded-lg border border-dashed p-3">
            <legend className="text-sm font-medium">Try it</legend>
            <div className="flex gap-2">
              <Input aria-label="Arguments to try (JSON)" className="font-mono" value={args} onChange={(e) => setArgs(e.target.value)} />
              <Button variant="outline" onClick={() => tryIt.mutate()} loading={tryIt.isPending}>
                Try it
              </Button>
            </div>
            {result !== null && <pre className="max-h-48 overflow-auto rounded-md bg-muted p-2 text-xs whitespace-pre-wrap">{result}</pre>}
          </fieldset>
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={() => save.mutate()} loading={save.isPending}>
            {tool ? 'Save' : 'Make the tool'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

interface ToolsetRow {
  id: string
  name: string
  description: string | null
  tools: string[]
  groups: string[]
  defaultFor: string[]
  plugin: string | null
}

/** Toolsets: named sets of tools a chat starts with or switches to; some groups' new chats start with one. */
export function ToolsetsDialog({ tools, onClose }: { tools: { id: string; title: string }[]; onClose: () => void }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const sets = useQuery({ queryKey: ['admin', 'tools', 'toolsets'], queryFn: ({ signal }) => api<ToolsetRow[]>('/api/admin/tools/toolsets', { signal }) })
  const groups = useQuery(groupsQuery)
  const [editing, setEditing] = useState<ToolsetRow | null>(null)
  const [error, setError] = useState<string | null>(null)
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['admin', 'tools', 'toolsets'] })
  const save = useMutation({
    mutationFn: (s: ToolsetRow) => api(s.id ? `/api/admin/tools/toolsets/${s.id}` : '/api/admin/tools/toolsets', { method: s.id ? 'PUT' : 'POST', body: s }),
    onSuccess: () => {
      setEditing(null)
      void refresh()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: (id: string) => api(`/api/admin/tools/toolsets/${id}`, { method: 'DELETE' }),
    onSuccess: refresh,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const toggle = (list: string[], id: string, on: boolean) => (on ? [...list, id] : list.filter((x) => x !== id))
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>Toolsets</DialogTitle>
          <DialogDescription>Named sets of tools: people switch a chat to one in its Tools menu, and a group&apos;s new chats can start with one.</DialogDescription>
        </DialogHeader>
        {error && <Alert variant="destructive">{error}</Alert>}
        {editing ? (
          <div className="grid gap-3">
            <Field label="Name">
              <Input value={editing.name} onChange={(e) => setEditing({ ...editing, name: e.target.value })} maxLength={100} />
            </Field>
            <Field label="What it is for (optional)">
              <Input value={editing.description ?? ''} onChange={(e) => setEditing({ ...editing, description: e.target.value })} maxLength={500} />
            </Field>
            <fieldset className="grid gap-1.5">
              <legend className="mb-1 text-sm font-medium">Tools</legend>
              <div className="flex flex-wrap gap-x-4 gap-y-1.5">
                {tools.map((t) => (
                  <Tick key={t.id} checked={editing.tools.includes(t.id)} onChange={(on) => setEditing({ ...editing, tools: toggle(editing.tools, t.id, on) })}>
                    {t.title}
                  </Tick>
                ))}
              </div>
            </fieldset>
            <fieldset className="grid gap-1.5">
              <legend className="mb-1 text-sm font-medium">Who sees it (none ticked: everyone)</legend>
              <div className="flex flex-wrap gap-x-4 gap-y-1.5">
                {(groups.data ?? []).map((g) => (
                  <Tick key={g.id} checked={editing.groups.includes(g.id)} onChange={(on) => setEditing({ ...editing, groups: toggle(editing.groups, g.id, on) })}>
                    {g.name}
                  </Tick>
                ))}
              </div>
            </fieldset>
            <fieldset className="grid gap-1.5">
              <legend className="mb-1 text-sm font-medium">New chats of these groups start with it</legend>
              <div className="flex flex-wrap gap-x-4 gap-y-1.5">
                {(groups.data ?? []).map((g) => (
                  <Tick key={g.id} checked={editing.defaultFor.includes(g.id)} onChange={(on) => setEditing({ ...editing, defaultFor: toggle(editing.defaultFor, g.id, on) })}>
                    {g.name}
                  </Tick>
                ))}
              </div>
            </fieldset>
            <DialogFooter>
              <Button variant="outline" onClick={() => setEditing(null)}>
                Back
              </Button>
              <Button onClick={() => save.mutate(editing)} loading={save.isPending}>
                Save the toolset
              </Button>
            </DialogFooter>
          </div>
        ) : (
          <div className="grid gap-2">
            {(sets.data ?? []).map((s) => (
              <div key={s.id} className="flex flex-wrap items-center gap-2 rounded-lg border p-2.5 text-sm">
                <span className="font-medium">{s.name}</span>
                {s.plugin && <Badge variant="outline">from {s.plugin}</Badge>}
                <span className="flex-1 text-xs text-muted-foreground">{s.tools.map((id) => tools.find((t) => t.id === id)?.title ?? id).join(', ')}</span>
                <Button variant="ghost" size="sm" onClick={() => setEditing(s)}>
                  Change
                </Button>
                <Button variant="ghost" size="icon-sm" aria-label={`Remove ${s.name}`} onClick={async () => {
                  if (await confirm({ title: `Remove ${s.name}?`, confirm: 'Remove', destructive: true })) remove.mutate(s.id)
                }}>
                  <Trash2 />
                </Button>
              </div>
            ))}
            {sets.data?.length === 0 && <p className="text-sm text-muted-foreground">No toolsets yet.</p>}
            <Button variant="outline" className="w-fit" onClick={() => setEditing({ id: '', name: '', description: null, tools: [], groups: [], defaultFor: [], plugin: null })}>
              <Plus /> New toolset
            </Button>
          </div>
        )}
      </DialogContent>
    </Dialog>
  )
}
