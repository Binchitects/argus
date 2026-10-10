import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Building2, Download, LogOut, UserCheck, UserPlus, UserX, Wallet } from 'lucide-react'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { useNavigate } from 'react-router'
import { z } from 'zod'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Secret } from '@/components/app/secret'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Avatar } from '@/components/ui/avatar'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { useConfirm } from '@/components/ui/confirm'
import { DataTable, SortHeader, selectColumn, type ColumnDef } from '@/components/ui/data-table'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { ago, money } from '@/lib/format'
import { DirectoryMoveDialog } from './directory-move'
import { PersonBadges } from './person-badges'
import { creditKinds, parseCredit, peopleQuery, signInQuery, type Created, type CreditKind, type Person } from './people-api'

const columns: ColumnDef<Person>[] = [
  selectColumn<Person>(),
  {
    id: 'person',
    accessorFn: (p) => `${p.displayName} ${p.userName} ${p.email}`,
    header: ({ column }) => <SortHeader column={column} title="Person" />,
    sortingFn: (a, b) => a.original.displayName.localeCompare(b.original.displayName),
    cell: ({ row: { original: p } }) => (
      <div className="flex min-w-48 items-center gap-2.5">
        <Avatar name={p.displayName || p.userName} className="size-8" />
        <div className="min-w-0">
          <p className="truncate font-medium">{p.displayName}</p>
          <p className="truncate text-xs text-muted-foreground">
            {p.userName} · {p.email}
          </p>
        </div>
      </div>
    ),
  },
  {
    id: 'role',
    header: 'Role',
    accessorFn: (p) => (p.isAdmin ? 'Admin' : 'Member'),
    cell: ({ row: { original: p } }) => (
      <div className="flex flex-wrap gap-1">
        <Badge variant={p.isAdmin ? 'default' : 'secondary'}>{p.isAdmin ? 'Admin' : 'Member'}</Badge>
        {p.source === 'ldap' && <Badge variant="outline">Directory</Badge>}
        {p.source === 'oidc' && <Badge variant="outline">Company</Badge>}
      </div>
    ),
  },
  { id: 'status', header: 'Status', accessorFn: (p) => (p.disabled ? 'disabled' : 'active'), cell: ({ row: { original: p } }) => <PersonBadges p={p} /> },
  {
    id: 'credit',
    header: ({ column }) => <SortHeader column={column} title="Spent this month" />,
    accessorFn: (p) => p.spend ?? 0,
    cell: ({ row: { original: p } }) => (
      <span className="flex flex-wrap items-center gap-1.5">
        <span className="tabular-nums">{p.spend === null ? '—' : money(p.spend)}</span>
        {p.overCredit.map((k) => (
          <Badge key={k} variant="destructive" title={`Their ${creditKinds.find((c) => c.kind === k)?.label.toLowerCase()} credit is used up this month`}>
            {creditKinds.find((c) => c.kind === k)?.label} used up
          </Badge>
        ))}
        {p.apiOff && <Badge variant="outline">No API</Badge>}
      </span>
    ),
  },
  {
    id: 'lastSignIn',
    header: ({ column }) => <SortHeader column={column} title="Last sign-in" />,
    accessorFn: (p) => (p.lastSignInAt ? new Date(p.lastSignInAt).getTime() : 0),
    cell: ({ row: { original: p } }) => <span className="whitespace-nowrap text-muted-foreground">{p.lastSignInAt ? ago(p.lastSignInAt) : 'never'}</span>,
  },
]

type Filter = 'all' | 'admins' | 'disabled' | 'over' | 'local'

