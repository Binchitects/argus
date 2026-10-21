import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Check, Plus, ShieldCheck, Trash2, X } from 'lucide-react'
import { useState, type ReactNode } from 'react'
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
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { accessQuery, type AccessCheck, type AccessOverview, type Subject, type SubjectKind } from './access-api'
import { ManyPeople } from './access-bulk'
import { groupsQuery } from './groups-api'
import { peopleQuery } from './people-api'

const day = (iso: string | null) => (iso ? new Date(iso).toLocaleDateString() : null)

export function AccessPage() {
  const access = useQuery(accessQuery)
  return (
    <>
      <PageHeader
        title="Access"
        description="Roles say what people may run; rules give or take a model, a tool, a knowledge source, a repository or a feature. The most specific wins: a person's own over their groups', a group's over everyone's."
      />
      {access.error ? (
        <QueryError error={access.error} retry={() => access.refetch()} />
      ) : !access.data ? (
        <Skeleton className="h-64" />
      ) : (
        <Tabs defaultValue={new URLSearchParams(window.location.search).get('tab') ?? 'roles'}>
          <TabsList>
            <TabsTrigger value="roles">Roles</TabsTrigger>
            <TabsTrigger value="rules">Rules</TabsTrigger>
            <TabsTrigger value="check">Checker</TabsTrigger>
            <TabsTrigger value="many">Many people</TabsTrigger>
            <TabsTrigger value="requests">Requests</TabsTrigger>
            <TabsTrigger value="history">History</TabsTrigger>
          </TabsList>
          <TabsContent value="roles" className="mt-4">
            <Roles data={access.data} />
          </TabsContent>
          <TabsContent value="rules" className="mt-4">
            <Rules data={access.data} />
          </TabsContent>
          <TabsContent value="check" className="mt-4">
            <Checker data={access.data} />
          </TabsContent>
          <TabsContent value="many" className="mt-4">
            <ManyPeople data={access.data} />
          </TabsContent>
          <TabsContent value="requests" className="mt-4">
            <Requests />
          </TabsContent>
          <TabsContent value="history" className="mt-4">
            <History />
          </TabsContent>
        </Tabs>
      )}
    </>
  )
}

function useChange() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ path, method, body }: { path: string; method?: string; body?: unknown }) => api(path, { method, body }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['admin', 'access'] }),
    onError: (e) => toast.error(errorMessage(e)),
  })
}

function SubjectBadge({ subject }: { subject: Subject }) {
  return (
    <Badge variant={subject.kind === 'Everyone' ? 'secondary' : 'outline'} title={subject.kind}>
      {subject.name}
    </Badge>
  )
}

