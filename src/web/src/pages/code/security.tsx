import { useQuery, useQueryClient } from '@tanstack/react-query'
import { BookOpen, Download, Package, ShieldAlert, ShieldCheck } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { Markdown } from '@/pages/chat/markdown'
import { securityScanQuery, securityScansQuery, type ScanBrief, type SecurityFinding } from './api'

const severityVariant = { critical: 'destructive', high: 'destructive', medium: 'warning', low: 'secondary' } as const
const severities = ['critical', 'high', 'medium', 'low'] as const

export function ScanState({ s }: { s: ScanBrief }) {
  if (s.status === 'failed') return <Badge variant="destructive">Failed</Badge>
  if (s.status !== 'done') return <Badge variant="secondary">{s.step ?? 'Waiting'}…</Badge>
  return <Badge variant="success">Done</Badge>
}

/** Code Studio's security reviews: start one on a project (and a branch), and the list of them with what is open. */
export function SecurityReviews() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const scans = useQuery(securityScansQuery)
  const [project, setProject] = useState('')
  const [ref, setRef] = useState('')
  const [busy, setBusy] = useState(false)
  const start = async () => {
    setBusy(true)
    try {
      const made = await api<{ id: string }>('/api/code/security', { body: { project: project.trim(), ...(ref.trim() && { ref: ref.trim() }) } })
      await queryClient.invalidateQueries({ queryKey: ['code', 'security'] })
      navigate(`/code/security/${made.id}`)
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setBusy(false)
    }
  }
  return (
    <section aria-labelledby="security-title" className="mt-8">
      <h2 id="security-title" className="mb-3 text-lg font-semibold">
        Security
      </h2>
      <Card>
        <CardHeader>
          <CardTitle>Review a repository's security</CardTitle>
          <CardDescription>
            Where data from outside reaches a query, a command, a path, a fetch, a deserialiser or HTML, traced to its callers and judged by the model. A finding is kept only when it cites
            the line as the file has it and names a weakness the security pack knows. Read with your own access; nothing is written.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <form
            className="flex flex-wrap items-end gap-2"
            onSubmit={(e) => {
              e.preventDefault()
              void start()
            }}
          >
            <Field label="Repository" className="min-w-56 flex-1">
              <Input value={project} onChange={(e) => setProject(e.target.value)} placeholder="group/project" />
            </Field>
            <Field label="Branch, tag or commit" className="w-48">
              <Input value={ref} onChange={(e) => setRef(e.target.value)} placeholder="the default branch" />
            </Field>
            <Button type="submit" loading={busy} disabled={!project.trim()}>
              <ShieldAlert /> Review security
            </Button>
          </form>
        </CardContent>
      </Card>
      {scans.error ? (
        <QueryError error={scans.error} retry={() => scans.refetch()} />
      ) : !scans.data ? (
        <Skeleton className="mt-3 h-16" />
      ) : (
        scans.data.length > 0 && (
          <ul className="mt-3 grid gap-2" aria-label="Security reviews">
            {scans.data.map(({ scan: s, open }) => (
              <li key={s.id}>
                <Link to={`/code/security/${s.id}`} className="flex items-center gap-3 rounded-lg border px-4 py-3 text-sm outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring">
                  <span className="min-w-0 flex-1">
                    <span className="block truncate font-medium">
                      {s.project}
                      {s.ref && <span className="text-muted-foreground"> @ {s.ref}</span>}
                    </span>
                    <span className="block text-xs text-muted-foreground">{new Date(s.createdAt).toLocaleString()}</span>
                  </span>
                  {severities.map((v) => (open[v] ? <Badge key={v} variant={severityVariant[v]}>{`${open[v]} ${v}`}</Badge> : null))}
                  <ScanState s={s} />
                </Link>
              </li>
            ))}
          </ul>
        )
      )}
    </section>
  )
}

