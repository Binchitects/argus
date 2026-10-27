import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Check, Download } from 'lucide-react'
import { useState } from 'react'
import { QueryError } from '@/components/app/query-state'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import type { Control, State } from './compliance-api'

type Status = 'implemented' | 'partial' | 'planned' | 'not_implemented'

interface SoaLine {
  clause: string
  title: string
  decided: boolean
  applicable: boolean
  status: Status | null
  justification: string | null
  updatedBy: string | null
  updatedAt: string | null
  controls: string[]
  risks: { id: string; title: string }[]
}

interface Approval {
  version: number
  approvedBy: string
  approvedAt: string
  note: string | null
}

interface SoaView {
  framework: string
  title: string
  lines: SoaLine[]
  approved: Approval | null
  changedSinceApproval: boolean
  versions: Approval[]
}

const FRAMEWORKS = [
  { key: 'iso27001', label: 'ISO 27001, Annex A' },
  { key: 'iso42001', label: 'ISO 42001, Annex A' },
]
const STATUS: Record<Status, string> = { implemented: 'In place', partial: 'In part', planned: 'Planned', not_implemented: 'Not in place' }
const STATE_TONE: Record<State, string> = { ok: 'border-success/50 text-success-ink', warn: 'border-warning/60 text-warning-ink', fail: 'border-destructive/50 text-destructive-ink', unknown: '' }

const soaQuery = (framework: string) => ({
  queryKey: ['admin', 'compliance', 'soa', framework] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<SoaView>(`/api/admin/compliance/soa/${framework}`, { signal }),
})

