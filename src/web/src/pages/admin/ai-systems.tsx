import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Plus } from 'lucide-react'
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

interface AiSystem {
  id: string
  name: string
  kind: string
  source: string | null
  purpose: string | null
  intendedUse: string | null
  affected: string | null
  class: 'unassessed' | 'minimal' | 'limited' | 'high' | 'prohibited'
  classWhy: string | null
  highRiskArea: string | null
  role: string
  owner: string | null
  oversight: string | null
  evaluation: string | null
  impactAssessment: string | null
  impactAssessedAt: string | null
  impactAssessedBy: string | null
  inUseSince: string | null
  reviewDue: string
  reviewOverdue: boolean
  retiredAt: string | null
  missing: string[]
}

interface Suggestion {
  source: string
  name: string
  kind: string
}

const KINDS: Record<string, string> = { assistant: 'Assistant', agent: 'Agent', app: 'App', model: 'Model', other: 'Other' }
const CLASSES: Record<AiSystem['class'], string> = { unassessed: 'Not assessed yet', minimal: 'Minimal', limited: 'Limited (transparency)', high: 'High-risk', prohibited: 'A prohibited practice' }
const CLASS_BADGE = { unassessed: 'secondary', minimal: 'success', limited: 'secondary', high: 'warning', prohibited: 'destructive' } as const
const ROLES: Record<string, string> = { deployer: 'Deployer (uses it)', provider: 'Provider (makes it)', both: 'Both' }
const AREAS = [
  'biometrics', 'critical infrastructure', 'education and vocational training', 'employment and workers management', 'essential private and public services',
  'law enforcement', 'migration, asylum and border control', 'justice and democratic processes',
]

const aiQuery = {
  queryKey: ['admin', 'compliance', 'ai-systems'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ systems: AiSystem[]; suggested: Suggestion[] }>('/api/admin/compliance/ai-systems', { signal }),
}

