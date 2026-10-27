import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, ArrowLeft, Clock } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate, useOutletContext, useParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage, type Me } from '@/lib/api'
import { ago, when } from '@/lib/format'
import { incidentQuery, incidentsQuery, STEP_WORDS, type Incident, type SecurityAlert } from './compliance-api'

const SEVERITY: Record<Incident['severity'], 'secondary' | 'warning' | 'destructive'> = { low: 'secondary', medium: 'warning', high: 'destructive', critical: 'destructive' }

/** How long is left until a deadline, or how far past it. */
function Due({ label, at, done }: { label: string; at: string; done: string | null }) {
  // As the page was drawn: the clock is read again when it is.
  const [now] = useState(() => Date.now())
  const left = new Date(at).getTime() - now
  const hours = Math.round(Math.abs(left) / 3_600_000)
  const words = hours >= 48 ? `${Math.round(hours / 24)} days` : `${hours} hours`
  return (
    <span className="inline-flex items-center gap-1 text-xs">
      <Clock className="size-3.5" aria-hidden="true" />
      {label}:{' '}
      {done ? (
        <span className="text-success-ink">done {new Date(done).toLocaleString()}</span>
      ) : left < 0 ? (
        <span className="font-medium text-destructive-ink">{words} late</span>
      ) : (
        <span className={left < 24 * 3_600_000 ? 'font-medium text-warning-ink' : ''}>{words} left (by {new Date(at).toLocaleString()})</span>
      )}
    </span>
  )
}

