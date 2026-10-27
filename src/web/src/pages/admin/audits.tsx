import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Plus } from 'lucide-react'
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
import { FRAMEWORKS } from './compliance-api'

interface Audit {
  id: string
  title: string
  kind: string
  framework: string | null
  scope: string | null
  auditor: string | null
  starts: string
  ends: string | null
  conclusion: string | null
  reportedAt: string | null
  findings: number
  openFindings: number
}

interface Finding {
  id: string
  auditId: string | null
  audit: string | null
  title: string
  kind: 'major' | 'minor' | 'observation' | 'opportunity'
  description: string | null
  clause: string | null
  control: string | null
  owner: string | null
  due: string | null
  late: boolean
  correction: string | null
  rootCause: string | null
  correctiveAction: string | null
  status: 'open' | 'done' | 'verified'
  doneBy: string | null
  verifiedBy: string | null
  verification: string | null
}

interface Certificate {
  id: string
  framework: string
  body: string
  number: string | null
  scope: string | null
  issued: string
  expires: string
  daysLeft: number
}

interface Waiver {
  id: string
  control: string
  reason: string
  compensating: string | null
  approvedBy: string
  until: string
  withdrawnAt: string | null
  expired: boolean
}

interface AuditsView {
  audits: Audit[]
  findings: Finding[]
  certificates: Certificate[]
  waivers: Waiver[]
}

const KINDS: Record<string, string> = {
  internal: 'Internal', stage1: 'Certification, stage 1', stage2: 'Certification, stage 2', surveillance: 'Surveillance', recertification: 'Recertification', soc2: 'SOC 2 period', assessment: 'Assessment', other: 'Other',
}
const FINDING_KINDS: Record<Finding['kind'], string> = { major: 'Major nonconformity', minor: 'Minor nonconformity', observation: 'Observation', opportunity: 'Opportunity to improve' }
const FINDING_BADGE: Record<Finding['kind'], 'destructive' | 'warning' | 'secondary'> = { major: 'destructive', minor: 'warning', observation: 'secondary', opportunity: 'secondary' }
const label = (key: string | null) => FRAMEWORKS.find((f) => f.key === key)?.label ?? key ?? ''
const day = (iso: string) => new Date(iso).toLocaleDateString()
const isoDay = (d: string) => (d ? new Date(`${d}T00:00:00Z`).toISOString() : null)
const dateInput = (iso: string | null) => (iso ? iso.slice(0, 10) : '')