export function PeoplePage() {
  const navigate = useNavigate()
  const people = useQuery(peopleQuery)
  const [filter, setFilter] = useState<Filter>('all')
  const [adding, setAdding] = useState(false)
  const [creditFor, setCreditFor] = useState<Person[] | null>(null)
  const [moving, setMoving] = useState<Person[] | null>(null)
  const directory = useQuery(signInQuery).data?.ldap === true
  const bulk = useBulk()
  const all = people.data?.people ?? []
  const shown = all.filter((p) =>
    filter === 'admins'
      ? p.isAdmin
      : filter === 'disabled'
        ? p.disabled
        : filter === 'over'
          ? p.overCredit.length > 0
          : filter === 'local'
            ? p.source === 'local'
            : true,
  )
  return (
    <>
      <PageHeader
        title="People"
        description="Everyone who can sign in: their role, credit and sign-in security."
        actions={
          <>
            <Button variant="outline" asChild>
              <a href="/api/admin/people.csv" download>
                <Download /> Export CSV
              </a>
            </Button>
            <Button onClick={() => setAdding(true)}>
              <UserPlus /> Add person
            </Button>
          </>
        }
      />
      {people.data?.warning && (
        <Alert variant="warning" className="mb-4">
          {people.data.warning}
        </Alert>
      )}
      {people.error ? (
        <QueryError error={people.error} retry={() => people.refetch()} />
      ) : (
        <DataTable
          columns={columns}
          data={people.isPending ? undefined : shown}
          loading={people.isPending}
          noun="people"
          getRowId={(p) => p.id}
          onRowClick={(p) => navigate(`/admin/people/${p.id}`)}
          initialSorting={[{ id: 'person', desc: false }]}
          toolbar={
            <Segmented
              label="Show"
              value={filter}
              onChange={setFilter}
              options={[
                { value: 'all', label: `All (${all.length})` },
                { value: 'admins', label: 'Admins' },
                { value: 'disabled', label: 'Disabled' },
                { value: 'over', label: 'Over credit' },
                ...(directory ? [{ value: 'local' as const, label: 'Local accounts' }] : []),
              ]}
            />
          }
          bulk={(selected, table) => (
            <>
              <Button size="sm" variant="outline" onClick={() => setCreditFor(selected)}>
                <Wallet /> Set credit
              </Button>
              <Button size="sm" variant="outline" loading={bulk.isPending} onClick={() => bulk.run('disable', selected, () => table.resetRowSelection())}>
                <UserX /> Disable
              </Button>
              <Button size="sm" variant="outline" loading={bulk.isPending} onClick={() => bulk.run('enable', selected, () => table.resetRowSelection())}>
                <UserCheck /> Enable
              </Button>
              <Button size="sm" variant="outline" loading={bulk.isPending} onClick={() => bulk.run('sign-out', selected, () => table.resetRowSelection())}>
                <LogOut /> Sign out everywhere
              </Button>
              {directory && (
                <Button size="sm" variant="outline" onClick={() => setMoving(selected)}>
                  <Building2 /> Move to the directory
                </Button>
              )}
            </>
          )}
        />
      )}
      <AddPersonDialog open={adding} onOpenChange={setAdding} />
      <CreditDialog people={creditFor} onClose={() => setCreditFor(null)} />
      <DirectoryMoveDialog people={moving} onClose={() => setMoving(null)} />
    </>
  )
}

/** One action for several people, one request each; says how many worked. */
function useBulk() {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const [isPending, setPending] = useState(false)
  const run = async (action: 'disable' | 'enable' | 'sign-out', people: Person[], done: () => void) => {
    const words = { disable: 'Disable', enable: 'Enable', 'sign-out': 'Sign out' }[action]
    const ok = await confirm({
      title: `${words} ${people.length} ${people.length === 1 ? 'person' : 'people'}?`,
      description:
        action === 'disable'
          ? 'They are signed out and their API keys stop working until enabled again. You cannot disable yourself.'
          : action === 'sign-out'
            ? 'Every session they have ends, here and in every service that trusts this sign-in.'
            : 'They can sign in again, and their API keys work again.',
      confirm: words,
      destructive: action !== 'enable',
    })
    if (!ok) return
    setPending(true)
    const results = await Promise.allSettled(
      people.map((p) =>
        action === 'sign-out'
          ? api(`/api/admin/people/${p.id}/sign-out`, { body: {} })
          : api(`/api/admin/people/${p.id}`, { method: 'PATCH', body: { disabled: action === 'disable' } }),
      ),
    )
    setPending(false)
    const failed = results.filter((r) => r.status === 'rejected')
    if (failed.length === 0) toast.success(`${words}: done for ${people.length}.`)
    else toast.error(`${people.length - failed.length} done, ${failed.length} failed: ${errorMessage((failed[0] as PromiseRejectedResult).reason)}`)
    await queryClient.invalidateQueries({ queryKey: ['admin'] })
    done()
  }
  return { run, isPending }
}