function FindingCard({ f, onDecide }: { f: SecurityFinding; onDecide: (state: SecurityFinding['state'], why?: string, until?: string) => Promise<void> }) {
  const [asking, setAsking] = useState<'accepted' | 'wrong' | null>(null)
  const [why, setWhy] = useState('')
  const [until, setUntil] = useState(() => new Date(Date.now() + 90 * 86_400_000).toISOString().slice(0, 10))
  const refuted = f.verified === false
  const cwePage = f.pages[0]
  return (
    <li className={`grid gap-2 rounded-lg border p-4 ${refuted || f.state !== 'open' ? 'opacity-70' : ''}`}>
      <div className="flex flex-wrap items-center gap-2">
        <Badge variant={severityVariant[f.severity]}>{f.severity}</Badge>
        {f.exploited && <Badge variant="destructive">known exploited</Badge>}
        {f.kind === 'dependency' ? (
          <Badge variant="outline">{f.cwe}</Badge>
        ) : cwePage?.url ? (
          <a href={cwePage.url} target="_blank" rel="noreferrer" title={f.cweTitle}>
            <Badge variant="outline">{f.cwe}</Badge>
          </a>
        ) : (
          <Badge variant="outline">{f.cwe}</Badge>
        )}
        <code className="text-xs text-muted-foreground">
          {f.path}
          {f.line > 0 && `:${f.line}`}
        </code>
        {f.state !== 'open' && <Badge variant="secondary">{f.state === 'accepted' && f.until ? `accepted until ${new Date(f.until).toLocaleDateString()}` : f.state}</Badge>}
      </div>
      <p className="font-medium">{f.title}</p>
      {f.kind === 'dependency' ? (
        <p className="flex items-center gap-2 font-mono text-sm" aria-label="The package">
          <Package className="size-4 text-muted-foreground" aria-hidden="true" />
          {f.sinkCode}
        </p>
      ) : (
        <pre className="overflow-x-auto rounded-md bg-muted p-3 font-mono text-xs" aria-label="The sink">
          <code>{f.sinkCode}</code>
        </pre>
      )}
      <dl className="grid gap-x-4 gap-y-1 text-sm sm:grid-cols-[8rem_1fr]">
        {f.source && (
          <>
            <dt className="text-muted-foreground">{f.kind === 'dependency' ? 'Advisory' : 'Comes in at'}</dt>
            <dd>{f.source}</dd>
          </>
        )}
        {f.flow && (
          <>
            <dt className="text-muted-foreground">{f.kind === 'dependency' ? 'Comes in' : 'Path'}</dt>
            <dd>
              <ol className="list-decimal pl-5">
                {f.flow.split('\n').map((step, i) => (
                  <li key={i}>{step}</li>
                ))}
              </ol>
            </dd>
          </>
        )}
        {f.exploit && (
          <>
            <dt className="text-muted-foreground">{f.kind === 'dependency' ? 'Exploited' : 'Exploited by'}</dt>
            <dd>{f.exploit}</dd>
          </>
        )}
        <dt className="text-muted-foreground">Fix</dt>
        <dd>
          <Markdown text={f.fix} />
        </dd>
      </dl>
      {f.pages.length > 0 && (
        <p className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs">
          <BookOpen className="size-3.5 text-muted-foreground" aria-hidden="true" />
          {f.pages.map((p, i) =>
            p.url ? (
              <a key={i} href={p.url} target="_blank" rel="noreferrer" className="underline-offset-2 hover:underline">
                {p.title}
              </a>
            ) : (
              <span key={i}>{p.title}</span>
            ),
          )}
        </p>
      )}
      {f.verifyNote && (
        <p className="text-xs text-muted-foreground">
          {refuted ? 'A second check doubts it: ' : 'Checked: '}
          {f.verifyNote}
        </p>
      )}
      {f.why && f.state !== 'open' && <p className="text-xs text-muted-foreground">Why: {f.why}</p>}
      <div className="flex flex-wrap gap-2">
        {f.state === 'open' ? (
          <>
            <Button size="sm" variant="outline" onClick={() => void onDecide('fixed')}>
              <ShieldCheck /> Fixed
            </Button>
            <Button size="sm" variant="ghost" onClick={() => setAsking('accepted')}>
              Accept the risk
            </Button>
            <Button size="sm" variant="ghost" onClick={() => setAsking('wrong')}>
              Not a weakness
            </Button>
          </>
        ) : (
          <Button size="sm" variant="ghost" onClick={() => void onDecide('open')}>
            Open again
          </Button>
        )}
      </div>
      {asking && (
        <Dialog open onOpenChange={(o) => !o && setAsking(null)}>
          <DialogContent>
            <DialogHeader>
              <DialogTitle>{asking === 'accepted' ? 'Accept the risk' : 'Not a weakness'}</DialogTitle>
              <DialogDescription>{asking === 'accepted' ? 'Why it stays as it is, and when to look at it again.' : 'Why it is not one: remembered with the finding.'}</DialogDescription>
            </DialogHeader>
            <Field label="Why">
              <Textarea value={why} onChange={(e) => setWhy(e.target.value)} rows={3} maxLength={2000} />
            </Field>
            {asking === 'accepted' && (
              <Field label="Look again by">
                <Input type="date" value={until} onChange={(e) => setUntil(e.target.value)} />
              </Field>
            )}
            <DialogFooter>
              <Button variant="outline" onClick={() => setAsking(null)}>
                Cancel
              </Button>
              <Button
                disabled={!why.trim()}
                onClick={() => void onDecide(asking, why.trim(), asking === 'accepted' ? new Date(`${until}T12:00:00Z`).toISOString() : undefined).then(() => setAsking(null))}
              >
                Save
              </Button>
            </DialogFooter>
          </DialogContent>
        </Dialog>
      )}
    </li>
  )
}