/** One Annex A control: whether it applies (and why not), how far it is in place, and what serves it. */
function Line({ line, framework, states, mayManage }: { line: SoaLine; framework: string; states: Map<string, State | null>; mayManage: boolean }) {
  const queryClient = useQueryClient()
  const [why, setWhy] = useState(line.justification ?? '')
  const save = useMutation({
    mutationFn: (body: { applicable: boolean; status: Status; justification: string }) =>
      api(`/api/admin/compliance/soa/${framework}/${line.clause}`, { method: 'PUT', body }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: soaQuery(framework).queryKey }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const status = line.status ?? (line.controls.length > 0 ? 'implemented' : 'planned')
  const put = (over: Partial<{ applicable: boolean; status: Status; justification: string }>) =>
    save.mutate({ applicable: line.applicable, status, justification: why, ...over })
  return (
    <tr className={cn('align-top', !line.decided && 'bg-warning/5')}>
      <td className="p-2 font-mono text-xs whitespace-nowrap">{line.clause}</td>
      <td className="p-2">
        <div className="font-medium">{line.title}</div>
        <div className="mt-1 flex flex-wrap gap-1">
          {line.controls.map((c) => (
            <span key={c} className={cn('rounded border px-1.5 font-mono text-[11px]', STATE_TONE[states.get(c) ?? 'unknown'])} title={states.get(c) ? `Its checks: ${states.get(c)}` : 'Watched from outside'}>
              {c}
            </span>
          ))}
          {line.risks.length > 0 && (
            <span className="text-xs text-muted-foreground" title={line.risks.map((r) => r.title).join('\n')}>
              treats {line.risks.length} risk{line.risks.length === 1 ? '' : 's'}
            </span>
          )}
        </div>
      </td>
      <td className="p-2">
        <Switch checked={line.applicable} disabled={!mayManage} onCheckedChange={(v) => put({ applicable: v })} aria-label={`${line.clause} applies`} />
      </td>
      <td className="p-2">
        <Select value={line.applicable ? status : 'not_implemented'} disabled={!mayManage || !line.applicable} onValueChange={(v) => put({ status: v as Status })}>
          <SelectTrigger className="h-8 w-36" aria-label={`${line.clause}: how far`}>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {(Object.keys(STATUS) as Status[]).map((s) => (
              <SelectItem key={s} value={s}>
                {STATUS[s]}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </td>
      <td className="p-2">
        <Input
          className="h-8 min-w-48"
          value={why}
          disabled={!mayManage}
          placeholder={line.applicable ? 'How, or why it is so' : 'Why it does not apply (needed)'}
          aria-label={`${line.clause}: why`}
          maxLength={2000}
          onChange={(e) => setWhy(e.target.value)}
          onBlur={() => why !== (line.justification ?? '') && put({ justification: why })}
          onKeyDown={(e) => e.key === 'Enter' && (e.currentTarget as HTMLInputElement).blur()}
        />
      </td>
      <td className="p-2 text-xs whitespace-nowrap text-muted-foreground">
        {line.decided ? (
          <span className="inline-flex items-center gap-1">
            <Check className="size-3.5 text-success" aria-hidden="true" /> {line.updatedBy}
          </span>
        ) : mayManage ? (
          <Button size="sm" variant="outline" className="h-7" loading={save.isPending} onClick={() => put({})}>
            Decide
          </Button>
        ) : (
          'not decided'
        )}
      </td>
    </tr>
  )
}

/** Admin → Compliance → Statement of Applicability: each Annex A control decided, the statement approved as versions. */
export function SoaSection({ mayManage, controls }: { mayManage: boolean; controls: Control[] }) {
  const queryClient = useQueryClient()
  const [framework, setFramework] = useState('iso27001')
  const [undecided, setUndecided] = useState(false)
  const [approving, setApproving] = useState(false)
  const [note, setNote] = useState('')
  const data = useQuery(soaQuery(framework))
  const states = new Map(controls.map((c) => [c.id, c.state]))
  const approve = useMutation({
    mutationFn: () => api<Approval>(`/api/admin/compliance/soa/${framework}/approve`, { method: 'POST', body: { note } }),
    onSuccess: (a) => {
      void queryClient.invalidateQueries({ queryKey: soaQuery(framework).queryKey })
      toast.success(`Version ${a.version} approved.`)
      setApproving(false)
      setNote('')
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const v = data.data
  const decided = v?.lines.filter((l) => l.decided).length ?? 0
  const excluded = v?.lines.filter((l) => l.decided && !l.applicable).length ?? 0
  const shown = (v?.lines ?? []).filter((l) => !undecided || !l.decided)
  return (
    <section aria-labelledby="soa-title" className="grid gap-4">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div>
          <h2 id="soa-title" className="text-lg font-semibold">
            Statement of Applicability
          </h2>
          <p className="text-sm text-muted-foreground">Each Annex A control: whether it applies and why, how far it is in place, Arena's controls that serve it and the risks they treat.</p>
        </div>
        <div className="flex flex-wrap gap-2">
          <Select value={framework} onValueChange={setFramework}>
            <SelectTrigger className="w-52" aria-label="Which statement">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {FRAMEWORKS.map((f) => (
                <SelectItem key={f.key} value={f.key}>
                  {f.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Button variant="outline" asChild>
            <a href={`/api/admin/compliance/soa/${framework}/csv`} download>
              <Download /> Download
            </a>
          </Button>
          {mayManage && <Button onClick={() => setApproving(true)}>Approve</Button>}
        </div>
      </div>
      {data.error ? (
        <QueryError error={data.error} retry={() => data.refetch()} />
      ) : !v ? (
        <Skeleton className="h-96" />
      ) : (
        <>
          <div className="flex flex-wrap items-center gap-x-4 gap-y-2 text-sm">
            <span>
              {decided} of {v.lines.length} decided{excluded > 0 ? `, ${excluded} excluded` : ''}
            </span>
            {v.approved ? (
              <span className={cn(v.changedSinceApproval && 'text-warning-ink')}>
                Version {v.approved.version} approved by {v.approved.approvedBy} on {new Date(v.approved.approvedAt).toLocaleDateString()}
                {v.changedSinceApproval ? ', changed since' : ''}
              </span>
            ) : (
              <Badge variant="warning">not approved yet</Badge>
            )}
            {v.versions.length > 0 && (
              <span className="flex flex-wrap gap-2 text-xs">
                {v.versions.map((a) => (
                  <a key={a.version} className="text-primary-ink underline-offset-2 hover:underline" href={`/api/admin/compliance/soa/${framework}/csv?version=${a.version}`} download>
                    v{a.version}
                  </a>
                ))}
              </span>
            )}
            <label className="ml-auto flex items-center gap-2">
              <input type="checkbox" checked={undecided} onChange={(e) => setUndecided(e.target.checked)} /> Not decided only
            </label>
          </div>
          <div className="overflow-x-auto rounded-lg border bg-card">
            <table className="w-full text-sm">
              <thead className="border-b text-left text-xs text-muted-foreground">
                <tr>
                  <th className="p-2 font-medium">Control</th>
                  <th className="p-2 font-medium">Title, and what serves it</th>
                  <th className="p-2 font-medium">Applies</th>
                  <th className="p-2 font-medium">How far</th>
                  <th className="p-2 font-medium">Why</th>
                  <th className="p-2 font-medium">Decided</th>
                </tr>
              </thead>
              <tbody className="divide-y">
                {shown.map((l) => (
                  <Line key={`${framework}-${l.clause}`} line={l} framework={framework} states={states} mayManage={mayManage} />
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
      {approving && (
        <Dialog open onOpenChange={(o) => !o && setApproving(false)}>
          <DialogContent>
            <DialogHeader>
              <DialogTitle>Approve the statement</DialogTitle>
              <DialogDescription>In your name, as it stands: a new version, kept as it is now. Every control must be decided first.</DialogDescription>
            </DialogHeader>
            <Field label="Note (optional)" hint="What it follows: the year's risk assessment, a change of scope.">
              <Textarea value={note} onChange={(e) => setNote(e.target.value)} rows={2} maxLength={2000} />
            </Field>
            <DialogFooter>
              <Button variant="outline" onClick={() => setApproving(false)}>
                Cancel
              </Button>
              <Button loading={approve.isPending} onClick={() => approve.mutate()}>
                Approve
              </Button>
            </DialogFooter>
          </DialogContent>
        </Dialog>
      )}
    </section>
  )
}