function CreditDialog({ people, onClose }: { people: Person[] | null; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [kind, setKind] = useState<CreditKind>('chat')
  const [value, setValue] = useState('')
  const [error, setError] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: async (credit: number | null) => {
      const results = await Promise.allSettled((people ?? []).map((p) => api(`/api/admin/people/${p.id}/credits/${kind}`, { method: 'PUT', body: { credit } })))
      const failed = results.filter((r) => r.status === 'rejected')
      if (failed.length) throw (failed[0] as PromiseRejectedResult).reason
    },
    onSuccess: () => {
      toast.success('Credit set', { description: 'It holds at once; their other credits stay as they were.' })
      void queryClient.invalidateQueries({ queryKey: ['admin'] })
      setValue('')
      setKind('chat')
      setError(null)
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  return (
    <Dialog
      open={!!people}
      onOpenChange={(o) => {
        if (!o) {
          onClose()
          setValue('')
          setError(null)
        }
      }}
    >
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Set credit for {people?.length === 1 ? people[0]!.displayName : `${people?.length} people`}</DialogTitle>
          <DialogDescription>One kind at a time, in dollars a calendar month. Empty means no limit of that kind.</DialogDescription>
        </DialogHeader>
        <form
          className="grid gap-4"
          onSubmit={(e) => {
            e.preventDefault()
            const c = parseCredit(value)
            if (c === 'invalid') setError('A number of dollars, like 25, or empty for no limit.')
            else save.mutate(c)
          }}
        >
          {error && <Alert variant="destructive">{error}</Alert>}
          <Field label="For">
            <Select value={kind} onValueChange={(v) => setKind(v as CreditKind)}>
              <SelectTrigger aria-label="Credit for">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {creditKinds.map((k) => (
                  <SelectItem key={k.kind} value={k.kind}>
                    {k.label}: {k.hint}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
          <Field label="Credit ($)">
            <Input inputMode="decimal" value={value} onChange={(e) => setValue(e.target.value)} placeholder="no limit" autoFocus />
          </Field>
          <DialogFooter>
            <Button type="submit" loading={save.isPending}>
              Set credit
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

const addSchema = z.object({
  userName: z
    .string()
    .trim()
    .regex(/^[a-z0-9][a-z0-9._-]{1,63}$/, 'Lower-case letters, digits and . _ -, 2 to 64 characters.'),
  email: z.string().trim().email('An email address.'),
  displayName: z.string().trim(),
  chatCredit: z.string().refine((v) => parseCredit(v) !== 'invalid', 'A number of dollars, or empty for no limit.'),
  apiCredit: z.string().refine((v) => parseCredit(v) !== 'invalid', 'A number of dollars, or empty for no limit.'),
  admin: z.boolean(),
})

function AddPersonDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (o: boolean) => void }) {
  const queryClient = useQueryClient()
  const [created, setCreated] = useState<Created | null>(null)
  const [error, setError] = useState<string | null>(null)
  const form = useForm({ resolver: zodResolver(addSchema), defaultValues: { userName: '', email: '', displayName: '', chatCredit: '', apiCredit: '', admin: false } })
  const close = () => {
    onOpenChange(false)
    setTimeout(() => {
      setCreated(null)
      setError(null)
      form.reset()
    }, 200)
  }
  const submit = form.handleSubmit(async (v) => {
    setError(null)
    try {
      const credit = (t: string) => {
        const c = parseCredit(t)
        return c === 'invalid' ? null : c
      }
      const r = await api<Created>('/api/admin/people', {
        body: { userName: v.userName, email: v.email, displayName: v.displayName || null, admin: v.admin, credits: { chat: credit(v.chatCredit), api: credit(v.apiCredit) } },
      })
      setCreated(r)
      await queryClient.invalidateQueries({ queryKey: ['admin'] })
    } catch (e) {
      setError(errorMessage(e))
    }
  })
  const { errors, isSubmitting } = form.formState
  return (
    <Dialog open={open} onOpenChange={(o) => (o ? onOpenChange(true) : close())}>
      <DialogContent>
        {created ? (
          <>
            <DialogHeader>
              <DialogTitle>Person added</DialogTitle>
              <DialogDescription>Give them these now: they are shown only this once.</DialogDescription>
            </DialogHeader>
            <div className="grid gap-4">
              <Secret label="Password" value={created.password} />
              {created.apiKey && <Secret label="API key" value={created.apiKey} />}
              {created.warning && <Alert variant="warning">{created.warning}</Alert>}
            </div>
            <DialogFooter>
              <Button onClick={close}>Done</Button>
            </DialogFooter>
          </>
        ) : (
          <>
            <DialogHeader>
              <DialogTitle>Add a person</DialogTitle>
              <DialogDescription>A local account. People from the company directory appear by themselves when they first sign in.</DialogDescription>
            </DialogHeader>
            <form onSubmit={submit} noValidate className="grid gap-4">
              {error && <Alert variant="destructive">{error}</Alert>}
              <Field label="Username" hint="Use their GitLab username, so Argus knows what they may read." error={errors.userName?.message}>
                <Input autoComplete="off" autoFocus {...form.register('userName')} />
              </Field>
              <Field label="Email" error={errors.email?.message}>
                <Input type="email" autoComplete="off" {...form.register('email')} />
              </Field>
              <Field label="Name" error={errors.displayName?.message}>
                <Input autoComplete="off" {...form.register('displayName')} />
              </Field>
              <Field label="Chat credit ($ a month)" hint="Empty: no limit." error={errors.chatCredit?.message}>
                <Input inputMode="decimal" placeholder="no limit" {...form.register('chatCredit')} />
              </Field>
              <Field label="API credit ($ a month)" hint="Empty: no limit. Pictures, video and speech: on their page." error={errors.apiCredit?.message}>
                <Input inputMode="decimal" placeholder="no limit" {...form.register('apiCredit')} />
              </Field>
              <Label className="font-normal">
                <Switch checked={form.watch('admin')} onCheckedChange={(v) => form.setValue('admin', v)} /> Admin
              </Label>
              <DialogFooter>
                <Button type="button" variant="outline" onClick={close}>
                  Cancel
                </Button>
                <Button type="submit" loading={isSubmitting}>
                  Add person
                </Button>
              </DialogFooter>
            </form>
          </>
        )}
      </DialogContent>
    </Dialog>
  )
}
