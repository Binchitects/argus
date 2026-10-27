import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Plus, X } from 'lucide-react'
import { useState } from 'react'
import { QueryError } from '@/components/app/query-state'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'

export type Level = 'low' | 'medium' | 'high' | 'critical'

export interface Risk {
  id: string
  title: string
  description: string | null
  category: string
  assets: string[]
  threat: string | null
  likelihood: number
  impact: number
  score: number
  level: Level
  owner: string | null
  treatment: 'reduce' | 'accept' | 'transfer' | 'avoid'
  controls: string[]
  plan: string | null
  residualLikelihood: number | null
  residualImpact: number | null
  residualScore: number | null
  residualLevel: Level | null
  acceptedBy: string | null
  acceptedWhy: string | null
  acceptedUntil: string | null
  reviewDue: string
  reviewOverdue: boolean
  closedAt: string | null
  createdBy: string
  createdAt: string
  updatedAt: string
  openActions: number
  lateActions: number
}

interface RiskAction {
  id: string
  text: string
  owner: string | null
  due: string | null
  doneAt: string | null
  doneBy: string | null
}

interface RiskEvent {
  id: string
  at: string
  by: string
  kind: string
  text: string | null
}

const CATEGORIES = ['security', 'privacy', 'ai', 'continuity', 'supplier', 'legal', 'other'] as const
const TREATMENTS: Record<Risk['treatment'], string> = { reduce: 'Reduce, with controls', accept: 'Accept', transfer: 'Transfer (insurance, a contract)', avoid: 'Avoid (stop doing it)' }
const LIKELIHOOD = ['', 'Rare', 'Unlikely', 'Possible', 'Likely', 'Almost certain']
const IMPACT = ['', 'Negligible', 'Minor', 'Moderate', 'Major', 'Severe']
const LEVEL_BADGE: Record<Level, 'success' | 'warning' | 'destructive'> = { low: 'success', medium: 'warning', high: 'destructive', critical: 'destructive' }
const EVENT_WORDS: Record<string, string> = {
  created: 'Registered', changed: 'Changed', reviewed: 'Reviewed', accepted: 'Accepted', action: 'Action planned', action_done: 'Action done', closed: 'Closed', reopened: 'Reopened',
}

/** The level of a score, as the register sets it: low 1–4, medium 5–9, high 10–16, critical 20–25. */
const levelOf = (score: number): Level => (score >= 20 ? 'critical' : score >= 10 ? 'high' : score >= 5 ? 'medium' : 'low')

const CELL: Record<Level, string> = {
  low: 'bg-success/15',
  medium: 'bg-warning/20',
  high: 'bg-destructive/15',
  critical: 'bg-destructive/30',
}

