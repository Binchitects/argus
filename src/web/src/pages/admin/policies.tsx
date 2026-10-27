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
import { Markdown } from '../chat/markdown'

type Audience = 'Everyone' | 'Admins' | 'Groups'

interface Policy {
  id: string
  key: string
  title: string
  owner: string | null
  audience: Audience
  groups: string[]
  reviewMonths: number
  version: number | null
  approvedAt: string | null
  approvedBy: string | null
  reviewDue: string | null
  reviewOverdue: boolean
  draft: number | null
  acknowledged: number
  readers: number
}

interface Version {
  id: string
  version: number
  changes: string | null
  createdBy: string
  createdAt: string
  approvedBy: string | null
  approvedAt: string | null
  reviewDue: string | null
  reviewedBy: string | null
  reviewedAt: string | null
}

const AUDIENCE: Record<Audience, string> = { Everyone: 'Everyone who signs in', Admins: 'The admins', Groups: 'Members of groups' }
const day = (iso: string) => new Date(iso).toLocaleDateString()

const policiesQuery = {
  queryKey: ['admin', 'compliance', 'policies'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Policy[]>('/api/admin/compliance/policies', { signal }),
}

/** A policy: who owns it and must read it, its draft and versions, and who has not read the version in force. */
function PolicyDialog({ id, mayManage, onClose }: { id: string; mayManage: boolean; onClose: () => void }) {
  const queryClient = useQueryClient()
  const data = useQuery({
    queryKey: ['admin', 'compliance', 'policies', id],
    queryFn: ({ signal }) => api<{ policy: Policy; versions: Version[]; pending: string[] }>(`/api/admin/compliance/policies/${id}`, { signal }),
  })
  const groups = useQuery({ queryKey: ['admin', 'groups', 'names'], queryFn: ({ signal }) => api<{ id: string; name: string }[]>('/api/admin/groups', { signal }) })
  const p = data.data?.policy
  const versions = data.data?.versions ?? []
  const draft = versions.find((v) => !v.approvedAt)
  const shown = draft ?? versions.find((v) => v.approvedAt)
  const text = useQuery({
    queryKey: ['admin', 'compliance', 'policies', id, 'text', shown?.version],
    queryFn: ({ signal }) => api<{ text: string }>(`/api/admin/compliance/policies/${id}/versions/${shown!.version}`, { signal }),
    enabled: !!shown,
  })
  const [settings, setSettings] = useState<{ title: string; owner: string; audience: Audience; groups: string[]; reviewMonths: number } | null>(null)
  const [editing, setEditing] = useState<{ text: string; changes: string } | null>(null)
  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['admin', 'compliance', 'policies'] })
  }
  const call = useMutation({
    mutationFn: ({ path, method, body }: { path: string; method: 'POST' | 'PUT'; body?: object }) => api(`/api/admin/compliance/policies/${id}${path}`, { method, body: body ?? {} }),
    onSuccess: (_, v) => {
      refresh()
      if (v.path === '/draft') setEditing(null)
      if (v.path === '') setSettings(null)
      toast.success(v.path === '/approve' ? 'In force, and its readers are told.' : v.path === '/review' ? 'Reviewed: kept in force.' : 'Saved.')
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const s = settings ?? (p ? { title: p.title, owner: p.owner ?? '', audience: p.audience, groups: p.groups, reviewMonths: p.reviewMonths } : null)
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-3xl">
        {data.error ? (
          <QueryError error={data.error} retry={() => data.refetch()} />
        ) : !p || !s ? (
          <Skeleton className="h-96" />
        ) : (
          <>
            <DialogHeader>
              <DialogTitle>{p.title}</DialogTitle>
              <DialogDescription>
                {p.version ? `Version ${p.version} in force since ${day(p.approvedAt!)}, approved by ${p.approvedBy}` : 'Not in force yet'}
                {p.reviewDue && `; review ${p.reviewOverdue ? 'past due since' : 'due'} ${day(p.reviewDue)}`}
              </DialogDescription>
            </DialogHeader>
            <div className="grid max-h-[68vh] gap-5 overflow-y-auto pr-1 text-sm">
              <section aria-label="Who and when" className="grid gap-3 sm:grid-cols-2">
                <Field label="Title">
                  <Input value={s.title} disabled={!mayManage} maxLength={200} onChange={(e) => setSettings({ ...s, title: e.target.value })} />
                </Field>
                <Field label="Owner">
                  <Input value={s.owner} disabled={!mayManage} maxLength={256} onChange={(e) => setSettings({ ...s, owner: e.target.value })} />
                </Field>
                <Field label="Who must read it">
                  <Select value={s.audience} disabled={!mayManage} onValueChange={(v) => setSettings({ ...s, audience: v as Audience })}>
                    <SelectTrigger>
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      {(Object.keys(AUDIENCE) as Audience[]).map((a) => (
                        <SelectItem key={a} value={a}>
                          {AUDIENCE[a]}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                </Field>
                <Field label="Reviewed every (months)">
                  <Input type="number" min={1} max={36} value={s.reviewMonths} disabled={!mayManage} onChange={(e) => setSettings({ ...s, reviewMonths: Number(e.target.value) })} />
                </Field>
                {s.audience === 'Groups' && (
                  <fieldset className="grid gap-1.5 sm:col-span-2">
                    <legend className="mb-1 text-sm font-medium">Groups</legend>
                    <div className="flex flex-wrap gap-1.5">
                      {(groups.data ?? []).map((g) => (
                        <button
                          key={g.id}
                          type="button"
                          aria-pressed={s.groups.includes(g.id)}
                          disabled={!mayManage}
                          onClick={() => setSettings({ ...s, groups: s.groups.includes(g.id) ? s.groups.filter((x) => x !== g.id) : [...s.groups, g.id] })}
                          className={cn('rounded-md border px-2 py-0.5 text-xs', s.groups.includes(g.id) ? 'border-primary bg-primary/10 text-primary-ink' : 'text-muted-foreground')}
                        >
                          {g.name}
                        </button>
                      ))}
                    </div>
                  </fieldset>
                )}
                {mayManage && settings && (
                  <div className="sm:col-span-2">
                    <Button size="sm" loading={call.isPending} onClick={() => call.mutate({ path: '', method: 'PUT', body: settings })}>
                      Save these
                    </Button>
                  </div>
                )}
              </section>

              <section aria-label="The text" className="grid gap-2">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <h3 className="font-medium">{draft ? `Draft, version ${draft.version}` : shown ? `Version ${shown.version}, in force` : 'Text'}</h3>
                  {mayManage && !editing && (
                    <Button size="sm" variant="outline" disabled={!text.data} onClick={() => setEditing({ text: text.data?.text ?? '', changes: draft?.changes ?? '' })}>
                      {draft ? 'Edit the draft' : 'Start a new version'}
                    </Button>
                  )}
                </div>
                {editing ? (
                  <div className="grid gap-2">
                    <Textarea value={editing.text} onChange={(e) => setEditing({ ...editing, text: e.target.value })} rows={16} className="font-mono text-xs" aria-label="The policy's text (Markdown)" />
                    {(draft?.version ?? (p.version ?? 0) + 1) > 1 && (
                      <Field label="What changed from the version before">
                        <Input value={editing.changes} onChange={(e) => setEditing({ ...editing, changes: e.target.value })} maxLength={2000} />
                      </Field>
                    )}
                    <div className="flex gap-2">
                      <Button size="sm" loading={call.isPending} onClick={() => call.mutate({ path: '/draft', method: 'PUT', body: editing })}>
                        Save the draft
                      </Button>
                      <Button size="sm" variant="outline" onClick={() => setEditing(null)}>
                        Cancel
                      </Button>
                    </div>
                  </div>
                ) : text.data ? (
                  <div className="max-h-80 overflow-y-auto rounded-lg border p-3">
                    <Markdown text={text.data.text} />
                  </div>
                ) : (
                  <Skeleton className="h-40" />
                )}
              </section>

              {p.version && (
                <section aria-label="Not read yet" className="grid gap-1">
                  <h3 className="font-medium">
                    Read by {p.acknowledged} of {p.readers}
                  </h3>
                  {data.data!.pending.length > 0 && <p className="text-muted-foreground">Not yet: {data.data!.pending.join(', ')}</p>}
                </section>
              )}

              <section aria-label="Versions" className="grid gap-1">
                <h3 className="font-medium">Versions</h3>
                <ol className="grid gap-1">
                  {versions.map((v) => (
                    <li key={v.id} className="text-muted-foreground">
                      <span className="font-medium text-foreground">v{v.version}</span>{' '}
                      {v.approvedAt ? `approved by ${v.approvedBy} on ${day(v.approvedAt)}` : `draft by ${v.createdBy}`}
                      {v.changes && `: ${v.changes}`}
                      {v.reviewedAt && `; reviewed by ${v.reviewedBy} on ${day(v.reviewedAt)}`}
                    </li>
                  ))}
                </ol>
              </section>
            </div>
            {mayManage && (
              <DialogFooter className="gap-2 sm:justify-start">
                <Button disabled={!draft || !!editing} loading={call.isPending} onClick={() => call.mutate({ path: '/approve', method: 'POST' })}>
                  Approve the draft
                </Button>
                <Button variant="outline" disabled={!p.version || !!draft} loading={call.isPending} onClick={() => call.mutate({ path: '/review', method: 'POST' })}>
                  Reviewed, keep it
                </Button>
              </DialogFooter>
            )}
          </>
        )}
      </DialogContent>
    </Dialog>
  )
}

/** Admin → Compliance → Policies: the company's policies, each in force or a draft, its review and who has read it. */
export function PoliciesSection({ mayManage }: { mayManage: boolean }) {
  const queryClient = useQueryClient()
  const data = useQuery(policiesQuery)
  const templates = useQuery({ queryKey: ['admin', 'compliance', 'policies', 'templates'], queryFn: ({ signal }) => api<{ key: string; title: string }[]>('/api/admin/compliance/policies/templates', { signal }) })
  const [open, setOpen] = useState<string | null>(null)
  const [template, setTemplate] = useState('')
  const [title, setTitle] = useState('')
  const adopted = new Set((data.data ?? []).map((p) => p.key))
  const create = useMutation({
    mutationFn: (body: { template?: string; title?: string }) => api<Policy>('/api/admin/compliance/policies', { method: 'POST', body }),
    onSuccess: (made) => {
      void queryClient.invalidateQueries({ queryKey: policiesQuery.queryKey })
      setTemplate('')
      setTitle('')
      setOpen(made.id)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <section aria-labelledby="policies-title" className="grid gap-4">
      <div>
        <h2 id="policies-title" className="text-lg font-semibold">
          Policies
        </h2>
        <p className="text-sm text-muted-foreground">The company's policies: each owned, approved as versions, reviewed on time, and read by those who must.</p>
      </div>
      {mayManage && (
        <div className="flex flex-wrap items-end gap-2">
          <Field label="Adopt a template" className="w-72">
            <Select value={template} onValueChange={setTemplate}>
              <SelectTrigger>
                <SelectValue placeholder="Choose one" />
              </SelectTrigger>
              <SelectContent>
                {(templates.data ?? []).filter((t) => !adopted.has(t.key)).map((t) => (
                  <SelectItem key={t.key} value={t.key}>
                    {t.title}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
          <Button variant="outline" disabled={!template} loading={create.isPending && !!template} onClick={() => create.mutate({ template })}>
            Adopt
          </Button>
          <span className="px-2 pb-2 text-sm text-muted-foreground">or</span>
          <Field label="A policy of your own" className="w-64">
            <Input value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} placeholder="Its title" />
          </Field>
          <Button variant="outline" disabled={!title.trim()} onClick={() => create.mutate({ title })}>
            <Plus /> Write it
          </Button>
        </div>
      )}
      {data.error ? (
        <QueryError error={data.error} retry={() => data.refetch()} />
      ) : !data.data ? (
        <Skeleton className="h-40" />
      ) : data.data.length === 0 ? (
        <p className="rounded-lg border bg-card p-6 text-sm text-muted-foreground">No policy yet. Adopt the templates that apply: they name each framework's clauses they serve.</p>
      ) : (
        <ul className="divide-y rounded-lg border bg-card">
          {data.data.map((p) => (
            <li key={p.id}>
              <button type="button" onClick={() => setOpen(p.id)} className="flex w-full flex-wrap items-center gap-x-3 gap-y-1 p-3 text-left text-sm hover:bg-muted/50">
                <span className="min-w-48 flex-1 font-medium">{p.title}</span>
                {p.version ? <Badge variant="success">v{p.version} in force</Badge> : <Badge variant="secondary">not in force</Badge>}
                {p.draft && <Badge variant="warning">draft v{p.draft}</Badge>}
                {p.owner && <span className="text-xs text-muted-foreground">{p.owner}</span>}
                {p.reviewDue && <span className={cn('text-xs', p.reviewOverdue ? 'font-medium text-destructive-ink' : 'text-muted-foreground')}>review {day(p.reviewDue)}</span>}
                {p.version && (
                  <span className={cn('text-xs', p.acknowledged < p.readers ? 'text-warning-ink' : 'text-muted-foreground')}>
                    read by {p.acknowledged} of {p.readers}
                  </span>
                )}
              </button>
            </li>
          ))}
        </ul>
      )}
      {open && <PolicyDialog id={open} mayManage={mayManage} onClose={() => setOpen(null)} />}
    </section>
  )
}
