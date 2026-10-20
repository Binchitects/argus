import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowDown, ArrowUp, Pencil, Plus, ShieldAlert, Trash2 } from 'lucide-react'
import { useId, useMemo, useState, type ReactNode } from 'react'
import { Link } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { groupsQuery } from './groups-api'
import { peopleQuery } from './people-api'
import {
  actionText, detectorText, emptyRule, eventsQuery, rulesQuery, stageText, statsQuery, templatesQuery,
  type Detector, type RuleAction, type RuleDraft, type RulesResponse, type SafeguardEvent, type SafeguardRule, type Stage, type TrialResult,
} from './safeguards-api'

const when = (iso: string) => new Date(iso).toLocaleString()

/** Admin → Safeguards: the rules in their order, a test bench, what they did, and people's requests to review a refusal. */
export function SafeguardsPage() {
  const rules = useQuery(rulesQuery)
  const stats = useQuery(statsQuery)
  return (
    <>
      <PageHeader
        title="Safeguards"
        description="Rules that look at questions, files, answers, tools and API requests, and block, mask, warn, ask for a reason, log or reroute. They run top to bottom; limits on messages and pictures are under Settings → Safeguards."
      />
      {rules.error ? (
        <QueryError error={rules.error} retry={() => rules.refetch()} />
      ) : !rules.data ? (
        <Skeleton className="h-64" />
      ) : (
        <Tabs defaultValue={new URLSearchParams(window.location.search).get('tab') ?? 'rules'}>
          <TabsList>
            <TabsTrigger value="rules">Rules</TabsTrigger>
            <TabsTrigger value="test">Test bench</TabsTrigger>
            <TabsTrigger value="events">What they did</TabsTrigger>
            <TabsTrigger value="reviews">
              Reviews{stats.data && stats.data.waiting > 0 && <Badge className="ml-1.5">{stats.data.waiting}</Badge>}
            </TabsTrigger>
          </TabsList>
          <TabsContent value="rules" className="mt-4">
            <Rules data={rules.data} />
          </TabsContent>
          <TabsContent value="test" className="mt-4">
            <Bench />
          </TabsContent>
          <TabsContent value="events" className="mt-4">
            <Events />
          </TabsContent>
          <TabsContent value="reviews" className="mt-4">
            <Reviews />
          </TabsContent>
        </Tabs>
      )}
    </>
  )
}

function Scope({ rule }: { rule: SafeguardRule }) {
  const who = rule.groups.length + rule.people.length === 0 ? 'everyone' : [rule.groups.length && `${rule.groups.length} group${rule.groups.length > 1 ? 's' : ''}`, rule.people.length && `${rule.people.length} ${rule.people.length > 1 ? 'people' : 'person'}`].filter(Boolean).join(', ')
  return (
    <span className="text-xs text-muted-foreground">
      {rule.stages.map((s) => stageText[s]).join(', ')} · {who}
      {rule.models.length > 0 && <> · models {rule.models.join(', ')}</>}
    </span>
  )
}