/** One security review: what it did, its findings worst first (doubted ones apart), each fixed, accepted or marked wrong. */
export function SecurityScanPage() {
  const { id = '' } = useParams()
  const queryClient = useQueryClient()
  const scan = useQuery(securityScanQuery(id))
  if (scan.error) return <QueryError error={scan.error} retry={() => scan.refetch()} />
  if (!scan.data) return <Skeleton className="h-64" />
  const d = scan.data
  const s = d.scan
  const decide = async (findingId: string, state: SecurityFinding['state'], why?: string, until?: string) => {
    try {
      await api(`/api/code/security/${id}/findings/${findingId}`, { method: 'PUT', body: { state, ...(why && { why }), ...(until && { until }) } })
      await queryClient.invalidateQueries({ queryKey: ['code', 'security'] })
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }
  const packages = d.findings.filter((f) => f.kind === 'dependency')
  const code = d.findings.filter((f) => f.kind === 'code')
  const kept = code.filter((f) => f.verified !== false)
  const doubted = code.filter((f) => f.verified === false)
  const age = s.advisoriesAsOf ? Math.floor((Date.now() - new Date(`${s.advisoriesAsOf}T00:00:00Z`).getTime()) / 86_400_000) : null
  return (
    <>
      <PageHeader
        title={`Security: ${s.project}${s.ref ? ` @ ${s.ref}` : ''}`}
        description={`Reviewed for ${d.by}${d.model ? ` with ${d.model}` : ''}. Read with ${d.mine ? 'your' : 'their'} own access; nothing is written to the code host.`}
        actions={
          s.sbom ? (
            <Button variant="outline" asChild>
              <a href={`/api/code/security/${id}/sbom`} download>
                <Download /> Bill of materials
              </a>
            </Button>
          ) : undefined
        }
      />
      <div className="mb-4 flex flex-wrap items-center gap-2">
        <ScanState s={s} />
        {s.status === 'done' && (
          <span className="text-sm text-muted-foreground">
            {s.read} of {s.candidates} places read · {s.dropped} dropped uncited
          </span>
        )}
      </div>
      {s.status === 'failed' && <Alert variant="destructive">{s.error}</Alert>}
      {age !== null && age > 7 && (
        <Alert variant="warning" className="mb-4">
          The advisories its packages were checked against are {age} days old: renew the advisories pack (Admin → Packs) and review it again.
        </Alert>
      )}
      {d.summary && (
        <Card className="mb-6">
          <CardHeader>
            <CardTitle>Summary</CardTitle>
          </CardHeader>
          <CardContent>
            <Markdown text={d.summary} />
          </CardContent>
        </Card>
      )}
      {s.status === 'done' && (
        <>
          {s.advisoriesAsOf && (
            <>
              <h2 className="mb-3 text-lg font-semibold">
                Vulnerable packages ({packages.length} of {s.dependencies})
              </h2>
              {packages.length === 0 ? (
                <p className="mb-6 text-sm text-muted-foreground">No package is known to be vulnerable in the advisories of {s.advisoriesAsOf}.</p>
              ) : (
                <ul className="mb-6 grid gap-3" aria-label="Vulnerable packages">
                  {packages.map((f) => (
                    <FindingCard key={f.id} f={f} onDecide={(state, why, until) => decide(f.id, state, why, until)} />
                  ))}
                </ul>
              )}
            </>
          )}
          <h2 className="mb-3 text-lg font-semibold">Weaknesses in the code ({kept.length})</h2>
          {kept.length === 0 ? (
            <p className="text-sm text-muted-foreground">No weakness was proven in what was read.</p>
          ) : (
            <ul className="grid gap-3" aria-label="Weaknesses">
              {kept.map((f) => (
                <FindingCard key={f.id} f={f} onDecide={(state, why, until) => decide(f.id, state, why, until)} />
              ))}
            </ul>
          )}
          {doubted.length > 0 && (
            <details className="mt-6">
              <summary className="cursor-pointer text-sm text-muted-foreground">Doubted by the second check ({doubted.length})</summary>
              <ul className="mt-3 grid gap-3" aria-label="Doubted weaknesses">
                {doubted.map((f) => (
                  <FindingCard key={f.id} f={f} onDecide={(state, why, until) => decide(f.id, state, why, until)} />
                ))}
              </ul>
            </details>
          )}
        </>
      )}
    </>
  )
}
