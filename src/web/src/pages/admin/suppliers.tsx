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

interface Supplier {
  id: string
  name: string
  kind: string
  service: string | null
  data: string[]
  location: string | null
  certifications: string | null
  agreements: string | null
  risk: 'low' | 'medium' | 'high'
  owner: string | null
  notes: string | null
  source: string | null
  reviewDue: string
  reviewOverdue: boolean
  reviewedAt: string | null
  reviewedBy: string | null
  retiredAt: string | null
  agreementMissing: boolean
}

interface Suggestion {
  source: string
  name: string
  kind: string
  service: string
}

const KINDS: Record<string, string> = { model: 'Models', cloud: 'Cloud', connector: 'Connector', software: 'Software', service: 'Service', other: 'Other' }
const DATA = ['personal', 'health', 'confidential', 'code', 'payment']
const RISK_BADGE = { low: 'success', medium: 'warning', high: 'destructive' } as const
const day = (iso: string) => new Date(iso).toLocaleDateString()

const suppliersQuery = {
  queryKey: ['admin', 'compliance', 'suppliers'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ suppliers: Supplier[]; suggested: Suggestion[] }>('/api/admin/compliance/suppliers', { signal }),
}

function SupplierForm({ supplier, from, mayManage, onClose }: { supplier: Supplier | null; from: Suggestion | null; mayManage: boolean; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [f, setF] = useState({
    name: supplier?.name ?? from?.name ?? '', kind: supplier?.kind ?? from?.kind ?? 'service', service: supplier?.service ?? from?.service ?? '', data: supplier?.data ?? [],
    location: supplier?.location ?? '', certifications: supplier?.certifications ?? '', agreements: supplier?.agreements ?? '', risk: supplier?.risk ?? 'medium',
    owner: supplier?.owner ?? '', notes: supplier?.notes ?? '', source: supplier?.source ?? from?.source ?? null,
  })
  const [note, setNote] = useState('')
  const refresh = () => void queryClient.invalidateQueries({ queryKey: suppliersQuery.queryKey })
  const save = useMutation({
    mutationFn: () => api(supplier ? `/api/admin/compliance/suppliers/${supplier.id}` : '/api/admin/compliance/suppliers', { method: supplier ? 'PUT' : 'POST', body: f }),
    onSuccess: () => {
      refresh()
      onClose()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const step = useMutation({
    mutationFn: ({ path, body }: { path: string; body?: object }) => api(`/api/admin/compliance/suppliers/${supplier!.id}${path}`, { method: 'POST', body: body ?? {} }),
    onSuccess: () => {
      refresh()
      onClose()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const toggle = (d: string) => setF({ ...f, data: f.data.includes(d) ? f.data.filter((x) => x !== d) : [...f.data, d] })
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{supplier ? supplier.name : 'Register a supplier'}</DialogTitle>
          <DialogDescription>What it provides and the data it gets, where, its certifications and agreements, and its risk (which sets how often it is reviewed).</DialogDescription>
        </DialogHeader>
        <form
          className="grid max-h-[68vh] gap-4 overflow-y-auto pr-1"
          onSubmit={(e) => {
            e.preventDefault()
            if (mayManage) save.mutate()
          }}
        >
          <div className="grid gap-4 sm:grid-cols-[1fr_10rem_10rem]">
            <Field label="Name">
              <Input value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} maxLength={200} required />
            </Field>
            <Field label="Kind">
              <Select value={f.kind} onValueChange={(v) => setF({ ...f, kind: v })}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {Object.entries(KINDS).map(([k, v]) => (
                    <SelectItem key={k} value={k}>
                      {v}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
            <Field label="Risk">
              <Select value={f.risk} onValueChange={(v) => setF({ ...f, risk: v as Supplier['risk'] })}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {(['low', 'medium', 'high'] as const).map((r) => (
                    <SelectItem key={r} value={r}>
                      {r}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
          </div>
          <Field label="What it provides">
            <Input value={f.service} onChange={(e) => setF({ ...f, service: e.target.value })} maxLength={1000} />
          </Field>
          <fieldset className="grid gap-1.5">
            <legend className="mb-1 text-sm font-medium">The data it gets</legend>
            <div className="flex flex-wrap gap-1.5">
              {DATA.map((d) => (
                <button
                  key={d}
                  type="button"
                  aria-pressed={f.data.includes(d)}
                  onClick={() => toggle(d)}
                  className={cn('rounded-md border px-2 py-0.5 text-xs', f.data.includes(d) ? 'border-primary bg-primary/10 text-primary-ink' : 'text-muted-foreground')}
                >
                  {d}
                </button>
              ))}
            </div>
          </fieldset>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Where" hint="A country or region.">
              <Input value={f.location} onChange={(e) => setF({ ...f, location: e.target.value })} maxLength={200} />
            </Field>
            <Field label="Owner">
              <Input value={f.owner} onChange={(e) => setF({ ...f, owner: e.target.value })} maxLength={256} />
            </Field>
          </div>
          <Field label="Certifications and reports" hint="With their dates: SOC 2 Type II to 2026-06; ISO 27001 until 2027-05.">
            <Input value={f.certifications} onChange={(e) => setF({ ...f, certifications: e.target.value })} maxLength={2000} />
          </Field>
          <Field label="Agreements" hint="A DPA, a HIPAA BAA, standard contractual clauses.">
            <Input value={f.agreements} onChange={(e) => setF({ ...f, agreements: e.target.value })} maxLength={2000} />
          </Field>
          <Field label="Notes">
            <Textarea value={f.notes} onChange={(e) => setF({ ...f, notes: e.target.value })} rows={2} maxLength={4000} />
          </Field>
          {supplier && mayManage && !supplier.retiredAt && (
            <div className="grid gap-2 rounded-lg border p-3">
              <Field label="Review: what was found">
                <Input value={note} onChange={(e) => setNote(e.target.value)} maxLength={2000} />
              </Field>
              <div className="flex flex-wrap gap-2">
                <Button type="button" variant="outline" disabled={!note.trim()} loading={step.isPending} onClick={() => step.mutate({ path: '/review', body: { note } })}>
                  Reviewed
                </Button>
                <Button type="button" variant="ghost" loading={step.isPending} onClick={() => step.mutate({ path: '/retire' })}>
                  No longer used
                </Button>
              </div>
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

/** Admin → Compliance → Suppliers: the register, and what Arena connects to that is not in it yet. */
export function SuppliersSection({ mayManage }: { mayManage: boolean }) {
  const data = useQuery(suppliersQuery)
  const [form, setForm] = useState<{ supplier: Supplier | null; from: Suggestion | null } | null>(null)
  const v = data.data
  return (
    <section aria-labelledby="suppliers-title" className="grid gap-4">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div>
          <h2 id="suppliers-title" className="text-lg font-semibold">
            Suppliers
          </h2>
          <p className="text-sm text-muted-foreground">Each supplier, the data it gets and where, its certifications and agreements, its risk and its review.</p>
        </div>
        {mayManage && (
          <Button onClick={() => setForm({ supplier: null, from: null })}>
            <Plus /> Register a supplier
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
              <p className="font-medium">Arena connects to these, which the register does not hold yet:</p>
              <ul className="grid gap-1">
                {v.suggested.map((s) => (
                  <li key={s.source} className="flex flex-wrap items-center gap-2">
                    <span className="flex-1">
                      {s.name} <span className="text-muted-foreground">({s.service})</span>
                    </span>
                    {mayManage && (
                      <Button size="sm" variant="outline" onClick={() => setForm({ supplier: null, from: s })}>
                        Register
                      </Button>
                    )}
                  </li>
                ))}
              </ul>
            </div>
          )}
          {v.suppliers.length === 0 ? (
            <p className="text-sm text-muted-foreground">The register is empty.</p>
          ) : (
            <ul className="divide-y rounded-lg border bg-card">
              {v.suppliers.map((s) => (
                <li key={s.id}>
                  <button type="button" onClick={() => setForm({ supplier: s, from: null })} className="flex w-full flex-wrap items-center gap-x-3 gap-y-1 p-3 text-left text-sm hover:bg-muted/50">
                    <span className={cn('min-w-40 flex-1 font-medium', s.retiredAt && 'text-muted-foreground line-through')}>{s.name}</span>
                    <span className="text-xs text-muted-foreground">{KINDS[s.kind] ?? s.kind}</span>
                    <Badge variant={RISK_BADGE[s.risk]}>{s.risk}</Badge>
                    {s.data.length > 0 && <span className="text-xs text-muted-foreground">{s.data.join(', ')}</span>}
                    {s.agreementMissing && <Badge variant="warning">no agreement</Badge>}
                    {!s.retiredAt && <span className={cn('text-xs', s.reviewOverdue ? 'font-medium text-destructive-ink' : 'text-muted-foreground')}>review {day(s.reviewDue)}</span>}
                  </button>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
      {form && <SupplierForm supplier={form.supplier} from={form.from} mayManage={mayManage} onClose={() => setForm(null)} />}
    </section>
  )
}