function Pick({ label, value, options, onChange }: { label: string; value: string; options: Record<string, string>; onChange: (v: string) => void }) {
  return (
    <Field label={label}>
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

function SystemForm({ system, from, mayManage, onClose }: { system: AiSystem | null; from: Suggestion | null; mayManage: boolean; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [f, setF] = useState({
    name: system?.name ?? from?.name ?? '', kind: system?.kind ?? from?.kind ?? 'assistant', source: system?.source ?? from?.source ?? null,
    purpose: system?.purpose ?? '', intendedUse: system?.intendedUse ?? '', affected: system?.affected ?? '', class: system?.class ?? 'unassessed',
    classWhy: system?.classWhy ?? '', highRiskArea: system?.highRiskArea ?? AREAS[3]!, role: system?.role ?? 'deployer', owner: system?.owner ?? '',
    oversight: system?.oversight ?? '', evaluation: system?.evaluation ?? '',
  })
  const [assessment, setAssessment] = useState(system?.impactAssessment ?? '')
  const [note, setNote] = useState('')
  const refresh = () => void queryClient.invalidateQueries({ queryKey: aiQuery.queryKey })
  const save = useMutation({
    mutationFn: () =>
      api(system ? `/api/admin/compliance/ai-systems/${system.id}` : '/api/admin/compliance/ai-systems', {
        method: system ? 'PUT' : 'POST',
        body: { ...f, highRiskArea: f.class === 'high' ? f.highRiskArea : null },
      }),
    onSuccess: () => {
      refresh()
      onClose()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const step = useMutation({
    mutationFn: ({ path, body }: { path: string; body?: object }) => api(`/api/admin/compliance/ai-systems/${system!.id}${path}`, { method: 'POST', body: body ?? {} }),
    onSuccess: (_, v) => {
      refresh()
      toast.success(v.path === '/in-use' ? 'In use.' : 'Saved.')
      if (v.path !== '/assess') onClose()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const text = (key: 'purpose' | 'intendedUse' | 'affected' | 'classWhy' | 'oversight' | 'evaluation', label: string, hint?: string) => (
    <Field label={label} hint={hint}>
      <Textarea value={f[key]} onChange={(e) => setF({ ...f, [key]: e.target.value })} rows={2} maxLength={2000} />
    </Field>
  )
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{system ? system.name : 'Register an AI system'}</DialogTitle>
          <DialogDescription>
            {system?.inUseSince ? `In use since ${new Date(system.inUseSince).toLocaleDateString()}.` : 'Its purpose and class, its impact, oversight and evaluation: what its class needs before it is put into use.'}
          </DialogDescription>
        </DialogHeader>
        <form
          className="grid max-h-[68vh] gap-4 overflow-y-auto pr-1"
          onSubmit={(e) => {
            e.preventDefault()
            if (mayManage) save.mutate()
          }}
        >
          <div className="grid gap-4 sm:grid-cols-[1fr_10rem]">
            <Field label="Name">
              <Input value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} maxLength={200} required />
            </Field>
            <Pick label="Kind" value={f.kind} options={KINDS} onChange={(v) => setF({ ...f, kind: v })} />
          </div>
          {text('purpose', 'Purpose')}
          {text('intendedUse', 'Intended use', 'How it is meant to be used, and how not.')}
          {text('affected', 'Whom it affects', 'Those who use it, and those its outputs are about.')}
          <div className="grid gap-4 sm:grid-cols-2">
            <Pick label="Class under the AI Act" value={f.class} options={CLASSES} onChange={(v) => setF({ ...f, class: v as AiSystem['class'] })} />
            <Pick label="The company's role" value={f.role} options={ROLES} onChange={(v) => setF({ ...f, role: v })} />
          </div>
          {f.class === 'high' && <Pick label="Annex III area" value={f.highRiskArea} options={Object.fromEntries(AREAS.map((a) => [a, a]))} onChange={(v) => setF({ ...f, highRiskArea: v })} />}
          {f.class === 'prohibited' && <p className="text-sm text-destructive-ink">A prohibited practice (Article 5) is never put into use.</p>}
          {text('classWhy', 'Why this class')}
          <Field label="Owner">
            <Input value={f.owner} onChange={(e) => setF({ ...f, owner: e.target.value })} maxLength={256} />
          </Field>
          {text('oversight', 'Human oversight', 'Who can stop or overrule it, and how.')}
          {text('evaluation', 'Evaluation', 'How it was measured for its task, and where the results are.')}
          {system && (
            <section aria-label="Impact assessment" className="grid gap-2 rounded-lg border p-3">
              <Field label="Impact assessment" hint={system.impactAssessedBy ? `By ${system.impactAssessedBy}, ${new Date(system.impactAssessedAt!).toLocaleDateString()}.` : 'Whom it affects, how, and what limits the harm.'}>
                <Textarea value={assessment} onChange={(e) => setAssessment(e.target.value)} rows={4} maxLength={8000} disabled={!mayManage} />
              </Field>
              {mayManage && (
                <Button type="button" variant="outline" size="sm" className="w-fit" loading={step.isPending} disabled={!assessment.trim()} onClick={() => step.mutate({ path: '/assess', body: { assessment } })}>
                  Save the assessment
                </Button>
              )}
            </section>
          )}
          {system && system.missing.length > 0 && <p className="text-sm text-warning-ink">Before it is in use: {system.missing.join('; ')}.</p>}
          {system && mayManage && !system.retiredAt && (
            <div className="flex flex-wrap items-end gap-2 rounded-lg border p-3">
              {!system.inUseSince && (
                <Button type="button" loading={step.isPending} onClick={() => step.mutate({ path: '/in-use' })}>
                  Put into use
                </Button>
              )}
              <Field label="Review: what was found" className="min-w-56 flex-1">
                <Input value={note} onChange={(e) => setNote(e.target.value)} maxLength={2000} />
              </Field>
              <Button type="button" variant="outline" disabled={!note.trim()} loading={step.isPending} onClick={() => step.mutate({ path: '/review', body: { note } })}>
                Reviewed
              </Button>
              <Button type="button" variant="ghost" loading={step.isPending} onClick={() => step.mutate({ path: '/retire' })}>
                Retire
              </Button>
            </div>
          )}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            {mayManage && (
              <Button type="submit" loading={save.isPending}>
                Save
              </Button>
            )}
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

/** Admin → Compliance → AI systems: the register, and the assistants, agents, apps and models not in it yet. */
export function AiSystemsSection({ mayManage }: { mayManage: boolean }) {
  const data = useQuery(aiQuery)
  const [form, setForm] = useState<{ system: AiSystem | null; from: Suggestion | null } | null>(null)
  const v = data.data
  return (
    <section aria-labelledby="ai-title" className="grid gap-4">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div>
          <h2 id="ai-title" className="text-lg font-semibold">
            AI systems
          </h2>
          <p className="text-sm text-muted-foreground">Each AI system the company runs: its purpose, its class under the EU AI Act, its impact assessment, oversight and evaluation.</p>
        </div>
        {mayManage && (
          <Button onClick={() => setForm({ system: null, from: null })}>
            <Plus /> Register a system
          </Button>
        )}
      </div>
      {data.error ? (
        <QueryError error={data.error} retry={() => data.refetch()} />
      ) : !v ? (
        <Skeleton className="h-48" />
      ) : (
        <>
          {v.suggested.length > 0 && (
            <div className="grid gap-2 rounded-lg border border-warning/40 bg-warning/5 p-3 text-sm">
              <p className="font-medium">People use these, which the register does not hold yet:</p>
              <ul className="grid gap-1">
                {v.suggested.slice(0, 20).map((s) => (
                  <li key={s.source} className="flex flex-wrap items-center gap-2">
                    <span className="flex-1">
                      {s.name} <span className="text-muted-foreground">({KINDS[s.kind] ?? s.kind})</span>
                    </span>
                    {mayManage && (
                      <Button size="sm" variant="outline" onClick={() => setForm({ system: null, from: s })}>
                        Register
                      </Button>
                    )}
                  </li>
                ))}
              </ul>
              {v.suggested.length > 20 && <p className="text-muted-foreground">and {v.suggested.length - 20} more.</p>}
            </div>
          )}
          {v.systems.length === 0 ? (
            <p className="text-sm text-muted-foreground">The register is empty.</p>
          ) : (
            <ul className="divide-y rounded-lg border bg-card">
              {v.systems.map((s) => (
                <li key={s.id}>
                  <button type="button" onClick={() => setForm({ system: s, from: null })} className="flex w-full flex-wrap items-center gap-x-3 gap-y-1 p-3 text-left text-sm hover:bg-muted/50">
                    <span className={cn('min-w-40 flex-1 font-medium', s.retiredAt && 'text-muted-foreground line-through')}>{s.name}</span>
                    <span className="text-xs text-muted-foreground">{KINDS[s.kind] ?? s.kind}</span>
                    <Badge variant={CLASS_BADGE[s.class]}>{CLASSES[s.class]}</Badge>
                    {s.inUseSince ? <Badge variant="success">in use</Badge> : s.missing.length > 0 && <span className="text-xs text-warning-ink">{s.missing.length} to say first</span>}
                    {!s.retiredAt && <span className={cn('text-xs', s.reviewOverdue ? 'font-medium text-destructive-ink' : 'text-muted-foreground')}>review {new Date(s.reviewDue).toLocaleDateString()}</span>}
                  </button>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
      {form && <SystemForm system={form.system} from={form.from} mayManage={mayManage} onClose={() => setForm(null)} />}
    </section>
  )
}