function Rules({ data }: { data: RulesResponse }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const templates = useQuery(templatesQuery)
  const [editing, setEditing] = useState<{ id: string | null; draft: RuleDraft } | null>(null)
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['admin', 'safeguards'] })
  const change = useMutation({
    mutationFn: ({ path, method, body }: { path: string; method: 'PUT' | 'DELETE' | 'POST'; body?: object }) => api(path, { method, body }),
    onSuccess: refresh,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const move = (index: number, by: number) => {
    const ids = data.rules.map((r) => r.rule.id)
    const [id] = ids.splice(index, 1)
    ids.splice(index + by, 0, id)
    change.mutate({ path: '/api/admin/safeguards/rules/order', method: 'POST', body: { ids } })
  }
  const toDraft = (r: SafeguardRule): RuleDraft => {
    const { id, order, updatedAt, ...rest } = r
    void id
    void order
    void updatedAt
    return rest
  }
  return (
    <div className="grid gap-4">
      <div className="flex flex-wrap items-center gap-2">
        <Button onClick={() => setEditing({ id: null, draft: emptyRule })}>
          <Plus /> New rule
        </Button>
        {templates.data && (
          <Select onValueChange={(name) => {
            const t = templates.data.find((x) => x.name === name)
            if (t) setEditing({ id: null, draft: { ...emptyRule, ...t, description: t.description } })
          }}>
            <SelectTrigger className="w-60" aria-label="Start from a template">
              <SelectValue placeholder="Start from a template" />
            </SelectTrigger>
            <SelectContent>
              {templates.data.map((t) => (
                <SelectItem key={t.name} value={t.name}>
                  {t.name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
        <p className="text-xs text-muted-foreground">Top to bottom: an Allow exempts what it covers from the rules of its detector below it; the first Block ends it.</p>
      </div>
      {data.rules.length === 0 ? (
        <EmptyState icon={ShieldAlert} title="No rules">Start from a template: secrets, personal data to cloud models, jailbreaks…</EmptyState>
      ) : (
        <ol className="grid gap-2" aria-label="Rules, in their order">
          {data.rules.map(({ rule, week }, i) => (
            <li key={rule.id} className="flex flex-wrap items-center gap-3 rounded-lg border bg-card px-3 py-2.5">
              <div className="flex flex-col">
                <Button variant="ghost" size="icon-sm" className="size-6" disabled={i === 0} onClick={() => move(i, -1)} aria-label={`Move ${rule.name} up`}>
                  <ArrowUp />
                </Button>
                <Button variant="ghost" size="icon-sm" className="size-6" disabled={i === data.rules.length - 1} onClick={() => move(i, 1)} aria-label={`Move ${rule.name} down`}>
                  <ArrowDown />
                </Button>
              </div>
              <div className="grid min-w-0 flex-1 gap-0.5">
                <p className="flex flex-wrap items-center gap-1.5 text-sm font-medium">
                  {rule.name}
                  <Badge variant="outline">{detectorText[rule.detector].title}</Badge>
                  <Badge variant={rule.action === 'block' ? 'destructive' : 'secondary'}>{actionText[rule.action].title}</Badge>
                  {rule.monitor && <Badge variant="outline">Monitor</Badge>}
                  {rule.strike && <Badge variant="outline">Strike</Badge>}
                </p>
                <Scope rule={rule} />
              </div>
              <span className="text-xs text-muted-foreground tabular-nums" title="Times it found something in the last 7 days">
                {week} this week
              </span>
              <Switch
                checked={rule.enabled}
                onCheckedChange={(enabled) => change.mutate({ path: `/api/admin/safeguards/rules/${rule.id}`, method: 'PUT', body: { ...toDraft(rule), enabled } })}
                aria-label={`${rule.name} on`}
              />
              <Button variant="ghost" size="icon-sm" onClick={() => setEditing({ id: rule.id, draft: toDraft(rule) })} aria-label={`Change ${rule.name}`}>
                <Pencil />
              </Button>
              <Button
                variant="ghost"
                size="icon-sm"
                aria-label={`Remove ${rule.name}`}
                onClick={async () => {
                  if (await confirm({ title: `Remove ${rule.name}?`, description: 'What it did stays in the events.', confirm: 'Remove', destructive: true }))
                    change.mutate({ path: `/api/admin/safeguards/rules/${rule.id}`, method: 'DELETE' })
                }}
              >
                <Trash2 />
              </Button>
            </li>
          ))}
        </ol>
      )}
      {editing && <RuleDialog data={data} id={editing.id} start={editing.draft} onClose={() => setEditing(null)} onSaved={refresh} />}
    </div>
  )
}

/** A tick box (or a switch) and its words, joined for screen readers. */
function Tick({ checked, onChange, toggle, children }: { checked: boolean; onChange: (on: boolean) => void; toggle?: boolean; children: ReactNode }) {
  const id = useId()
  return (
    <div className="flex items-center gap-2 text-sm">
      {toggle ? <Switch id={id} checked={checked} onCheckedChange={onChange} /> : <Checkbox id={id} checked={checked} onCheckedChange={(on) => onChange(on === true)} />}
      <label htmlFor={id}>{children}</label>
    </div>
  )
}

const lines = (s: string) => s.split(/[\n,]/).map((x) => x.trim()).filter(Boolean)

function Choices({ label, all, value, onChange, about }: { label: string; all: string[]; value: string[]; onChange: (v: string[]) => void; about?: (v: string) => string }) {
  return (
    <fieldset className="grid gap-1.5">
      <legend className="mb-1 text-sm font-medium">{label}</legend>
      <div className="flex flex-wrap gap-x-4 gap-y-1.5">
        {all.map((v) => (
          <label key={v} className="flex items-center gap-1.5 text-sm" title={about?.(v)}>
            <Checkbox checked={value.includes(v)} onCheckedChange={(on) => onChange(on ? [...value, v] : value.filter((x) => x !== v))} />
            {v}
          </label>
        ))}
      </div>
    </fieldset>
  )
}

/** A rule's form: what it looks for, where, for whom and which models, and what it does. */
function RuleDialog({ data, id, start, onClose, onSaved }: { data: RulesResponse; id: string | null; start: RuleDraft; onClose: () => void; onSaved: () => void }) {
  const [r, setR] = useState<RuleDraft>(start)
  const [error, setError] = useState<string | null>(null)
  const [who, setWho] = useState(start.groups.length + start.people.length > 0 ? 'some' : 'everyone')
  const [find, setFind] = useState('')
  const groups = useQuery(groupsQuery)
  const people = useQuery(peopleQuery)
  const set = <K extends keyof RuleDraft>(k: K, v: RuleDraft[K]) => setR((x) => ({ ...x, [k]: v }))
  const save = useMutation({
    mutationFn: () => {
      const body = { ...r, groups: who === 'everyone' ? [] : r.groups, people: who === 'everyone' ? [] : r.people }
      return api(id ? `/api/admin/safeguards/rules/${id}` : '/api/admin/safeguards/rules', { method: id ? 'PUT' : 'POST', body })
    },
    onSuccess: () => {
      toast.success(id ? 'Rule saved' : 'Rule added', { description: 'It holds at once.' })
      onSaved()
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  const matches = useMemo(
    () => (people.data?.people ?? []).filter((p) => r.people.includes(p.id) || (find.trim().length > 0 && `${p.displayName ?? ''} ${p.userName}`.toLowerCase().includes(find.toLowerCase()))).slice(0, 30),
    [people.data, find, r.people],
  )
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{id ? `Change ${start.name}` : 'New rule'}</DialogTitle>
          <DialogDescription>{detectorText[r.detector].about}</DialogDescription>
        </DialogHeader>
        <form
          className="grid gap-4"
          onSubmit={(e) => {
            e.preventDefault()
            setError(null)
            save.mutate()
          }}
        >
          {error && <Alert variant="destructive">{error}</Alert>}
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Name">
              <Input value={r.name} onChange={(e) => set('name', e.target.value)} maxLength={200} required />
            </Field>
            <Field label="Looks for">
              <Select value={r.detector} onValueChange={(v) => set('detector', v as Detector)}>
                <SelectTrigger aria-label="Looks for">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {data.detectors.map((d) => (
                    <SelectItem key={d} value={d}>
                      {detectorText[d].title}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
          </div>
          <Field label="What it is for (optional)">
            <Input value={r.description ?? ''} onChange={(e) => set('description', e.target.value || null)} maxLength={2000} />
          </Field>
          {r.detector === 'secrets' && <Choices label="Kinds (none ticked: all)" all={data.secretKinds} value={r.kinds} onChange={(v) => set('kinds', v)} />}
          {r.detector === 'pii' && <Choices label="Kinds (none ticked: all)" all={data.piiKinds} value={r.kinds} onChange={(v) => set('kinds', v)} />}
          {r.detector === 'words' && (
            <Field label="Words, phrases and patterns" hint="One a line or separated by ;. A phrase matches as whole words, ignoring case; re: starts a regular expression (re:\bproject falcon\b). Empty for an Allow: all words.">
              <Textarea value={r.patterns ?? ''} onChange={(e) => set('patterns', e.target.value || null)} rows={3} />
            </Field>
          )}
          {r.detector === 'topic' && (
            <>
              <Choices label="Harms" all={data.categories.map((c) => c.key)} value={r.categories} onChange={(v) => set('categories', v)} about={(k) => data.categories.find((c) => c.key === k)?.description ?? ''} />
              <Field label="Topics of your own" hint="One a line: name: what it covers (legal advice: what someone should do in their own legal matter). The model reads the text once for every topic rule.">
                <Textarea value={r.topics ?? ''} onChange={(e) => set('topics', e.target.value || null)} rows={3} />
              </Field>
            </>
          )}
          {r.detector === 'injection' && (
            <Tick checked={r.modelCheck} onChange={(on) => set('modelCheck', on)}>
              The model reads it too, for what the patterns miss (one short call)
            </Tick>
          )}
          {r.detector === 'links' && (
            <>
              <Field label="Domains" hint="One a line; a domain covers its subdomains.">
                <Textarea value={r.domains.join('\n')} onChange={(e) => set('domains', lines(e.target.value))} rows={3} />
              </Field>
              <Tick checked={r.domainsAllowed} onChange={(on) => set('domainsAllowed', on)}>
                Only these are allowed (otherwise: these are refused)
              </Tick>
            </>
          )}
          {r.detector === 'files' && (
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="File types refused" hint=".exe, .dll, .ps1…">
                <Input value={r.extensions.join(', ')} onChange={(e) => set('extensions', lines(e.target.value))} />
              </Field>
              <Field label="Largest file (MB)">
                <Input inputMode="numeric" value={r.maxFileMb ?? ''} onChange={(e) => set('maxFileMb', e.target.value ? Number(e.target.value) || null : null)} />
              </Field>
            </div>
          )}
          <Choices label="Where it looks" all={data.stages} value={r.stages} onChange={(v) => set('stages', v as Stage[])} about={(s) => stageText[s as Stage]} />
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="What it does" hint={actionText[r.action].about}>
              <Select value={r.action} onValueChange={(v) => set('action', v as RuleAction)}>
                <SelectTrigger aria-label="What it does">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {data.actions.map((a) => (
                    <SelectItem key={a} value={a}>
                      {actionText[a].title}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
            <div className="grid content-start gap-2">
              <span className="text-sm font-medium">Mode</span>
              <Segmented
                label="Mode"
                value={r.monitor ? 'monitor' : 'enforce'}
                onChange={(v) => set('monitor', v === 'monitor')}
                options={[
                  { value: 'enforce', label: 'Enforce' },
                  { value: 'monitor', label: 'Monitor only' },
                ]}
              />
            </div>
          </div>
          {r.action === 'reroute' && (
            <Field label="Send it to the model" hint="Its name, as in Admin → Models.">
              <Input value={r.rerouteModel ?? ''} onChange={(e) => set('rerouteModel', e.target.value || null)} />
            </Field>
          )}
          {r.action === 'block' && (
            <Tick checked={r.strike} onChange={(on) => set('strike', on)}>
              A strike: counts towards suspension, and the admins hear of it
            </Tick>
          )}
          {['block', 'warn', 'justify'].includes(r.action) && (
            <Field label="What the person is told (optional)" hint="Empty: a sentence that says what was found, or the company's refusal.">
              <Textarea value={r.message ?? ''} onChange={(e) => set('message', e.target.value || null)} rows={2} />
            </Field>
          )}
          <div className="grid gap-2">
            <span className="text-sm font-medium">For whom</span>
            <Segmented label="For whom" value={who} onChange={setWho} options={[{ value: 'everyone', label: 'Everyone' }, { value: 'some', label: 'Some groups and people' }]} />
            {who === 'some' && (
              <div className="grid gap-3 rounded-md border p-3">
                <Choices label="Groups" all={(groups.data ?? []).map((g) => g.id)} value={r.groups} onChange={(v) => set('groups', v)} about={(g) => groups.data?.find((x) => x.id === g)?.name ?? g} />
                <p className="-mt-2 text-xs text-muted-foreground">{(groups.data ?? []).filter((g) => r.groups.includes(g.id)).map((g) => g.name).join(', ') || 'No group chosen'}</p>
                <Field label="People">
                  <Input value={find} onChange={(e) => setFind(e.target.value)} placeholder="Type a name to find someone" />
                </Field>
                <div className="flex flex-wrap gap-x-4 gap-y-1.5">
                  {matches.map((p) => (
                    <label key={p.id} className="flex items-center gap-1.5 text-sm">
                      <Checkbox checked={r.people.includes(p.id)} onCheckedChange={(on) => set('people', on ? [...r.people, p.id] : r.people.filter((x) => x !== p.id))} />
                      {p.displayName || p.userName}
                    </label>
                  ))}
                </div>
              </div>
            )}
          </div>
          <Field label="Models" hint="Empty: all. Names, * patterns, or @external for every model on another server or provider (cloud).">
            <Input value={r.models.join(', ')} onChange={(e) => set('models', lines(e.target.value))} placeholder="all models" />
          </Field>
          <Tick checked={r.enabled} onChange={(on) => set('enabled', on)} toggle>
            On
          </Tick>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={save.isPending}>
              {id ? 'Save' : 'Add the rule'}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

/** The test bench: a text through the rules as they stand, nothing recorded. */
function Bench() {
  const [text, setText] = useState('')
  const [stage, setStage] = useState<Stage>('question')
  const [model, setModel] = useState('')
  const run = useMutation({
    mutationFn: () => api<TrialResult>('/api/admin/safeguards/test', { method: 'POST', body: { text, stage, model: model || null } }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const r = run.data
  return (
    <div className="grid gap-4 lg:grid-cols-2">
      <Card>
        <CardHeader>
          <CardTitle>Try a text</CardTitle>
          <CardDescription>Which rules fire, and what they would do. Nothing is sent or recorded; rules in monitor mode show too.</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-3">
          <Field label="Text">
            <Textarea value={text} onChange={(e) => setText(e.target.value)} rows={6} />
          </Field>
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Where">
              <Select value={stage} onValueChange={(v) => setStage(v as Stage)}>
                <SelectTrigger aria-label="Where">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {(Object.keys(stageText) as Stage[]).map((s) => (
                    <SelectItem key={s} value={s}>
                      {stageText[s]}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
            <Field label="Model (optional)">
              <Input value={model} onChange={(e) => setModel(e.target.value)} placeholder="any" />
            </Field>
          </div>
          <Button onClick={() => run.mutate()} loading={run.isPending} disabled={!text.trim()}>
            Try it
          </Button>
        </CardContent>
      </Card>
      {r && (
        <Card aria-live="polite">
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              Result <Badge variant={r.outcome === 'blocked' ? 'destructive' : r.outcome === 'passes' ? 'secondary' : 'outline'}>{{ blocked: 'Blocked', asks: 'Asks the person', masked: 'Goes, masked', passes: 'Goes' }[r.outcome]}</Badge>
            </CardTitle>
            {r.reason && <CardDescription>{r.reason}</CardDescription>}
          </CardHeader>
          <CardContent className="grid gap-3 text-sm">
            {r.reroute && <p>Sent to {r.reroute}.</p>}
            {r.outcome === 'masked' && <pre className="rounded-md bg-muted p-2 text-xs whitespace-pre-wrap">{r.text}</pre>}
            {r.hits.length === 0 ? (
              <p className="text-muted-foreground">No rule found anything.</p>
            ) : (
              <ul className="grid gap-1.5" aria-label="Rules that fired">
                {r.hits.map((h, i) => (
                  <li key={i} className="flex flex-wrap items-baseline gap-1.5">
                    <span className="font-medium">{h.rule}</span>
                    <Badge variant="outline">{h.monitor ? `would be ${h.outcome}` : h.outcome}</Badge>
                    <span className="text-muted-foreground">{h.detail}</span>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>
      )}
    </div>
  )
}

/** Day by day, what the rules did: blocks apart from the rest, monitor rules apart. */
function Chart() {
  const stats = useQuery(statsQuery)
  if (!stats.data) return null
  const days = stats.data.days
  const max = Math.max(1, ...days.map((d) => Object.values(d.outcomes).reduce((a, b) => a + b, 0) + d.monitor))
  return (
    <Card>
      <CardHeader>
        <CardTitle>The last 30 days</CardTitle>
        <CardDescription>
          {stats.data.people} {stats.data.people === 1 ? 'person' : 'people'} refused. Each day: blocked, other actions, and monitor rules that would have acted.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <p className="sr-only">{days.map((d) => `${d.day}: ${d.outcomes.blocked ?? 0} blocked`).join('; ')}</p>
        <div className="flex h-32 items-end gap-[2px]" aria-hidden="true">
          {days.map((d) => {
            const blocked = d.outcomes.blocked ?? 0
            const other = Object.entries(d.outcomes).filter(([k]) => k !== 'blocked').reduce((a, [, v]) => a + v, 0)
            return (
              <div key={d.day} className="flex flex-1 flex-col-reverse gap-[2px]" title={`${d.day}: ${blocked} blocked, ${other} other, ${d.monitor} monitored`}>
                {blocked > 0 && <div className="rounded-t-[2px] bg-destructive/80" style={{ height: `${(blocked / max) * 120}px` }} />}
                {other > 0 && <div className="rounded-t-[2px] bg-primary/70" style={{ height: `${(other / max) * 120}px` }} />}
                {d.monitor > 0 && <div className="rounded-t-[2px] bg-muted-foreground/40" style={{ height: `${(d.monitor / max) * 120}px` }} />}
              </div>
            )
          })}
        </div>
        <ul className="mt-3 flex flex-wrap gap-4 text-xs text-muted-foreground" aria-label="Legend">
          <li className="flex items-center gap-1.5"><span className="size-2.5 rounded-sm bg-destructive/80" /> Blocked</li>
          <li className="flex items-center gap-1.5"><span className="size-2.5 rounded-sm bg-primary/70" /> Masked, warned, logged…</li>
          <li className="flex items-center gap-1.5"><span className="size-2.5 rounded-sm bg-muted-foreground/40" /> Monitor</li>
        </ul>
      </CardContent>
    </Card>
  )
}

function EventRow({ e }: { e: SafeguardEvent }) {
  return (
    <li className="grid gap-1 rounded-lg border bg-card px-3 py-2 text-sm">
      <p className="flex flex-wrap items-center gap-1.5">
        <span className="font-medium">{e.ruleName}</span>
        <Badge variant={e.outcome === 'blocked' && !e.monitor ? 'destructive' : 'outline'}>{e.monitor ? `would be ${e.outcome}` : e.outcome}</Badge>
        <span className="text-xs text-muted-foreground">
          {stageText[e.stage]} · {e.person?.name ?? 'a key the app does not know'} · {when(e.at)}
          {e.model && <> · {e.model}</>}
        </span>
        {e.review && <Badge variant="secondary">review {e.review}</Badge>}
      </p>
      {e.detail && <p className="text-muted-foreground">Found: {e.detail}</p>}
      {e.excerpt && <p className="rounded bg-muted px-2 py-1 text-xs whitespace-pre-wrap">{e.excerpt}</p>}
      {e.justification && <p className="text-xs">Their reason: {e.justification}</p>}
      {e.conversationId && (
        <Link to={`/chat/${e.conversationId}`} className="text-xs underline underline-offset-2">
          The chat
        </Link>
      )}
    </li>
  )
}

function Events() {
  const [outcome, setOutcome] = useState('all')
  const [stage, setStage] = useState('all')
  const filters = Object.fromEntries(Object.entries({ outcome, stage }).filter(([, v]) => v !== 'all'))
  const events = useQuery(eventsQuery(filters))
  return (
    <div className="grid gap-4">
      <Chart />
      <div className="flex flex-wrap gap-2">
        <Select value={outcome} onValueChange={setOutcome}>
          <SelectTrigger className="w-44" aria-label="What it did">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {['all', 'blocked', 'masked', 'warned', 'justified', 'logged', 'rerouted'].map((o) => (
              <SelectItem key={o} value={o}>
                {o === 'all' ? 'Whatever it did' : o}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select value={stage} onValueChange={setStage}>
          <SelectTrigger className="w-48" aria-label="Where">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">Anywhere</SelectItem>
            {(Object.keys(stageText) as Stage[]).map((s) => (
              <SelectItem key={s} value={s}>
                {stageText[s]}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>
      {events.error ? (
        <QueryError error={events.error} retry={() => events.refetch()} />
      ) : !events.data ? (
        <Skeleton className="h-32" />
      ) : events.data.length === 0 ? (
        <p className="text-sm text-muted-foreground">Nothing yet.</p>
      ) : (
        <ul className="grid gap-2" aria-label="What the rules did">
          {events.data.map((e) => (
            <EventRow key={e.id} e={e} />
          ))}
        </ul>
      )}
    </div>
  )
}

/** People's requests to review a refusal: allowed (the same message passes that rule for a day) or kept refused. */
function Reviews() {
  const queryClient = useQueryClient()
  const events = useQuery(eventsQuery({ review: 'asked' }))
  const [notes, setNotes] = useState<Record<string, string>>({})
  const decide = useMutation({
    mutationFn: ({ id, allow }: { id: string; allow: boolean }) => api(`/api/admin/safeguards/events/${id}/decide`, { method: 'POST', body: { allow, note: notes[id] || null } }),
    onSuccess: (_, v) => {
      toast.success(v.allow ? 'Allowed: they may send it again today' : 'Kept refused')
      void queryClient.invalidateQueries({ queryKey: ['admin', 'safeguards'] })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (events.error) return <QueryError error={events.error} retry={() => events.refetch()} />
  if (!events.data) return <Skeleton className="h-32" />
  if (events.data.length === 0) return <p className="text-sm text-muted-foreground">No one is waiting for a review.</p>
  return (
    <ul className="grid gap-3" aria-label="Requests to review a refusal">
      {events.data.map((e) => (
        <li key={e.id} className="grid gap-2">
          <EventRow e={e} />
          <div className="grid gap-2 rounded-lg border border-dashed p-3">
            <p className="text-sm">
              <span className="font-medium">{e.person?.name}</span> asks: {e.reviewReason}
            </p>
            <Input value={notes[e.id] ?? ''} onChange={(ev) => setNotes((n) => ({ ...n, [e.id]: ev.target.value }))} placeholder="A note for them (optional)" aria-label={`A note for ${e.person?.name}`} />
            <div className="flex gap-2">
              <Button size="sm" onClick={() => decide.mutate({ id: e.id, allow: true })}>
                Allow it
              </Button>
              <Button size="sm" variant="outline" onClick={() => decide.mutate({ id: e.id, allow: false })}>
                Keep it refused
              </Button>
            </div>
          </div>
        </li>
      ))}
    </ul>
  )
}