function Roles({ data }: { data: AccessOverview }) {
  const change = useChange()
  const confirm = useConfirm()
  const [editing, setEditing] = useState<AccessOverview['roles'][number] | 'new' | null>(null)
  const [giving, setGiving] = useState<string | null>(null)
  return (
    <div className="grid gap-4">
      <div className="flex justify-end">
        <Button onClick={() => setEditing('new')}>
          <Plus /> New role
        </Button>
      </div>
      {data.roles.map((r) => (
        <Card key={r.id}>
          <CardHeader>
            <CardTitle className="flex flex-wrap items-center gap-2">
              {r.name}
              {r.builtIn ? <Badge variant="secondary">Built-in</Badge> : <Badge variant="outline">Custom</Badge>}
              <span className="text-xs font-normal text-muted-foreground">{r.permissions.length} permissions</span>
            </CardTitle>
            {r.description && <CardDescription>{r.description}</CardDescription>}
          </CardHeader>
          <CardContent className="grid gap-3">
            <ul className="flex flex-wrap gap-1.5" aria-label={`Who holds ${r.name}`}>
              {r.holders.length === 0 && <li className="text-sm text-muted-foreground">Nobody holds it.</li>}
              {r.holders.map((h) => (
                <li key={h.id} className="flex items-center gap-1 rounded-md border px-2 py-0.5 text-sm">
                  {h.subject.name}
                  {h.expiresAt && <span className="text-xs text-muted-foreground">until {day(h.expiresAt)}</span>}
                  {!!h.scope?.groups.length && <span className="text-xs text-muted-foreground">for {h.scope.groups.length} groups</span>}
                  <Button
                    size="icon-sm"
                    variant="ghost"
                    className="size-6"
                    aria-label={`Take ${r.name} from ${h.subject.name}`}
                    onClick={async () => {
                      if (await confirm({ title: `Take ${r.name} from ${h.subject.name}?`, confirm: 'Take it', destructive: true })) {
                        change.mutate({ path: `/api/admin/access/assignments/${h.id}`, method: 'DELETE' })
                      }
                    }}
                  >
                    <X />
                  </Button>
                </li>
              ))}
            </ul>
            <div className="flex flex-wrap gap-2">
              <Button size="sm" variant="outline" onClick={() => setGiving(r.id)}>
                <Plus /> Give it
              </Button>
              <Button size="sm" variant="outline" onClick={() => setEditing(r)}>
                {r.builtIn ? 'See permissions' : 'Edit'}
              </Button>
              {!r.builtIn && (
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={async () => {
                    if (await confirm({ title: `Delete ${r.name}?`, description: 'Only a role nobody holds can go.', confirm: 'Delete', destructive: true })) {
                      change.mutate({ path: `/api/admin/access/roles/${r.id}`, method: 'DELETE' })
                    }
                  }}
                >
                  <Trash2 /> Delete
                </Button>
              )}
            </div>
          </CardContent>
        </Card>
      ))}
      {editing && <RoleDialog data={data} role={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
      {giving && <GiveDialog role={data.roles.find((r) => r.id === giving)!} onClose={() => setGiving(null)} />}
    </div>
  )
}

function RoleDialog({ data, role, onClose }: { data: AccessOverview; role: AccessOverview['roles'][number] | null; onClose: () => void }) {
  const queryClient = useQueryClient()
  const readOnly = role?.builtIn ?? false
  const [name, setName] = useState(role?.name ?? '')
  const [description, setDescription] = useState(role?.description ?? '')
  const [held, setHeld] = useState(new Set(role?.permissions ?? []))
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const areas = [...new Set(data.permissions.map((p) => p.area))]
  const save = async () => {
    setSaving(true)
    setError(null)
    try {
      const body = { name, description, permissions: [...held] }
      await (role ? api(`/api/admin/access/roles/${role.id}`, { method: 'PATCH', body }) : api('/api/admin/access/roles', { body }))
      await queryClient.invalidateQueries({ queryKey: ['admin', 'access'] })
      onClose()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setSaving(false)
    }
  }
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{role ? role.name : 'New role'}</DialogTitle>
          <DialogDescription>
            {readOnly ? 'A built-in role stays as it is: make a role of your own to hold something else.' : 'What its holders may do. Give it to people or groups afterwards.'}
          </DialogDescription>
        </DialogHeader>
        <div className="grid gap-4">
          {error && <Alert variant="destructive">{error}</Alert>}
          {!readOnly && (
            <>
              <Field label="Name">
                <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. Team manager" />
              </Field>
              <Field label="What it is for">
                <Textarea rows={2} value={description} onChange={(e) => setDescription(e.target.value)} />
              </Field>
            </>
          )}
          {areas.map((area) => (
            <fieldset key={area} className="grid gap-1.5">
              <legend className="mb-1 text-sm font-medium">{area}</legend>
              {data.permissions
                .filter((p) => p.area === area && !p.parent)
                .flatMap((p) => [p, ...data.permissions.filter((c) => c.parent === p.key)])
                .map((p) => (
                  <div key={p.key} className={`flex items-start gap-2 text-sm ${p.parent ? 'ml-6' : ''}`}>
                    <Checkbox
                      id={`perm-${p.key}`}
                      checked={held.has(p.key) || (!!p.parent && held.has(p.parent))}
                      disabled={readOnly || (!!p.parent && held.has(p.parent))}
                      onCheckedChange={(on) => {
                        const next = new Set(held)
                        if (on) next.add(p.key)
                        else next.delete(p.key)
                        setHeld(next)
                      }}
                      className="mt-0.5"
                    />
                    <label htmlFor={`perm-${p.key}`}>
                      <span className="font-mono text-xs">{p.key}</span> <span className="text-muted-foreground">{p.description}</span>
                    </label>
                  </div>
                ))}
            </fieldset>
          ))}
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            {readOnly ? 'Close' : 'Cancel'}
          </Button>
          {!readOnly && (
            <Button loading={saving} onClick={save} disabled={!name.trim()}>
              {role ? 'Save' : 'Make role'}
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

/** A person, a group or everyone, chosen from the lists. */
function SubjectPicker({ kind, setKind, id, setId, allowEveryone = true }: { kind: SubjectKind; setKind: (k: SubjectKind) => void; id: string; setId: (id: string) => void; allowEveryone?: boolean }) {
  const people = useQuery({ ...peopleQuery, enabled: kind === 'Person' })
  const groups = useQuery({ ...groupsQuery, enabled: kind === 'Group' })
  const options =
    kind === 'Person'
      ? (people.data?.people ?? []).map((p) => ({ id: p.id, label: `${p.displayName || p.userName} (${p.userName})` }))
      : (groups.data ?? []).map((g) => ({ id: g.id, label: g.directory ? `${g.name} (directory: ${g.directory})` : g.name }))
  return (
    <div className="grid gap-3">
      <Segmented
        label="Who"
        value={kind}
        onChange={(k) => {
          setKind(k)
          setId('')
        }}
        options={[
          { value: 'Person' as SubjectKind, label: 'A person' },
          { value: 'Group' as SubjectKind, label: 'A group' },
          ...(allowEveryone ? [{ value: 'Everyone' as SubjectKind, label: 'Everyone' }] : []),
        ]}
      />
      {kind !== 'Everyone' && (
        <Field label={kind === 'Person' ? 'Person' : 'Group'} hint={kind === 'Group' ? 'A directory group linked on the Groups page counts its directory members.' : undefined}>
          <Select value={id} onValueChange={setId}>
            <SelectTrigger>
              <SelectValue placeholder="Choose…" />
            </SelectTrigger>
            <SelectContent>
              {options.map((o) => (
                <SelectItem key={o.id} value={o.id}>
                  {o.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>
      )}
    </div>
  )
}

function GiveDialog({ role, onClose }: { role: AccessOverview['roles'][number]; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [kind, setKind] = useState<SubjectKind>('Group')
  const [id, setId] = useState('')
  const [until, setUntil] = useState('')
  const [only, setOnly] = useState('all')
  const groups = useQuery(groupsQuery)
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const give = async () => {
    setSaving(true)
    setError(null)
    try {
      await api('/api/admin/access/assignments', {
        body: {
          roleId: role.id,
          subjectKind: kind,
          subjectId: kind === 'Everyone' ? null : id,
          expiresAt: until ? new Date(until).toISOString() : null,
          ...(only !== 'all' && { scope: { groups: [only], repos: [] } }),
        },
      })
      await queryClient.invalidateQueries({ queryKey: ['admin', 'access'] })
      onClose()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setSaving(false)
    }
  }
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Give {role.name}</DialogTitle>
          <DialogDescription>To a person, a group (a directory group linked to one too) or everyone, maybe until a date.</DialogDescription>
        </DialogHeader>
        <div className="grid gap-4">
          {error && <Alert variant="destructive">{error}</Alert>}
          <SubjectPicker kind={kind} setKind={setKind} id={id} setId={setId} />
          <Field label="Over" hint="A team manager: the role reaches only that group's people (People admin, Billing).">
            <Select value={only} onValueChange={setOnly}>
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="all">Everyone</SelectItem>
                {(groups.data ?? []).map((g) => (
                  <SelectItem key={g.id} value={g.id}>
                    Only the members of {g.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
          <Field label="Until" hint="Empty: until it is taken.">
            <Input type="date" value={until} onChange={(e) => setUntil(e.target.value)} />
          </Field>
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button loading={saving} disabled={kind !== 'Everyone' && !id} onClick={give}>
            Give it
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function Rules({ data }: { data: AccessOverview }) {
  const change = useChange()
  const confirm = useConfirm()
  const [adding, setAdding] = useState(false)
  return (
    <div className="grid gap-4">
      <div className="flex justify-end">
        <Button onClick={() => setAdding(true)}>
          <Plus /> New rule
        </Button>
      </div>
      {data.rules.length === 0 ? (
        <p className="text-sm text-muted-foreground">No rules: everyone may use every model, tool and knowledge source.</p>
      ) : (
        <div className="overflow-x-auto rounded-md border">
          <table className="w-full text-sm">
            <thead className="bg-muted/50 text-left text-xs text-muted-foreground">
              <tr>
                <th className="px-3 py-2 font-medium">Effect</th>
                <th className="px-3 py-2 font-medium">Who</th>
                <th className="px-3 py-2 font-medium">What</th>
                <th className="px-3 py-2 font-medium">Note</th>
                <th className="px-3 py-2">
                  <span className="sr-only">Remove</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {data.rules.map((r) => (
                <tr key={r.id} className="border-t">
                  <td className="px-3 py-2">
                    <Badge variant={r.effect === 'Deny' ? 'destructive' : 'success'}>{r.effect === 'Deny' ? 'Deny' : 'Allow'}</Badge>
                  </td>
                  <td className="px-3 py-2">
                    <SubjectBadge subject={r.subject} />
                  </td>
                  <td className="px-3 py-2">
                    <span className="text-muted-foreground">{r.action} </span>
                    <span className="font-mono text-xs">
                      {r.resourceKind}:{r.resource}
                    </span>
                    {r.expiresAt && <span className="ml-2 text-xs text-muted-foreground">until {day(r.expiresAt)}</span>}
                  </td>
                  <td className="px-3 py-2 text-muted-foreground">{r.note}</td>
                  <td className="px-3 py-2 text-right">
                    <Button
                      size="icon-sm"
                      variant="ghost"
                      aria-label="Remove the rule"
                      onClick={async () => {
                        if (await confirm({ title: 'Remove this rule?', confirm: 'Remove', destructive: true })) {
                          change.mutate({ path: `/api/admin/access/rules/${r.id}`, method: 'DELETE' })
                        }
                      }}
                    >
                      <Trash2 />
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {adding && <RuleDialog data={data} onClose={() => setAdding(false)} />}
    </div>
  )
}

function RuleDialog({ data, onClose }: { data: AccessOverview; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [effect, setEffect] = useState<'Allow' | 'Deny'>('Deny')
  const [kind, setKind] = useState<SubjectKind>('Group')
  const [id, setId] = useState('')
  const [resourceKind, setResourceKind] = useState('feature')
  const [resource, setResource] = useState('')
  const [action, setAction] = useState('')
  const [note, setNote] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const actions = data.resourceKinds.find((k) => k.kind === resourceKind)?.actions ?? []
  const features = data.permissions.filter((p) => !p.admin)
  const add = async () => {
    setSaving(true)
    setError(null)
    try {
      await api('/api/admin/access/rules', {
        body: { effect, subjectKind: kind, subjectId: kind === 'Everyone' ? null : id, resourceKind, resource, action: action || null, note: note || null },
      })
      await queryClient.invalidateQueries({ queryKey: ['admin', 'access'] })
      onClose()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setSaving(false)
    }
  }
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>New rule</DialogTitle>
          <DialogDescription>A person's own rule wins over their groups', a group's over everyone's; at one level a deny wins.</DialogDescription>
        </DialogHeader>
        <div className="grid gap-4">
          {error && <Alert variant="destructive">{error}</Alert>}
          <Segmented
            label="Effect"
            value={effect}
            onChange={setEffect}
            options={[
              { value: 'Deny', label: 'Deny' },
              { value: 'Allow', label: 'Allow' },
            ]}
          />
          <SubjectPicker kind={kind} setKind={setKind} id={id} setId={setId} />
          <Field label="Kind of thing">
            <Select
              value={resourceKind}
              onValueChange={(k) => {
                setResourceKind(k)
                setResource('')
                setAction('')
              }}
            >
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {data.resourceKinds.map((k) => (
                  <SelectItem key={k.kind} value={k.kind}>
                    {k.kind.replace('_', ' ')}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
          {resourceKind === 'feature' ? (
            <Field label="Feature">
              <Select value={resource} onValueChange={setResource}>
                <SelectTrigger>
                  <SelectValue placeholder="Choose…" />
                </SelectTrigger>
                <SelectContent>
                  {features.map((p) => (
                    <SelectItem key={p.key} value={p.key}>
                      {p.key}: {p.description}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
          ) : (
            <Field label="Name" hint="Its name or id; * matches any part (team-a/**, qwen*).">
              <Input value={resource} onChange={(e) => setResource(e.target.value)} />
            </Field>
          )}
          {actions.length > 1 && (
            <Field label="Action">
              <Select value={action || actions[0]} onValueChange={setAction}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {actions.map((a) => (
                    <SelectItem key={a} value={a}>
                      {a}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
          )}
          <Field label="Note">
            <Input value={note} onChange={(e) => setNote(e.target.value)} placeholder="Why, for whoever reads it later" />
          </Field>
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button loading={saving} disabled={!resource.trim() || (kind !== 'Everyone' && !id)} onClick={add}>
            Add rule
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function Checker({ data }: { data: AccessOverview }) {
  const people = useQuery(peopleQuery)
  const [person, setPerson] = useState('')
  const [mode, setMode] = useState<'permission' | 'resource'>('permission')
  const [permission, setPermission] = useState('')
  const [resourceKind, setResourceKind] = useState('model')
  const [resource, setResource] = useState('')
  const [answer, setAnswer] = useState<AccessCheck | null>(null)
  const [error, setError] = useState<string | null>(null)
  const ask = async () => {
    setError(null)
    setAnswer(null)
    const q = new URLSearchParams({ person })
    if (mode === 'permission') q.set('permission', permission)
    else {
      q.set('kind', resourceKind)
      q.set('resource', resource)
    }
    try {
      setAnswer(await api<AccessCheck>(`/api/admin/access/check?${q}`))
    } catch (e) {
      setError(errorMessage(e))
    }
  }
  return (
    <Card>
      <CardHeader>
        <CardTitle>May this person…?</CardTitle>
        <CardDescription>The answer, with the rules and roles that decide it, in order.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <Field label="Person">
          <Select value={person} onValueChange={setPerson}>
            <SelectTrigger>
              <SelectValue placeholder="Choose…" />
            </SelectTrigger>
            <SelectContent>
              {(people.data?.people ?? []).map((p) => (
                <SelectItem key={p.id} value={p.id}>
                  {p.displayName || p.userName} ({p.userName})
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>
        <Segmented
          label="About"
          value={mode}
          onChange={setMode}
          options={[
            { value: 'permission', label: 'A permission' },
            { value: 'resource', label: 'A model, tool…' },
          ]}
        />
        {mode === 'permission' ? (
          <Field label="Permission">
            <Select value={permission} onValueChange={setPermission}>
              <SelectTrigger>
                <SelectValue placeholder="Choose…" />
              </SelectTrigger>
              <SelectContent>
                {data.permissions.map((p) => (
                  <SelectItem key={p.key} value={p.key}>
                    {p.key}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
        ) : (
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Kind">
              <Select value={resourceKind} onValueChange={setResourceKind}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {data.resourceKinds.map((k) => (
                    <SelectItem key={k.kind} value={k.kind}>
                      {k.kind.replace('_', ' ')}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
            <Field label="Name">
              <Input value={resource} onChange={(e) => setResource(e.target.value)} />
            </Field>
          </div>
        )}
        <div>
          <Button onClick={ask} disabled={!person || (mode === 'permission' ? !permission : !resource.trim())}>
            <ShieldCheck /> Check
          </Button>
        </div>
        {error && <Alert variant="destructive">{error}</Alert>}
        {answer && (
          <output className="grid gap-2">
            <p className="flex items-center gap-2 font-medium">
              {answer.allowed ? <Check className="size-4 text-success-ink" /> : <X className="size-4 text-destructive-ink" />}
              {answer.allowed ? 'Yes' : 'No'}
              {answer.isDisabled && <Badge variant="destructive">Disabled</Badge>}
            </p>
            <ol className="list-decimal pl-5 text-sm text-muted-foreground">
              {answer.because.map((b, i) => (
                <li key={i}>{b}</li>
              ))}
            </ol>
            <Line label="Roles">{answer.roles.length ? answer.roles.map((r) => `${r.name} (${r.via})`).join(' · ') : 'none'}</Line>
          </output>
        )}
      </CardContent>
    </Card>
  )
}

function Line({ label, children }: { label: string; children: ReactNode }) {
  return (
    <p className="text-sm">
      <span className="text-muted-foreground">{label}: </span>
      {children}
    </p>
  )
}

function History() {
  const history = useQuery({
    queryKey: ['admin', 'access', 'history'],
    queryFn: ({ signal }) => api<{ id: number; at: string; kind: string; actor: string | null; before: string | null; after: string | null }[]>('/api/admin/access/history?take=200', { signal }),
  })
  if (history.error) return <QueryError error={history.error} retry={() => history.refetch()} />
  if (!history.data) return <Skeleton className="h-40" />
  if (history.data.length === 0) return <p className="text-sm text-muted-foreground">No changes yet.</p>
  return (
    <ul className="grid gap-2">
      {history.data.map((h) => (
        <li key={h.id} className="rounded-md border p-3 text-sm">
          <p className="flex flex-wrap items-center gap-2">
            <Badge variant={h.before && !h.after ? 'destructive' : h.after && !h.before ? 'success' : 'outline'}>
              {h.before && !h.after ? 'removed' : h.after && !h.before ? 'added' : 'changed'} {h.kind}
            </Badge>
            <span className="text-muted-foreground">
              {new Date(h.at).toLocaleString()} · {h.actor ?? 'the app'}
            </span>
          </p>
          <pre className="mt-2 overflow-x-auto font-mono text-xs whitespace-pre-wrap text-muted-foreground">{h.after ?? h.before}</pre>
        </li>
      ))}
    </ul>
  )
}

interface AccessRequestRow {
  id: string
  person: { id: string; name: string }
  role: string | null
  resourceKind: string | null
  resource: string | null
  reason: string
  status: 'pending' | 'approved' | 'denied'
  note: string | null
  createdAt: string
  decidedBy: string | null
}

/** People's requests for access: a yes makes it so for them (until a date, when given); a no says why. */
function Requests() {
  const queryClient = useQueryClient()
  const [status, setStatus] = useState<'pending' | 'all'>('pending')
  const [notes, setNotes] = useState<Record<string, string>>({})
  const requests = useQuery({
    queryKey: ['admin', 'access', 'requests', status],
    queryFn: ({ signal }) => api<AccessRequestRow[]>(`/api/admin/access/requests?status=${status}`, { signal }),
  })
  const decide = async (id: string, approve: boolean) => {
    try {
      await api(`/api/admin/access/requests/${id}/decide`, { body: { approve, note: notes[id] || null } })
      toast.success(approve ? 'Given' : 'Declined')
      await queryClient.invalidateQueries({ queryKey: ['admin', 'access'] })
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }
  return (
    <div className="grid gap-4">
      <Segmented
        label="Requests"
        value={status}
        onChange={setStatus}
        options={[
          { value: 'pending', label: 'Waiting' },
          { value: 'all', label: 'All' },
        ]}
      />
      {requests.error ? (
        <QueryError error={requests.error} retry={() => requests.refetch()} />
      ) : !requests.data ? (
        <Skeleton className="h-24" />
      ) : requests.data.length === 0 ? (
        <p className="text-sm text-muted-foreground">{status === 'pending' ? 'Nobody waits for an answer.' : 'No requests yet.'}</p>
      ) : (
        <ul className="grid gap-3" aria-label="Access requests">
          {requests.data.map((r) => (
            <li key={r.id} className="grid gap-2 rounded-lg border p-4 text-sm">
              <p className="flex flex-wrap items-center gap-2">
                <span className="font-medium">{r.person.name}</span> asks for
                <Badge variant="outline">{r.role ? `role ${r.role}` : `${r.resourceKind?.replace('_', ' ')} ${r.resource}`}</Badge>
                <span className="text-xs text-muted-foreground">{new Date(r.createdAt).toLocaleString()}</span>
                {r.status !== 'pending' && (
                  <Badge variant={r.status === 'approved' ? 'success' : 'destructive'}>
                    {r.status === 'approved' ? 'Given' : 'Declined'}
                    {r.decidedBy ? ` by ${r.decidedBy}` : ''}
                  </Badge>
                )}
              </p>
              <p className="text-muted-foreground">{r.reason}</p>
              {r.status === 'pending' ? (
                <div className="flex flex-wrap items-center gap-2">
                  <Input value={notes[r.id] ?? ''} onChange={(e) => setNotes({ ...notes, [r.id]: e.target.value })} placeholder="A word to them (optional)" aria-label={`A word to ${r.person.name}`} className="h-8 max-w-sm" />
                  <Button size="sm" onClick={() => void decide(r.id, true)}>
                    <Check /> Give it
                  </Button>
                  <Button size="sm" variant="outline" onClick={() => void decide(r.id, false)}>
                    <X /> Decline
                  </Button>
                </div>
              ) : (
                r.note && <p className="text-xs text-muted-foreground">“{r.note}”</p>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