function OpenIncident({ alert, onClose }: { alert: SecurityAlert | null; onClose: () => void }) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const [title, setTitle] = useState(alert?.title ?? '')
  const [summary, setSummary] = useState(alert?.detail ?? '')
  const [severity, setSeverity] = useState<Incident['severity']>('medium')
  const [personal, setPersonal] = useState(false)
  const [health, setHealth] = useState(false)
  const open = useMutation({
    mutationFn: () =>
      api<Incident>('/api/admin/compliance/incidents', { method: 'POST', body: { title, summary, severity, personalData: personal, healthData: health, alertId: alert?.id } }),
    onSuccess: (made) => {
      void queryClient.invalidateQueries({ queryKey: incidentsQuery.queryKey })
      navigate(`/admin/compliance/incidents/${made.id}`)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Open an incident</DialogTitle>
          <DialogDescription>Its clocks run from when it was found: 72 hours to tell the supervisory authority of a personal data breach, 60 days to tell the people whose health data it was.</DialogDescription>
        </DialogHeader>
        <form
          className="grid gap-4"
          onSubmit={(e) => {
            e.preventDefault()
            open.mutate()
          }}
        >
          <Field label="What happened">
            <Input value={title} onChange={(e) => setTitle(e.target.value)} maxLength={300} required />
          </Field>
          <Field label="What is known so far">
            <Textarea value={summary} onChange={(e) => setSummary(e.target.value)} rows={3} maxLength={4000} />
          </Field>
          <Field label="Severity">
            <Select value={severity} onValueChange={(v) => setSeverity(v as Incident['severity'])}>
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {(['low', 'medium', 'high', 'critical'] as const).map((s) => (
                  <SelectItem key={s} value={s}>
                    {s}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
          <div className="flex items-center gap-2">
            <Checkbox id="incident-personal" checked={personal || health} disabled={health} onCheckedChange={(v) => setPersonal(v === true)} />
            <Label htmlFor="incident-personal">Personal data may be involved (the GDPR's 72 hours)</Label>
          </div>
          <div className="flex items-center gap-2">
            <Checkbox id="incident-health" checked={health} onCheckedChange={(v) => setHealth(v === true)} />
            <Label htmlFor="incident-health">Health data may be involved (HIPAA's breach steps)</Label>
          </div>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" loading={open.isPending} disabled={!title.trim()}>
              Open it
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

/** Admin → Compliance: the security alerts raised, and the incident register. */
export function IncidentsSection({ mayManage }: { mayManage: boolean }) {
  const data = useQuery(incidentsQuery)
  const [opening, setOpening] = useState<SecurityAlert | null | undefined>(undefined)
  const alerts = data.data?.alerts ?? []
  const incidents = data.data?.incidents ?? []
  return (
    <section id="incidents" aria-labelledby="incidents-title" className="grid scroll-mt-20 gap-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <h2 id="incidents-title" className="text-lg font-semibold">
            Security alerts and incidents
          </h2>
          <p className="text-sm text-muted-foreground">What the app noticed (failed sign-ins, an admin made, many exports, a failed backup), and each incident from finding to closure.</p>
        </div>
        {mayManage && (
          <Button variant="outline" onClick={() => setOpening(null)}>
            Open an incident
          </Button>
        )}
      </div>
      {incidents.length > 0 && (
        <ul className="divide-y rounded-lg border bg-card">
          {incidents.map((i) => (
            <li key={i.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 p-3 text-sm">
              <Link to={`/admin/compliance/incidents/${i.id}`} className="min-w-48 flex-1 font-medium text-primary-ink underline-offset-2 hover:underline">
                {i.title}
              </Link>
              <Badge variant={SEVERITY[i.severity]}>{i.severity}</Badge>
              <Badge variant={i.status === 'closed' ? 'success' : 'secondary'}>{i.status}</Badge>
              {i.status !== 'closed' && i.authorityDue && <Due label="Authority" at={i.authorityDue} done={i.authorityDecidedAt} />}
            </li>
          ))}
        </ul>
      )}
      {alerts.length === 0 ? (
        <p className="text-sm text-muted-foreground">No security alert in the last 60 days.</p>
      ) : (
        <ul className="divide-y rounded-lg border bg-card">
          {alerts.slice(0, 20).map((a) => (
            <li key={a.id} className="flex flex-wrap items-start gap-3 p-3 text-sm">
              <AlertTriangle className="mt-0.5 size-4 shrink-0 text-warning" aria-hidden="true" />
              <div className="min-w-48 flex-1">
                <div className="font-medium">{a.title}</div>
                {a.detail && <div className="text-muted-foreground">{a.detail}</div>}
                <time dateTime={a.at} title={when(a.at)} className="text-xs text-muted-foreground">
                  {ago(a.at)}
                </time>
              </div>
              {a.incidentId ? (
                <Link to={`/admin/compliance/incidents/${a.incidentId}`} className="text-xs text-primary-ink underline-offset-2 hover:underline">
                  Its incident
                </Link>
              ) : (
                mayManage && (
                  <Button size="sm" variant="ghost" onClick={() => setOpening(a)}>
                    Open an incident
                  </Button>
                )
              )}
            </li>
          ))}
        </ul>
      )}
      {opening !== undefined && <OpenIncident alert={opening} onClose={() => setOpening(undefined)} />}
    </section>
  )
}

const STEPS: { kind: string; label: string; needs?: string }[] = [
  { kind: 'note', label: 'Add a note', needs: 'What happened' },
  { kind: 'contained', label: 'Contained', needs: 'How it was stopped' },
  { kind: 'resolved', label: 'Resolved', needs: 'What was done' },
  { kind: 'authority_notified', label: 'Authority told', needs: 'Which authority, when, its reference' },
  { kind: 'authority_not_required', label: 'Authority need not be told', needs: 'Why the breach is unlikely to risk people’s rights and freedoms (Article 33(1))' },
  { kind: 'people_notified', label: 'People told', needs: 'Who was told, how' },
  { kind: 'closed', label: 'Close it', needs: 'What was learned, and what changes so it does not happen again' },
]

/** An incident: its clocks, the law's steps, and its timeline. */
export function IncidentPage() {
  const { id = '' } = useParams()
  const me = useOutletContext<Me>()
  const mayManage = !!me.isAdmin || !!me.permissions?.includes('compliance.manage')
  const queryClient = useQueryClient()
  const data = useQuery(incidentQuery(id))
  const [step, setStep] = useState<(typeof STEPS)[number] | null>(null)
  const [text, setText] = useState('')
  const record = useMutation({
    mutationFn: () => api<Incident>(`/api/admin/compliance/incidents/${id}/steps`, { method: 'POST', body: { kind: step!.kind, text } }),
    onSuccess: () => {
      setStep(null)
      setText('')
      void queryClient.invalidateQueries({ queryKey: ['admin', 'compliance', 'incidents'] })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (data.error) return <QueryError error={data.error} retry={() => data.refetch()} />
  if (!data.data) return <Skeleton className="h-64" />
  const { incident: i, events } = data.data
  const open = i.status !== 'closed'
  return (
    <>
      <Link to="/admin/compliance?tab=incidents" className="mb-3 inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground">
        <ArrowLeft className="size-4" aria-hidden="true" /> Compliance
      </Link>
      <PageHeader
        title={i.title}
        description={
          <span className="flex flex-wrap items-center gap-2">
            <Badge variant={SEVERITY[i.severity]}>{i.severity}</Badge>
            <Badge variant={i.status === 'closed' ? 'success' : 'secondary'}>{i.status}</Badge>
            Found {new Date(i.detectedAt).toLocaleString()}; opened by {i.openedBy}.
          </span>
        }
      />
      <div className="grid gap-6">
        {(i.authorityDue || i.peopleDue) && (
          <div className="grid gap-2 rounded-lg border bg-card p-4 text-sm">
            <h2 className="font-semibold">What the law asks</h2>
            {i.authorityDue && (
              <p>
                <Due label="Tell the supervisory authority (GDPR 33), or record why not" at={i.authorityDue} done={i.authorityDecidedAt} />
                {i.authorityNotified === false && <span className="ms-2 text-xs text-muted-foreground">(not required: see the timeline)</span>}
              </p>
            )}
            {i.peopleDue && (
              <p>
                <Due label="Tell the people whose health data it was (HIPAA 164.404); HHS too, at once if 500 or more" at={i.peopleDue} done={i.peopleNotifiedAt} />
              </p>
            )}
            {i.personalData && <p className="text-xs text-muted-foreground">A high risk to people’s rights: tell them too without undue delay (GDPR 34). Every breach stays in this register (GDPR 33(5)).</p>}
          </div>
        )}
        {i.summary && <p className="text-sm">{i.summary}</p>}
        {mayManage && open && (
          <div className="flex flex-wrap gap-2">
            {STEPS.map((s) => (
              <Button key={s.kind} size="sm" variant={s.kind === 'closed' ? 'default' : 'outline'} onClick={() => setStep(s)}>
                {s.label}
              </Button>
            ))}
          </div>
        )}
        {i.lessons && <Alert variant="success">Learned: {i.lessons}</Alert>}
        <section aria-labelledby="timeline" className="grid gap-2">
          <h2 id="timeline" className="text-lg font-semibold">
            Timeline
          </h2>
          <ol className="grid gap-3 border-s ps-4">
            {events.map((e) => (
              <li key={e.id} className="text-sm">
                <div className="font-medium">{STEP_WORDS[e.kind] ?? e.kind}</div>
                <div className="text-xs text-muted-foreground">
                  {new Date(e.at).toLocaleString()} by {e.by}
                </div>
                {e.text && <div className="mt-1">{e.text}</div>}
              </li>
            ))}
          </ol>
        </section>
      </div>
      {step && (
        <Dialog open onOpenChange={(o) => !o && setStep(null)}>
          <DialogContent>
            <DialogHeader>
              <DialogTitle>{step.label}</DialogTitle>
              <DialogDescription>Kept on the incident's timeline with your name and the time, and in the audit log.</DialogDescription>
            </DialogHeader>
            <form
              className="grid gap-4"
              onSubmit={(e) => {
                e.preventDefault()
                record.mutate()
              }}
            >
              <Field label={step.needs ?? 'Note'}>
                <Textarea value={text} onChange={(e) => setText(e.target.value)} rows={4} maxLength={4000} />
              </Field>
              <DialogFooter>
                <Button type="button" variant="outline" onClick={() => setStep(null)}>
                  Cancel
                </Button>
                <Button type="submit" loading={record.isPending}>
                  Record it
                </Button>
              </DialogFooter>
            </form>
          </DialogContent>
        </Dialog>
      )}
    </>
  )
}