const auditsQuery = {
  queryKey: ['admin', 'compliance', 'audits'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<AuditsView>('/api/admin/compliance/audits', { signal }),
}

function FormDialog({ title, description, onClose, onSubmit, pending, children, wide }: {
  title: string
  description: string
  onClose: () => void
  onSubmit: () => void
  pending: boolean
  children: ReactNode
  wide?: boolean
}) {
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className={cn(wide && 'sm:max-w-2xl')}>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>{description}</DialogDescription>
        </DialogHeader>
        <form
          className="grid max-h-[70vh] gap-4 overflow-y-auto pr-1"
          onSubmit={(e) => {
            e.preventDefault()
            onSubmit()
          }}
        >
          {children}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={pending}>
              Save
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

function Choice({ label: name, value, options, onChange }: { label: string; value: string; options: Record<string, string>; onChange: (v: string) => void }) {
  return (
    <Field label={name}>
      <Select value={value} onValueChange={onChange}>
        <SelectTrigger>
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          {Object.entries(options).map(([k, v]) => (
            <SelectItem key={k} value={k}>
              {v}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </Field>
  )
}

const FRAMEWORK_OPTIONS = { none: 'None in particular', ...Object.fromEntries(FRAMEWORKS.map((f) => [f.key, f.label])) }

/** An audit planned, or changed. */
function AuditForm({ audit, onClose }: { audit: Audit | null; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [f, setF] = useState({
    title: audit?.title ?? '', kind: audit?.kind ?? 'internal', framework: audit?.framework ?? 'none', scope: audit?.scope ?? '', auditor: audit?.auditor ?? '',
    starts: dateInput(audit?.starts ?? new Date().toISOString()), ends: dateInput(audit?.ends ?? null),
  })
  const save = useMutation({
    mutationFn: () =>
      api(audit ? `/api/admin/compliance/audits/${audit.id}` : '/api/admin/compliance/audits', {
        method: audit ? 'PUT' : 'POST',
        body: { ...f, framework: f.framework === 'none' ? null : f.framework, starts: isoDay(f.starts), ends: isoDay(f.ends) },
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: auditsQuery.queryKey })
      onClose()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <FormDialog title={audit ? 'Change the audit' : 'Plan an audit'} description="Internal, or an external body's: its framework, scope, auditor and dates." onClose={onClose} onSubmit={() => save.mutate()} pending={save.isPending}>
      <Field label="Title">
        <Input value={f.title} onChange={(e) => setF({ ...f, title: e.target.value })} maxLength={300} required />
      </Field>
      <div className="grid gap-4 sm:grid-cols-2">
        <Choice label="Kind" value={f.kind} options={KINDS} onChange={(v) => setF({ ...f, kind: v })} />
        <Choice label="Framework" value={f.framework} options={FRAMEWORK_OPTIONS} onChange={(v) => setF({ ...f, framework: v })} />
      </div>
      <Field label="Scope">
        <Textarea value={f.scope} onChange={(e) => setF({ ...f, scope: e.target.value })} rows={2} maxLength={4000} />
      </Field>
      <Field label="Auditor" hint="A person who does not audit their own work, or the body's name.">
        <Input value={f.auditor} onChange={(e) => setF({ ...f, auditor: e.target.value })} maxLength={256} />
      </Field>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Starts">
          <Input type="date" value={f.starts} onChange={(e) => setF({ ...f, starts: e.target.value })} required />
        </Field>
        <Field label="Ends">
          <Input type="date" value={f.ends} onChange={(e) => setF({ ...f, ends: e.target.value })} />
        </Field>
      </div>
    </FormDialog>
  )
}

/** A finding raised, or acted on: its correction, root cause and corrective action, then its check. */
function FindingForm({ finding, audits, mayManage, onClose }: { finding: Finding | null; audits: Audit[]; mayManage: boolean; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [f, setF] = useState({
    auditId: finding?.auditId ?? 'none', title: finding?.title ?? '', kind: finding?.kind ?? 'minor', description: finding?.description ?? '', clause: finding?.clause ?? '',
    control: finding?.control ?? '', owner: finding?.owner ?? '', due: dateInput(finding?.due ?? null), correction: finding?.correction ?? '',
    rootCause: finding?.rootCause ?? '', correctiveAction: finding?.correctiveAction ?? '',
  })
  const [note, setNote] = useState('')
  const done = () => void queryClient.invalidateQueries({ queryKey: auditsQuery.queryKey })
  const save = useMutation({
    mutationFn: () =>
      api(finding ? `/api/admin/compliance/findings/${finding.id}` : '/api/admin/compliance/findings', {
        method: finding ? 'PUT' : 'POST',
        body: { ...f, auditId: f.auditId === 'none' ? null : f.auditId, due: isoDay(f.due) },
      }),
    onSuccess: () => {
      done()
      onClose()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const step = useMutation({
    mutationFn: ({ path, body }: { path: string; body?: object }) => api(`/api/admin/compliance/findings/${finding!.id}${path}`, { method: 'POST', body: body ?? {} }),
    onSuccess: () => {
      done()
      onClose()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const closed = finding?.status === 'verified'
  return (
    <FormDialog
      wide
      title={finding ? finding.title : 'Raise a finding'}
      description={finding ? `${FINDING_KINDS[finding.kind]}, ${finding.status}${finding.audit ? `, from ${finding.audit}` : ''}` : 'A nonconformity, an observation or an opportunity: against which clause, who acts on it, by when.'}
      onClose={onClose}
      onSubmit={() => (closed || !mayManage ? onClose() : save.mutate())}
      pending={save.isPending}
    >
      <Field label="The finding">
        <Input value={f.title} disabled={closed} onChange={(e) => setF({ ...f, title: e.target.value })} maxLength={300} required />
      </Field>
      <div className="grid gap-4 sm:grid-cols-2">
        <Choice label="Kind" value={f.kind} options={FINDING_KINDS} onChange={(v) => setF({ ...f, kind: v as Finding['kind'] })} />
        <Choice label="From the audit" value={f.auditId} options={{ none: 'Found otherwise', ...Object.fromEntries(audits.map((a) => [a.id, a.title])) }} onChange={(v) => setF({ ...f, auditId: v })} />
      </div>
      <Field label="What was found">
        <Textarea value={f.description} disabled={closed} onChange={(e) => setF({ ...f, description: e.target.value })} rows={2} maxLength={4000} />
      </Field>
      <div className="grid gap-4 sm:grid-cols-3">
        <Field label="Clause" hint="iso27001 A.8.5">
          <Input value={f.clause} disabled={closed} onChange={(e) => setF({ ...f, clause: e.target.value })} maxLength={64} />
        </Field>
        <Field label="Arena's control" hint="IAM-03">
          <Input value={f.control} disabled={closed} onChange={(e) => setF({ ...f, control: e.target.value.toUpperCase() })} maxLength={16} />
        </Field>
        <Field label="By">
          <Input type="date" value={f.due} disabled={closed} onChange={(e) => setF({ ...f, due: e.target.value })} />
        </Field>
      </div>
      <Field label="Owner">
        <Input value={f.owner} disabled={closed} onChange={(e) => setF({ ...f, owner: e.target.value })} maxLength={256} />
      </Field>
      <Field label="Correction" hint="What was put right at once.">
        <Textarea value={f.correction} disabled={closed} onChange={(e) => setF({ ...f, correction: e.target.value })} rows={2} maxLength={4000} />
      </Field>
      <Field label="Root cause">
        <Textarea value={f.rootCause} disabled={closed} onChange={(e) => setF({ ...f, rootCause: e.target.value })} rows={2} maxLength={4000} />
      </Field>
      <Field label="Corrective action" hint="What stops it happening again.">
        <Textarea value={f.correctiveAction} disabled={closed} onChange={(e) => setF({ ...f, correctiveAction: e.target.value })} rows={2} maxLength={4000} />
      </Field>
      {finding?.verification && <p className="text-sm text-muted-foreground">Checked: {finding.verification}</p>}
      {finding && mayManage && !closed && (
        <div className="grid gap-2 rounded-lg border p-3">
          {finding.status === 'open' ? (
            <Button type="button" variant="outline" loading={step.isPending} onClick={() => step.mutate({ path: '/done' })}>
              The action is taken
            </Button>
          ) : (
            <>
              <Field label="How it was checked">
                <Input value={note} onChange={(e) => setNote(e.target.value)} maxLength={4000} />
              </Field>
              <div className="flex flex-wrap gap-2">
                <Button type="button" loading={step.isPending} disabled={!note.trim()} onClick={() => step.mutate({ path: '/verify', body: { worked: true, note } })}>
                  It worked: close it
                </Button>
                <Button type="button" variant="outline" loading={step.isPending} disabled={!note.trim()} onClick={() => step.mutate({ path: '/verify', body: { worked: false, note } })}>
                  It did not: open it again
                </Button>
              </div>
            </>
          )}
        </div>
      )}
    </FormDialog>
  )
}

function CertificateForm({ onClose }: { onClose: () => void }) {
  const queryClient = useQueryClient()
  const [f, setF] = useState({ framework: 'iso27001', body: '', number: '', scope: '', issued: '', expires: '' })
  const save = useMutation({
    mutationFn: () => api('/api/admin/compliance/certificates', { method: 'POST', body: { ...f, issued: isoDay(f.issued), expires: isoDay(f.expires) } }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: auditsQuery.queryKey })
      onClose()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <FormDialog title="A certificate or report" description="What the company holds: its framework, who issued it, its scope, and when it ends." onClose={onClose} onSubmit={() => save.mutate()} pending={save.isPending}>
      <div className="grid gap-4 sm:grid-cols-2">
        <Choice label="Framework" value={f.framework} options={Object.fromEntries(FRAMEWORKS.map((x) => [x.key, x.label]))} onChange={(v) => setF({ ...f, framework: v })} />
        <Field label="Issued by">
          <Input value={f.body} onChange={(e) => setF({ ...f, body: e.target.value })} maxLength={256} required />
        </Field>
      </div>
      <Field label="Number">
        <Input value={f.number} onChange={(e) => setF({ ...f, number: e.target.value })} maxLength={128} />
      </Field>
      <Field label="Scope">
        <Textarea value={f.scope} onChange={(e) => setF({ ...f, scope: e.target.value })} rows={2} maxLength={4000} />
      </Field>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Issued">
          <Input type="date" value={f.issued} onChange={(e) => setF({ ...f, issued: e.target.value })} required />
        </Field>
        <Field label="Ends">
          <Input type="date" value={f.expires} onChange={(e) => setF({ ...f, expires: e.target.value })} required />
        </Field>
      </div>
    </FormDialog>
  )
}

function WaiverForm({ onClose }: { onClose: () => void }) {
  const queryClient = useQueryClient()
  const [f, setF] = useState({ control: '', reason: '', compensating: '', until: '' })
  const save = useMutation({
    mutationFn: () => api('/api/admin/compliance/waivers', { method: 'POST', body: { ...f, until: isoDay(f.until) } }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: auditsQuery.queryKey })
      onClose()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <FormDialog title="Waive a control" description="In your name, for a time (a year at most): why it is not met, and what makes up for it." onClose={onClose} onSubmit={() => save.mutate()} pending={save.isPending}>
      <Field label="Control or clause" hint="IAM-03, or iso27001 A.8.5">
        <Input value={f.control} onChange={(e) => setF({ ...f, control: e.target.value })} maxLength={64} required />
      </Field>
      <Field label="Why it is not met">
        <Textarea value={f.reason} onChange={(e) => setF({ ...f, reason: e.target.value })} rows={2} maxLength={4000} required />
      </Field>
      <Field label="What makes up for it">
        <Textarea value={f.compensating} onChange={(e) => setF({ ...f, compensating: e.target.value })} rows={2} maxLength={4000} />
      </Field>
      <Field label="Until">
        <Input type="date" value={f.until} onChange={(e) => setF({ ...f, until: e.target.value })} required />
      </Field>
    </FormDialog>
  )
}

function Part({ title, action, children }: { title: string; action?: ReactNode; children: ReactNode }) {
  return (
    <section aria-label={title} className="grid gap-2">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="font-medium">{title}</h3>
        {action}
      </div>
      {children}
    </section>
  )
}

/** Admin → Compliance → Audits: the programme, the findings and their actions, the certificates held, the waivers. */
export function AuditsSection({ mayManage }: { mayManage: boolean }) {
  const queryClient = useQueryClient()
  const data = useQuery(auditsQuery)
  const [form, setForm] = useState<{ kind: 'audit'; audit: Audit | null } | { kind: 'finding'; finding: Finding | null } | { kind: 'certificate' } | { kind: 'waiver' } | null>(null)
  const [reporting, setReporting] = useState<Audit | null>(null)
  const [conclusion, setConclusion] = useState('')
  const report = useMutation({
    mutationFn: () => api(`/api/admin/compliance/audits/${reporting!.id}/report`, { method: 'POST', body: { conclusion } }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: auditsQuery.queryKey })
      setReporting(null)
      setConclusion('')
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const withdraw = useMutation({
    mutationFn: (id: string) => api(`/api/admin/compliance/waivers/${id}/withdraw`, { method: 'POST', body: {} }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: auditsQuery.queryKey }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const v = data.data
  const add = (text: string, onClick: () => void) =>
    mayManage && (
      <Button size="sm" variant="outline" onClick={onClick}>
        <Plus /> {text}
      </Button>
    )
  return (
    <section aria-labelledby="audits-title" className="grid gap-6">
      <div>
        <h2 id="audits-title" className="text-lg font-semibold">
          Audits, findings and certificates
        </h2>
        <p className="text-sm text-muted-foreground">The audit programme, what each audit found and the actions it took, the certificates the company holds, and the controls waived for a time.</p>
      </div>
      {data.error ? (
        <QueryError error={data.error} retry={() => data.refetch()} />
      ) : !v ? (
        <Skeleton className="h-64" />
      ) : (
        <>
          <Part title="Audits" action={add('Plan an audit', () => setForm({ kind: 'audit', audit: null }))}>
            {v.audits.length === 0 ? (
              <p className="text-sm text-muted-foreground">None yet. An internal audit each year covers the management system and the controls in turn.</p>
            ) : (
              <ul className="divide-y rounded-lg border bg-card text-sm">
                {v.audits.map((a) => (
                  <li key={a.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 p-3">
                    <button type="button" className="min-w-48 flex-1 text-left font-medium hover:underline" onClick={() => setForm({ kind: 'audit', audit: a })}>
                      {a.title}
                    </button>
                    <Badge variant="secondary">{KINDS[a.kind] ?? a.kind}</Badge>
                    {a.framework && <span className="text-xs text-muted-foreground">{label(a.framework)}</span>}
                    <span className="text-xs text-muted-foreground">{day(a.starts)}</span>
                    <span className="text-xs text-muted-foreground">
                      {a.findings} finding{a.findings === 1 ? '' : 's'}
                      {a.openFindings > 0 ? `, ${a.openFindings} open` : ''}
                    </span>
                    {a.reportedAt ? (
                      <Badge variant="success" title={a.conclusion ?? ''}>
                        reported
                      </Badge>
                    ) : (
                      mayManage && (
                        <Button size="sm" variant="ghost" onClick={() => setReporting(a)}>
                          Report
                        </Button>
                      )
                    )}
                  </li>
                ))}
              </ul>
            )}
          </Part>
          <Part title="Findings" action={add('Raise a finding', () => setForm({ kind: 'finding', finding: null }))}>
            {v.findings.length === 0 ? (
              <p className="text-sm text-muted-foreground">None.</p>
            ) : (
              <ul className="divide-y rounded-lg border bg-card text-sm">
                {v.findings.map((f) => (
                  <li key={f.id}>
                    <button type="button" onClick={() => setForm({ kind: 'finding', finding: f })} className="flex w-full flex-wrap items-center gap-x-3 gap-y-1 p-3 text-left hover:bg-muted/50">
                      <span className={cn('min-w-48 flex-1 font-medium', f.status === 'verified' && 'text-muted-foreground')}>{f.title}</span>
                      <Badge variant={FINDING_BADGE[f.kind]}>{f.kind}</Badge>
                      {f.clause && <span className="font-mono text-xs text-muted-foreground">{f.clause}</span>}
                      <Badge variant={f.status === 'verified' ? 'success' : 'secondary'}>{f.status === 'verified' ? 'closed' : f.status === 'done' ? 'to check' : 'open'}</Badge>
                      {f.due && f.status !== 'verified' && <span className={cn('text-xs', f.late ? 'font-medium text-destructive-ink' : 'text-muted-foreground')}>by {day(f.due)}</span>}
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </Part>
          <div className="grid gap-6 lg:grid-cols-2">
            <Part title="Certificates and reports" action={add('Add', () => setForm({ kind: 'certificate' }))}>
              {v.certificates.length === 0 ? (
                <p className="text-sm text-muted-foreground">None held yet.</p>
              ) : (
                <ul className="divide-y rounded-lg border bg-card text-sm">
                  {v.certificates.map((c) => (
                    <li key={c.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 p-3">
                      <span className="flex-1 font-medium">{label(c.framework)}</span>
                      <span className="text-xs text-muted-foreground">{c.body}</span>
                      <span className={cn('text-xs', c.daysLeft < 0 ? 'font-medium text-destructive-ink' : c.daysLeft < 90 ? 'text-warning-ink' : 'text-muted-foreground')}>
                        {c.daysLeft < 0 ? `ended ${day(c.expires)}` : `until ${day(c.expires)}`}
                      </span>
                    </li>
                  ))}
                </ul>
              )}
            </Part>
            <Part title="Waivers" action={add('Waive a control', () => setForm({ kind: 'waiver' }))}>
              {v.waivers.length === 0 ? (
                <p className="text-sm text-muted-foreground">No control is waived.</p>
              ) : (
                <ul className="divide-y rounded-lg border bg-card text-sm">
                  {v.waivers.map((w) => (
                    <li key={w.id} className={cn('flex flex-wrap items-center gap-x-3 gap-y-1 p-3', w.withdrawnAt && 'text-muted-foreground')}>
                      <span className="font-mono text-xs">{w.control}</span>
                      <span className="min-w-40 flex-1" title={w.compensating ?? ''}>
                        {w.reason}
                      </span>
                      <span className={cn('text-xs', w.expired ? 'font-medium text-destructive-ink' : 'text-muted-foreground')}>
                        {w.withdrawnAt ? 'withdrawn' : `${w.approvedBy}, until ${day(w.until)}`}
                      </span>
                      {mayManage && !w.withdrawnAt && (
                        <Button size="sm" variant="ghost" loading={withdraw.isPending} onClick={() => withdraw.mutate(w.id)}>
                          Withdraw
                        </Button>
                      )}
                    </li>
                  ))}
                </ul>
              )}
            </Part>
          </div>
        </>
      )}
      {form?.kind === 'audit' && <AuditForm audit={form.audit} onClose={() => setForm(null)} />}
      {form?.kind === 'finding' && <FindingForm finding={form.finding} audits={v?.audits ?? []} mayManage={mayManage} onClose={() => setForm(null)} />}
      {form?.kind === 'certificate' && <CertificateForm onClose={() => setForm(null)} />}
      {form?.kind === 'waiver' && <WaiverForm onClose={() => setForm(null)} />}
      {reporting && (
        <FormDialog title={`Report: ${reporting.title}`} description="What the audit concluded, as the auditor says it." onClose={() => setReporting(null)} onSubmit={() => report.mutate()} pending={report.isPending}>
          <Field label="Conclusion">
            <Textarea value={conclusion} onChange={(e) => setConclusion(e.target.value)} rows={4} maxLength={4000} required />
          </Field>
        </FormDialog>
      )}
    </section>
  )
}
