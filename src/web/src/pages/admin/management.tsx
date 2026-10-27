import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type ReactNode } from 'react'
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
import { Markdown } from '../chat/markdown'
import type { CheckResult, Control } from './compliance-api'

interface Objective {
  id: string
  title: string
  check: string
  target: number
  owner: string | null
  achieved: number | null
  days: number
  met: boolean
}

interface Review {
  id: string
  heldAt: string
  attendees: string | null
  inputs: string
  minutes: string | null
  decisions: string | null
  createdBy: string
}

interface EvidenceRequest {
  id: string
  text: string
  clause: string | null
  requestedBy: string
  requestedAt: string
  answer: string | null
  answeredBy: string | null
  answeredAt: string | null
}

const day = (iso: string) => new Date(iso).toLocaleDateString()
const key = (k: string) => ['admin', 'compliance', 'management', k] as const

function Part({ title, hint, children }: { title: string; hint: string; children: ReactNode }) {
  return (
    <section aria-label={title} className="grid gap-2">
      <div>
        <h3 className="font-medium">{title}</h3>
        <p className="text-sm text-muted-foreground">{hint}</p>
      </div>
      {children}
    </section>
  )
}

function Owners({ controls, mayManage }: { controls: Control[]; mayManage: boolean }) {
  const queryClient = useQueryClient()
  const owners = useQuery({ queryKey: key('owners'), queryFn: ({ signal }) => api<Record<string, string>>('/api/admin/compliance/owners', { signal }) })
  const [edits, setEdits] = useState<Record<string, string>>({})
  const save = useMutation({
    mutationFn: ({ control, owner }: { control: string; owner: string }) => api(`/api/admin/compliance/owners/${control}`, { method: 'PUT', body: { owner } }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: key('owners') }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (!owners.data) return <Skeleton className="h-32" />
  return (
    <div className="grid gap-1.5 sm:grid-cols-2">
      {controls.map((c) => (
        <label key={c.id} className="flex items-center gap-2 text-sm">
          <code className="w-16 shrink-0 font-mono text-xs">{c.id}</code>
          <Input
            className="h-8"
            value={edits[c.id] ?? owners.data[c.id] ?? ''}
            placeholder="Owner"
            disabled={!mayManage}
            title={c.title}
            aria-label={`${c.id} owner`}
            maxLength={256}
            onChange={(e) => setEdits({ ...edits, [c.id]: e.target.value })}
            onBlur={() => edits[c.id] !== undefined && edits[c.id] !== (owners.data[c.id] ?? '') && save.mutate({ control: c.id, owner: edits[c.id]! })}
          />
        </label>
      ))}
    </div>
  )
}

function Objectives({ checks, mayManage }: { checks: CheckResult[]; mayManage: boolean }) {
  const queryClient = useQueryClient()
  const data = useQuery({ queryKey: key('objectives'), queryFn: ({ signal }) => api<Objective[]>('/api/admin/compliance/objectives', { signal }) })
  const [form, setForm] = useState({ title: '', check: checks[0]?.id ?? 'two-factor', target: 95, owner: '' })
  const add = useMutation({
    mutationFn: () => api('/api/admin/compliance/objectives', { method: 'POST', body: form }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: key('objectives') })
      setForm({ ...form, title: '', owner: '' })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: (id: string) => api(`/api/admin/compliance/objectives/${id}`, { method: 'DELETE' }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: key('objectives') }),
  })
  return (
    <div className="grid gap-3">
      {(data.data ?? []).length > 0 && (
        <ul className="divide-y rounded-lg border bg-card text-sm">
          {data.data!.map((o) => (
            <li key={o.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 p-3">
              <span className="min-w-48 flex-1 font-medium">{o.title}</span>
              <span className="font-mono text-xs text-muted-foreground">{o.check}</span>
              {o.achieved === null ? (
                <Badge variant="secondary">not measured yet</Badge>
              ) : (
                <Badge variant={o.met ? 'success' : 'warning'}>
                  {o.achieved}% of {o.days} days, target {o.target}%
                </Badge>
              )}
              {o.owner && <span className="text-xs text-muted-foreground">{o.owner}</span>}
              {mayManage && (
                <Button size="sm" variant="ghost" onClick={() => remove.mutate(o.id)}>
                  Remove
                </Button>
              )}
            </li>
          ))}
        </ul>
      )}
      {mayManage && (
        <form
          className="flex flex-wrap items-end gap-2"
          onSubmit={(e) => {
            e.preventDefault()
            add.mutate()
          }}
        >
          <Field label="Objective" className="min-w-56 flex-1">
            <Input value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} maxLength={300} placeholder="Every admin signs in with two factors" />
          </Field>
          <Field label="Measured by" className="w-60">
            <Select value={form.check} onValueChange={(v) => setForm({ ...form, check: v })}>
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {checks.map((c) => (
                  <SelectItem key={c.id} value={c.id}>
                    {c.title}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
          <Field label="Target, % of days" className="w-32">
            <Input type="number" min={1} max={100} value={form.target} onChange={(e) => setForm({ ...form, target: Number(e.target.value) })} />
          </Field>
          <Field label="Owner" className="w-40">
            <Input value={form.owner} onChange={(e) => setForm({ ...form, owner: e.target.value })} maxLength={256} />
          </Field>
          <Button type="submit" variant="outline" loading={add.isPending} disabled={!form.title.trim()}>
            Set it
          </Button>
        </form>
      )}
    </div>
  )
}

function Reviews({ mayManage }: { mayManage: boolean }) {
  const queryClient = useQueryClient()
  const data = useQuery({ queryKey: key('reviews'), queryFn: ({ signal }) => api<Review[]>('/api/admin/compliance/management-reviews', { signal }) })
  const [open, setOpen] = useState<Review | 'new' | null>(null)
  const inputs = useQuery({
    queryKey: key('inputs'),
    queryFn: ({ signal }) => api<{ inputs: string }>('/api/admin/compliance/management-reviews/inputs', { signal }),
    enabled: open === 'new',
  })
  const [form, setForm] = useState({ heldAt: new Date().toISOString().slice(0, 10), attendees: '', minutes: '', decisions: '' })
  const record = useMutation({
    mutationFn: () => api('/api/admin/compliance/management-reviews', { method: 'POST', body: { ...form, heldAt: new Date(`${form.heldAt}T12:00:00Z`).toISOString() } }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: key('reviews') })
      setOpen(null)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <div className="grid gap-3">
      <div className="flex flex-wrap items-center gap-2">
        {mayManage && <Button onClick={() => setOpen('new')}>Draw up the inputs and record a review</Button>}
      </div>
      {(data.data ?? []).length > 0 && (
        <ul className="divide-y rounded-lg border bg-card text-sm">
          {data.data!.map((r) => (
            <li key={r.id}>
              <button type="button" onClick={() => setOpen(r)} className="flex w-full flex-wrap items-center gap-x-3 p-3 text-left hover:bg-muted/50">
                <span className="font-medium">{day(r.heldAt)}</span>
                <span className="min-w-40 flex-1 truncate text-muted-foreground">{r.decisions}</span>
                <span className="text-xs text-muted-foreground">{r.attendees}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
      {open && (
        <Dialog open onOpenChange={(o) => !o && setOpen(null)}>
          <DialogContent className="sm:max-w-3xl">
            <DialogHeader>
              <DialogTitle>{open === 'new' ? 'A management review' : `The review of ${day(open.heldAt)}`}</DialogTitle>
              <DialogDescription>{open === 'new' ? 'Its inputs as the system stands now, then who took part, the minutes and the decisions.' : `Took part: ${open.attendees}`}</DialogDescription>
            </DialogHeader>
            <div className="grid max-h-[68vh] gap-4 overflow-y-auto pr-1 text-sm">
              <div className="rounded-lg border p-3">
                {open === 'new' ? inputs.data ? <Markdown text={inputs.data.inputs} /> : <Skeleton className="h-40" /> : <Markdown text={open.inputs} />}
              </div>
              {open === 'new' ? (
                <>
                  <div className="grid gap-4 sm:grid-cols-[10rem_1fr]">
                    <Field label="Held on">
                      <Input type="date" value={form.heldAt} onChange={(e) => setForm({ ...form, heldAt: e.target.value })} />
                    </Field>
                    <Field label="Who took part">
                      <Input value={form.attendees} onChange={(e) => setForm({ ...form, attendees: e.target.value })} maxLength={2000} />
                    </Field>
                  </div>
                  <Field label="Minutes">
                    <Textarea value={form.minutes} onChange={(e) => setForm({ ...form, minutes: e.target.value })} rows={4} maxLength={20000} />
                  </Field>
                  <Field label="Decisions" hint="Changes to the system, resources, what is to be improved.">
                    <Textarea value={form.decisions} onChange={(e) => setForm({ ...form, decisions: e.target.value })} rows={3} maxLength={10000} />
                  </Field>
                </>
              ) : (
                <>
                  {open.minutes && <Markdown text={`## Minutes\n\n${open.minutes}`} />}
                  <Markdown text={`## Decisions\n\n${open.decisions ?? ''}`} />
                </>
              )}
            </div>
            <DialogFooter>
              <Button variant="outline" onClick={() => setOpen(null)}>
                Close
              </Button>
              {open === 'new' && (
                <Button loading={record.isPending} disabled={!form.attendees.trim() || !form.decisions.trim()} onClick={() => record.mutate()}>
                  Record it
                </Button>
              )}
            </DialogFooter>
          </DialogContent>
        </Dialog>
      )}
    </div>
  )
}

function Requests({ mayManage }: { mayManage: boolean }) {
  const queryClient = useQueryClient()
  const data = useQuery({ queryKey: key('requests'), queryFn: ({ signal }) => api<EvidenceRequest[]>('/api/admin/compliance/evidence-requests', { signal }) })
  const [ask, setAsk] = useState({ text: '', clause: '' })
  const [answers, setAnswers] = useState<Record<string, string>>({})
  const refresh = () => void queryClient.invalidateQueries({ queryKey: key('requests') })
  const send = useMutation({
    mutationFn: () => api('/api/admin/compliance/evidence-requests', { method: 'POST', body: ask }),
    onSuccess: () => {
      refresh()
      setAsk({ text: '', clause: '' })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const answer = useMutation({
    mutationFn: (id: string) => api(`/api/admin/compliance/evidence-requests/${id}/answer`, { method: 'POST', body: { answer: answers[id] } }),
    onSuccess: refresh,
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <div className="grid gap-3">
      {(data.data ?? []).length > 0 && (
        <ul className="divide-y rounded-lg border bg-card text-sm">
          {data.data!.map((r) => (
            <li key={r.id} className="grid gap-2 p-3">
              <div className="flex flex-wrap items-center gap-x-3">
                <span className="min-w-48 flex-1 font-medium">{r.text}</span>
                {r.clause && <span className="font-mono text-xs text-muted-foreground">{r.clause}</span>}
                <span className="text-xs text-muted-foreground">
                  {r.requestedBy}, {day(r.requestedAt)}
                </span>
              </div>
              {r.answer ? (
                <p className={cn('text-muted-foreground')}>
                  <strong className="font-medium text-foreground">{r.answeredBy}:</strong> {r.answer}
                </p>
              ) : mayManage ? (
                <div className="flex gap-2">
                  <Input value={answers[r.id] ?? ''} placeholder="Where the evidence is" aria-label="Answer" maxLength={8000} onChange={(e) => setAnswers({ ...answers, [r.id]: e.target.value })} />
                  <Button size="sm" variant="outline" disabled={!answers[r.id]?.trim()} loading={answer.isPending} onClick={() => answer.mutate(r.id)}>
                    Answer
                  </Button>
                </div>
              ) : (
                <Badge variant="warning" className="w-fit">
                  waiting for an answer
                </Badge>
              )}
            </li>
          ))}
        </ul>
      )}
      <form
        className="flex flex-wrap items-end gap-2"
        onSubmit={(e) => {
          e.preventDefault()
          send.mutate()
        }}
      >
        <Field label="Ask for evidence" className="min-w-64 flex-1">
          <Input value={ask.text} onChange={(e) => setAsk({ ...ask, text: e.target.value })} maxLength={4000} placeholder="Last quarter's access review, with its decisions" />
        </Field>
        <Field label="Clause" className="w-44">
          <Input value={ask.clause} onChange={(e) => setAsk({ ...ask, clause: e.target.value })} maxLength={64} placeholder="iso27001 A.5.18" />
        </Field>
        <Button type="submit" variant="outline" loading={send.isPending} disabled={!ask.text.trim()}>
          Ask
        </Button>
      </form>
    </div>
  )
}

/** Admin → Compliance → Management: owners, objectives, management reviews and the auditors' requests. */
export function ManagementSection({ mayManage, controls, checks }: { mayManage: boolean; controls: Control[]; checks: CheckResult[] }) {
  const reviews = useQuery({ queryKey: key('reviews'), queryFn: ({ signal }) => api<Review[]>('/api/admin/compliance/management-reviews', { signal }) })
  if (reviews.error) return <QueryError error={reviews.error} retry={() => reviews.refetch()} />
  return (
    <section aria-labelledby="management-title" className="grid gap-6">
      <div>
        <h2 id="management-title" className="text-lg font-semibold">
          Running the management system
        </h2>
        <p className="text-sm text-muted-foreground">Who owns each control, the objectives and how they are met, the management reviews, and what auditors ask for.</p>
      </div>
      <Part title="Management reviews" hint="At least once a year: the inputs the clause names, drawn up as the system stands, then the decisions.">
        <Reviews mayManage={mayManage} />
      </Part>
      <Part title="Security objectives" hint="Each measured by a check: the share of the last 90 days it held.">
        <Objectives checks={checks} mayManage={mayManage} />
      </Part>
      <Part title="Auditors' requests" hint="An auditor asks for evidence here, and is answered.">
        <Requests mayManage={mayManage} />
      </Part>
      <Part title="Owners" hint="Who in the company runs each control's part and answers for it.">
        <Owners controls={controls} mayManage={mayManage} />
      </Part>
    </section>
  )
}