const risksQuery = {
  queryKey: ['admin', 'compliance', 'risks'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Risk[]>('/api/admin/compliance/risks', { signal }),
}

const riskQuery = (id: string) => ({
  queryKey: ['admin', 'compliance', 'risks', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ risk: Risk; actions: RiskAction[]; events: RiskEvent[] }>(`/api/admin/compliance/risks/${id}`, { signal }),
})

const day = (iso: string) => new Date(iso).toLocaleDateString()

/** Likelihood (across) by impact (down), each cell the open risks there, its colour its level; a cell picked filters the list. */
function HeatMap({ risks, residual, cell, onCell }: { risks: Risk[]; residual: boolean; cell: string | null; onCell: (c: string | null) => void }) {
  const at = (r: Risk) => (residual && r.residualLikelihood && r.residualImpact ? `${r.residualLikelihood}-${r.residualImpact}` : `${r.likelihood}-${r.impact}`)
  const counts = new Map<string, number>()
  for (const r of risks) counts.set(at(r), (counts.get(at(r)) ?? 0) + 1)
  return (
    <table className="w-fit border-separate border-spacing-1 text-xs">
      <caption className="sr-only">Heat map of the open risks, {residual ? 'as left after treatment' : 'before treatment'}: impact by row, likelihood by column</caption>
      <tbody>
        {[5, 4, 3, 2, 1].map((i) => (
          <tr key={i}>
            <th scope="row" className="pr-1 text-right font-normal text-muted-foreground" title={IMPACT[i]}>
              {i}
            </th>
            {[1, 2, 3, 4, 5].map((l) => {
              const key = `${l}-${i}`
              const n = counts.get(key) ?? 0
              return (
                <td key={key} className="p-0">
                  <button
                    type="button"
                    aria-label={`Likelihood ${l}, impact ${i}: ${n} risk${n === 1 ? '' : 's'}`}
                    aria-pressed={cell === key}
                    disabled={n === 0}
                    onClick={() => onCell(cell === key ? null : key)}
                    className={cn('grid size-11 place-items-center rounded-md font-medium tabular-nums', CELL[levelOf(l * i)], cell === key && 'ring-2 ring-primary', n === 0 && 'text-transparent')}
                  >
                    {n}
                  </button>
                </td>
              )
            })}
          </tr>
        ))}
      </tbody>
      <tfoot>
        <tr>
          <th scope="col" className="sr-only">
            Impact
          </th>
          {[1, 2, 3, 4, 5].map((l) => (
            <th key={l} scope="col" className="font-normal text-muted-foreground" title={LIKELIHOOD[l]}>
              {l}
            </th>
          ))}
        </tr>
        <tr>
          <th scope="row" className="sr-only">
            Axis
          </th>
          <td colSpan={5} className="text-center text-muted-foreground">
            likelihood → (impact ↑)
          </td>
        </tr>
      </tfoot>
    </table>
  )
}

function ScoreSelect({ label, value, words, onChange, optional }: { label: string; value: number | null; words: string[]; onChange: (v: number | null) => void; optional?: boolean }) {
  return (
    <Field label={label}>
      <Select value={value === null ? 'none' : String(value)} onValueChange={(v) => onChange(v === 'none' ? null : Number(v))}>
        <SelectTrigger>
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          {optional && <SelectItem value="none">Not assessed yet</SelectItem>}
          {[1, 2, 3, 4, 5].map((n) => (
            <SelectItem key={n} value={String(n)}>
              {n}, {words[n]}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </Field>
  )
}

/** A new risk, or one changed: what could happen to what, how likely and bad before and after, its owner and treatment. */
function RiskEditor({ risk, controls, onClose }: { risk: Risk | null; controls: string[]; onClose: () => void }) {
  const queryClient = useQueryClient()
  const assetsQuery = useQuery({
    queryKey: ['admin', 'compliance', 'risks', 'assets'],
    queryFn: ({ signal }) => api<{ group: string; name: string }[]>('/api/admin/compliance/risks/assets', { signal }),
  })
  const [form, setForm] = useState(() => ({
    title: risk?.title ?? '',
    description: risk?.description ?? '',
    category: risk?.category ?? 'security',
    assets: risk?.assets ?? [],
    threat: risk?.threat ?? '',
    likelihood: risk?.likelihood ?? 3,
    impact: risk?.impact ?? 3,
    owner: risk?.owner ?? '',
    treatment: risk?.treatment ?? 'reduce',
    controls: risk?.controls ?? [],
    plan: risk?.plan ?? '',
    residualLikelihood: risk?.residualLikelihood ?? null,
    residualImpact: risk?.residualImpact ?? null,
  }))
  const [asset, setAsset] = useState('')
  const set = <K extends keyof typeof form>(k: K, v: (typeof form)[K]) => setForm((f) => ({ ...f, [k]: v }))
  const save = useMutation({
    mutationFn: () =>
      api<Risk>(risk ? `/api/admin/compliance/risks/${risk.id}` : '/api/admin/compliance/risks', {
        method: risk ? 'PUT' : 'POST',
        body: { ...form, residualLikelihood: form.residualImpact === null ? null : form.residualLikelihood, residualImpact: form.residualLikelihood === null ? null : form.residualImpact },
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: risksQuery.queryKey })
      toast.success(risk ? 'Risk saved.' : 'Risk registered.')
      onClose()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const addAsset = (name: string) => {
    const n = name.trim()
    if (n && !form.assets.includes(n)) set('assets', [...form.assets, n])
    setAsset('')
  }
  const toggleControl = (id: string) => set('controls', form.controls.includes(id) ? form.controls.filter((c) => c !== id) : [...form.controls, id])
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{risk ? 'Change the risk' : 'Register a risk'}</DialogTitle>
          <DialogDescription>What could happen to what, how likely and how bad before and after treatment (1 to 5 each), who owns it and how it is treated.</DialogDescription>
        </DialogHeader>
        <form
          className="grid max-h-[70vh] gap-4 overflow-y-auto pr-1"
          onSubmit={(e) => {
            e.preventDefault()
            save.mutate()
          }}
        >
          <div className="grid gap-4 sm:grid-cols-[1fr_12rem]">
            <Field label="The risk">
              <Input value={form.title} onChange={(e) => set('title', e.target.value)} maxLength={300} required />
            </Field>
            <Field label="Category">
              <Select value={form.category} onValueChange={(v) => set('category', v)}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {CATEGORIES.map((c) => (
                    <SelectItem key={c} value={c}>
                      {c === 'ai' ? 'AI' : c}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
          </div>
          <Field label="What could happen">
            <Textarea value={form.description} onChange={(e) => set('description', e.target.value)} rows={2} maxLength={4000} />
          </Field>
          <Field label="The threat" hint="Who or what: phishing, an insider, a model's mistake, a supplier's outage.">
            <Input value={form.threat} onChange={(e) => set('threat', e.target.value)} maxLength={2000} />
          </Field>
          <Field label="What it threatens" hint="What Arena knows of, or any other name.">
            <div className="grid gap-2">
              {form.assets.length > 0 && (
                <ul className="flex flex-wrap gap-1.5" aria-label="Assets">
                  {form.assets.map((a) => (
                    <li key={a}>
                      <Badge variant="secondary" className="gap-1">
                        {a}
                        <button type="button" aria-label={`Take out ${a}`} onClick={() => set('assets', form.assets.filter((x) => x !== a))}>
                          <X className="size-3" />
                        </button>
                      </Badge>
                    </li>
                  ))}
                </ul>
              )}
              <div className="flex gap-2">
                <Input
                  value={asset}
                  onChange={(e) => setAsset(e.target.value)}
                  list="risk-assets"
                  placeholder="Arena's database, a model, a knowledge source…"
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') {
                      e.preventDefault()
                      addAsset(asset)
                    }
                  }}
                />
                <Button type="button" variant="outline" onClick={() => addAsset(asset)} disabled={!asset.trim()}>
                  Add
                </Button>
                <datalist id="risk-assets">
                  {(assetsQuery.data ?? []).map((a) => (
                    <option key={a.name} value={a.name}>
                      {a.group}
                    </option>
                  ))}
                </datalist>
              </div>
            </div>
          </Field>
          <div className="grid gap-4 sm:grid-cols-2">
            <ScoreSelect label="Likelihood, before treatment" value={form.likelihood} words={LIKELIHOOD} onChange={(v) => set('likelihood', v ?? 1)} />
            <ScoreSelect label="Impact, before treatment" value={form.impact} words={IMPACT} onChange={(v) => set('impact', v ?? 1)} />
          </div>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Owner">
              <Input value={form.owner} onChange={(e) => set('owner', e.target.value)} maxLength={256} />
            </Field>
            <Field label="Treatment">
              <Select value={form.treatment} onValueChange={(v) => set('treatment', v as Risk['treatment'])}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {Object.entries(TREATMENTS).map(([k, label]) => (
                    <SelectItem key={k} value={k}>
                      {label}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
          </div>
          <fieldset className="grid gap-2">
            <legend className="mb-1 text-sm font-medium">Arena's controls that treat it</legend>
            <div className="flex flex-wrap gap-1.5">
              {controls.map((id) => (
                <button
                  key={id}
                  type="button"
                  aria-pressed={form.controls.includes(id)}
                  onClick={() => toggleControl(id)}
                  className={cn('rounded-md border px-2 py-0.5 font-mono text-xs', form.controls.includes(id) ? 'border-primary bg-primary/10 text-primary-ink' : 'text-muted-foreground hover:border-primary/50')}
                >
                  {id}
                </button>
              ))}
            </div>
          </fieldset>
          <Field label="The plan">
            <Textarea value={form.plan} onChange={(e) => set('plan', e.target.value)} rows={2} maxLength={4000} />
          </Field>
          <div className="grid gap-4 sm:grid-cols-2">
            <ScoreSelect label="Likelihood left after treatment" value={form.residualLikelihood} words={LIKELIHOOD} optional onChange={(v) => set('residualLikelihood', v)} />
            <ScoreSelect label="Impact left after treatment" value={form.residualImpact} words={IMPACT} optional onChange={(v) => set('residualImpact', v)} />
          </div>
          <p className="text-sm text-muted-foreground">
            Level now: <strong>{levelOf(form.likelihood * form.impact)}</strong> ({form.likelihood * form.impact})
            {form.residualLikelihood !== null && form.residualImpact !== null && (
              <>
                , left after treatment: <strong>{levelOf(form.residualLikelihood * form.residualImpact)}</strong> ({form.residualLikelihood * form.residualImpact})
              </>
            )}
            . Reviewed again in {levelOf((form.residualLikelihood ?? form.likelihood) * (form.residualImpact ?? form.impact)) === 'critical' ? 'three months' : levelOf((form.residualLikelihood ?? form.likelihood) * (form.residualImpact ?? form.impact)) === 'high' ? 'six months' : 'a year'}.
          </p>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={save.isPending} disabled={!form.title.trim()}>
              {risk ? 'Save' : 'Register it'}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

/** One step a person takes on a risk, with what it needs said. */
function StepDialog({ title, description, fields, onSubmit, onClose, pending }: {
  title: string
  description: string
  fields: { key: string; label: string; type?: 'date' | 'text'; required?: boolean; hint?: string }[]
  onSubmit: (values: Record<string, string>) => void
  onClose: () => void
  pending: boolean
}) {
  const [values, setValues] = useState<Record<string, string>>({})
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>{description}</DialogDescription>
        </DialogHeader>
        <form
          className="grid gap-4"
          onSubmit={(e) => {
            e.preventDefault()
            onSubmit(values)
          }}
        >
          {fields.map((f) => (
            <Field key={f.key} label={f.label} hint={f.hint}>
              {f.type === 'date' ? (
                <Input type="date" value={values[f.key] ?? ''} onChange={(e) => setValues((v) => ({ ...v, [f.key]: e.target.value }))} required={f.required} />
              ) : (
                <Textarea value={values[f.key] ?? ''} onChange={(e) => setValues((v) => ({ ...v, [f.key]: e.target.value }))} rows={3} maxLength={2000} required={f.required} />
              )}
            </Field>
          ))}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={pending}>
              {title}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

const isoDay = (d: string | undefined) => (d ? new Date(`${d}T00:00:00Z`).toISOString() : undefined)

/** A risk with its treatment's actions and its history, and what a person may do to it. */
function RiskDetail({ id, controls, mayManage, onClose }: { id: string; controls: string[]; mayManage: boolean; onClose: () => void }) {
  const queryClient = useQueryClient()
  const data = useQuery(riskQuery(id))
  const [step, setStep] = useState<'edit' | 'review' | 'accept' | 'close' | 'reopen' | 'action' | null>(null)
  const done = () => {
    void queryClient.invalidateQueries({ queryKey: risksQuery.queryKey })
    setStep(null)
  }
  const act = useMutation({
    mutationFn: ({ path, body }: { path: string; body?: object }) => api(`/api/admin/compliance/risks/${id}${path}`, { method: 'POST', body: body ?? {} }),
    onSuccess: done,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const r = data.data?.risk
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-2xl">
        {data.error ? (
          <QueryError error={data.error} retry={() => data.refetch()} />
        ) : !r ? (
          <Skeleton className="h-64" />
        ) : (
          <>
            <DialogHeader>
              <DialogTitle>{r.title}</DialogTitle>
              <DialogDescription>
                {r.category === 'ai' ? 'AI' : r.category} · owner {r.owner ?? 'not named'} · registered by {r.createdBy} on {day(r.createdAt)}
              </DialogDescription>
            </DialogHeader>
            <div className="grid max-h-[65vh] gap-4 overflow-y-auto pr-1 text-sm">
              <div className="flex flex-wrap items-center gap-2">
                <Badge variant={LEVEL_BADGE[r.level]}>
                  {r.level}, {r.likelihood}×{r.impact}
                </Badge>
                {r.residualLevel && (
                  <>
                    <span className="text-muted-foreground">→ left</span>
                    <Badge variant={LEVEL_BADGE[r.residualLevel]}>
                      {r.residualLevel}, {r.residualLikelihood}×{r.residualImpact}
                    </Badge>
                  </>
                )}
                <Badge variant="secondary">{TREATMENTS[r.treatment]}</Badge>
                {r.closedAt ? <Badge variant="success">closed {day(r.closedAt)}</Badge> : <span className={cn('text-xs', r.reviewOverdue && 'font-medium text-destructive-ink')}>review {r.reviewOverdue ? 'past due since' : 'due'} {day(r.reviewDue)}</span>}
              </div>
              {r.description && <p>{r.description}</p>}
              <dl className="grid gap-x-4 gap-y-1 sm:grid-cols-[9rem_1fr]">
                {r.threat && (
                  <>
                    <dt className="text-muted-foreground">Threat</dt>
                    <dd>{r.threat}</dd>
                  </>
                )}
                <dt className="text-muted-foreground">Threatens</dt>
                <dd>{r.assets.length ? r.assets.join(', ') : 'not named'}</dd>
                <dt className="text-muted-foreground">Controls</dt>
                <dd className="font-mono text-xs">{r.controls.length ? r.controls.join(' ') : 'none named'}</dd>
                {r.plan && (
                  <>
                    <dt className="text-muted-foreground">Plan</dt>
                    <dd>{r.plan}</dd>
                  </>
                )}
                {r.acceptedBy && (
                  <>
                    <dt className="text-muted-foreground">Accepted</dt>
                    <dd>
                      by {r.acceptedBy} until {r.acceptedUntil && day(r.acceptedUntil)}: {r.acceptedWhy}
                    </dd>
                  </>
                )}
              </dl>
              <section aria-label="Actions" className="grid gap-2">
                <h3 className="font-medium">The treatment's actions</h3>
                {data.data!.actions.length === 0 ? (
                  <p className="text-muted-foreground">None yet.</p>
                ) : (
                  <ul className="divide-y rounded-lg border">
                    {data.data!.actions.map((a) => (
                      <li key={a.id} className="flex flex-wrap items-center gap-2 p-2">
                        <span className={cn('min-w-40 flex-1', a.doneAt && 'text-muted-foreground line-through')}>{a.text}</span>
                        {a.owner && <span className="text-xs text-muted-foreground">{a.owner}</span>}
                        {a.due && <span className={cn('text-xs', !a.doneAt && new Date(a.due) < new Date() && 'font-medium text-destructive-ink')}>by {day(a.due)}</span>}
                        {a.doneAt ? (
                          <span className="text-xs text-success-ink">done by {a.doneBy}</span>
                        ) : (
                          mayManage &&
                          !r.closedAt && (
                            <Button size="sm" variant="outline" onClick={() => act.mutate({ path: `/actions/${a.id}/done` })}>
                              Done
                            </Button>
                          )
                        )}
                      </li>
                    ))}
                  </ul>
                )}
              </section>
              <section aria-label="History" className="grid gap-2">
                <h3 className="font-medium">History</h3>
                <ol className="grid gap-1.5">
                  {data.data!.events.map((e) => (
                    <li key={e.id} className="grid gap-x-3 sm:grid-cols-[9rem_1fr]">
                      <span className="text-xs text-muted-foreground">{new Date(e.at).toLocaleString()}</span>
                      <span>
                        <strong className="font-medium">{EVENT_WORDS[e.kind] ?? e.kind}</strong> by {e.by}
                        {e.text ? `: ${e.text}` : ''}
                      </span>
                    </li>
                  ))}
                </ol>
              </section>
            </div>
            {mayManage && (
              <DialogFooter className="flex-wrap gap-2 sm:justify-start">
                {r.closedAt ? (
                  <Button variant="outline" onClick={() => setStep('reopen')}>
                    Reopen
                  </Button>
                ) : (
                  <>
                    <Button onClick={() => setStep('review')}>Review</Button>
                    <Button variant="outline" onClick={() => setStep('edit')}>
                      Change
                    </Button>
                    <Button variant="outline" onClick={() => setStep('action')}>
                      <Plus /> Action
                    </Button>
                    <Button variant="outline" onClick={() => setStep('accept')}>
                      Accept
                    </Button>
                    <Button variant="outline" onClick={() => setStep('close')}>
                      Close
                    </Button>
                  </>
                )}
              </DialogFooter>
            )}
          </>
        )}
        {r && step === 'edit' && <RiskEditor risk={r} controls={controls} onClose={() => { setStep(null); void queryClient.invalidateQueries({ queryKey: ['admin', 'compliance', 'risks', id] }) }} />}
        {step === 'review' && (
          <StepDialog
            title="Review"
            description="What the review found. The next review is the date its level allows, unless you set an earlier one."
            fields={[{ key: 'note', label: 'What the review found', required: true }, { key: 'next', label: 'Next review (optional)', type: 'date' }]}
            pending={act.isPending}
            onClose={() => setStep(null)}
            onSubmit={(v) => act.mutate({ path: '/review', body: { note: v.note, next: isoDay(v.next) } })}
          />
        )}
        {step === 'accept' && (
          <StepDialog
            title="Accept"
            description="You accept it as it is left, in your name: why, and until when (a year at most). It is decided again then."
            fields={[{ key: 'why', label: 'Why it is accepted', required: true }, { key: 'until', label: 'Until', type: 'date', hint: 'Empty: a year from today.' }]}
            pending={act.isPending}
            onClose={() => setStep(null)}
            onSubmit={(v) => act.mutate({ path: '/accept', body: { why: v.why, until: isoDay(v.until) } })}
          />
        )}
        {(step === 'close' || step === 'reopen') && (
          <StepDialog
            title={step === 'close' ? 'Close' : 'Reopen'}
            description={step === 'close' ? 'A risk that no longer applies stays in the register, closed, with its history.' : 'It applies again: it is reviewed on its level again.'}
            fields={[{ key: 'why', label: step === 'close' ? 'Why it no longer applies' : 'Why it applies again', required: true }]}
            pending={act.isPending}
            onClose={() => setStep(null)}
            onSubmit={(v) => act.mutate({ path: step === 'close' ? '/close' : '/reopen', body: { why: v.why } })}
          />
        )}
        {step === 'action' && <ActionDialog pending={act.isPending} onClose={() => setStep(null)} onSubmit={(body) => act.mutate({ path: '/actions', body })} />}
      </DialogContent>
    </Dialog>
  )
}

function ActionDialog({ pending, onClose, onSubmit }: { pending: boolean; onClose: () => void; onSubmit: (body: { text: string; owner?: string; due?: string }) => void }) {
  const [text, setText] = useState('')
  const [owner, setOwner] = useState('')
  const [due, setDue] = useState('')
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>A step of the treatment</DialogTitle>
          <DialogDescription>What is to be done, who does it, and by when.</DialogDescription>
        </DialogHeader>
        <form
          className="grid gap-4"
          onSubmit={(e) => {
            e.preventDefault()
            onSubmit({ text, owner: owner || undefined, due: isoDay(due) })
          }}
        >
          <Field label="What is to be done">
            <Input value={text} onChange={(e) => setText(e.target.value)} maxLength={2000} required />
          </Field>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Who">
              <Input value={owner} onChange={(e) => setOwner(e.target.value)} maxLength={256} />
            </Field>
            <Field label="By">
              <Input type="date" value={due} onChange={(e) => setDue(e.target.value)} />
            </Field>
          </div>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={pending} disabled={!text.trim()}>
              Add it
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

/** Admin → Compliance → Risks: the heat map, the register, and each risk's treatment, actions and history. */
export function RisksSection({ mayManage, controls }: { mayManage: boolean; controls: string[] }) {
  const data = useQuery(risksQuery)
  const [residual, setResidual] = useState(true)
  const [cell, setCell] = useState<string | null>(null)
  const [closed, setClosed] = useState(false)
  const [editing, setEditing] = useState(false)
  const [open, setOpen] = useState<string | null>(null)
  const all = data.data ?? []
  const live = all.filter((r) => !r.closedAt)
  const where = (r: Risk) => (residual && r.residualLikelihood && r.residualImpact ? `${r.residualLikelihood}-${r.residualImpact}` : `${r.likelihood}-${r.impact}`)
  const shown = (closed ? all : live).filter((r) => !cell || where(r) === cell)
  const due = live.filter((r) => r.reviewOverdue || r.lateActions > 0).length
  return (
    <section aria-labelledby="risks-title" className="grid gap-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <h2 id="risks-title" className="text-lg font-semibold">
            Risks
          </h2>
          <p className="text-sm text-muted-foreground">
            The register: each risk to the company's information and its AI, how likely and how bad before and after treatment, its owner, treatment and next review.
          </p>
        </div>
        {mayManage && (
          <Button onClick={() => setEditing(true)}>
            <Plus /> Register a risk
          </Button>
        )}
      </div>
      {data.error ? (
        <QueryError error={data.error} retry={() => data.refetch()} />
      ) : data.isPending ? (
        <Skeleton className="h-64" />
      ) : all.length === 0 ? (
        <p className="rounded-lg border bg-card p-6 text-sm text-muted-foreground">
          The register is empty. Start with the risks most installations share (the risk management policy lists them): an account taken over, an admin misusing their access, personal data reaching an outside model, data lost.
        </p>
      ) : (
        <div className="grid gap-6 lg:grid-cols-[auto_1fr]">
          <div className="grid content-start gap-3">
            <fieldset className="flex gap-1 text-sm">
              <legend className="sr-only">Which levels</legend>
              <Button size="sm" variant={residual ? 'default' : 'outline'} aria-pressed={residual} onClick={() => setResidual(true)}>
                Left after treatment
              </Button>
              <Button size="sm" variant={residual ? 'outline' : 'default'} aria-pressed={!residual} onClick={() => setResidual(false)}>
                Before
              </Button>
            </fieldset>
            <HeatMap risks={live} residual={residual} cell={cell} onCell={setCell} />
            <p className="max-w-64 text-xs text-muted-foreground">
              {live.length} open{due > 0 ? `, ${due} with a review or an action past due` : ''}. A cell filters the list.
            </p>
          </div>
          <div className="grid content-start gap-2">
            <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
              <span className="text-muted-foreground">
                {shown.length} shown{cell && ' (one cell)'}
              </span>
              <label className="flex items-center gap-2">
                <input type="checkbox" checked={closed} onChange={(e) => setClosed(e.target.checked)} /> Closed ones too
              </label>
            </div>
            <ul className="divide-y rounded-lg border bg-card">
              {shown.map((r) => (
                <li key={r.id}>
                  <button type="button" onClick={() => setOpen(r.id)} className="flex w-full flex-wrap items-center gap-x-3 gap-y-1 p-3 text-left text-sm hover:bg-muted/50">
                    <span className={cn('min-w-48 flex-1 font-medium', r.closedAt && 'text-muted-foreground line-through')}>{r.title}</span>
                    <Badge variant={LEVEL_BADGE[r.residualLevel ?? r.level]}>{r.residualLevel ?? r.level}</Badge>
                    <span className="text-xs text-muted-foreground">{r.category === 'ai' ? 'AI' : r.category}</span>
                    <span className="text-xs text-muted-foreground">{r.treatment}</span>
                    {r.owner && <span className="text-xs text-muted-foreground">{r.owner}</span>}
                    {!r.closedAt && <span className={cn('text-xs', r.reviewOverdue ? 'font-medium text-destructive-ink' : 'text-muted-foreground')}>review {day(r.reviewDue)}</span>}
                    {r.lateActions > 0 && <span className="text-xs font-medium text-destructive-ink">{r.lateActions} late</span>}
                  </button>
                </li>
              ))}
            </ul>
          </div>
        </div>
      )}
      {editing && <RiskEditor risk={null} controls={controls} onClose={() => setEditing(false)} />}
      {open && <RiskDetail id={open} controls={controls} mayManage={mayManage} onClose={() => setOpen(null)} />}
    </section>
  )
}
