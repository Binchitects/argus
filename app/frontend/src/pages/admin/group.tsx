import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Pencil, Trash2, UserMinus, UserPlus } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { ScrollRegion } from '@/components/app/scroll-region'
import { Alert } from '@/components/ui/alert'
import { Avatar } from '@/components/ui/avatar'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { DataTable, SortHeader, type ColumnDef } from '@/components/ui/data-table'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { groupQuery, type GroupDetail, type GroupMember } from './groups-api'
import { peopleQuery } from './people-api'

export function GroupPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const group = useQuery(groupQuery(id))
  const [editing, setEditing] = useState(false)
  const [adding, setAdding] = useState(false)
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['admin', 'groups'] })
  const remove = useMutation({
    mutationFn: (userId: string) => api(`/api/admin/groups/${id}/members/${userId}`, { method: 'DELETE' }),
    onSuccess: refresh,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const del = useMutation({
    mutationFn: () => api(`/api/admin/groups/${id}`, { method: 'DELETE' }),
    onSuccess: async () => {
      await refresh()
      navigate('/admin/groups')
    },
    onError: (e) => toast.error(errorMessage(e)),
  })

  if (group.isPending) return <PageSkeleton />
  if (group.error) return <QueryError error={group.error} retry={() => group.refetch()} />
  const g = group.data
  const app = g.directory === null

  const columns: ColumnDef<GroupMember>[] = [
    {
      id: 'person',
      accessorFn: (m) => `${m.displayName} ${m.userName} ${m.email}`,
      header: ({ column }) => <SortHeader column={column} title="Person" />,
      sortingFn: (a, b) => a.original.displayName.localeCompare(b.original.displayName),
      cell: ({ row: { original: m } }) => (
        <Link to={`/admin/people/${m.id}`} className="flex min-w-48 items-center gap-2.5 outline-none hover:underline focus-visible:ring-[3px] focus-visible:ring-ring">
          <Avatar name={m.displayName || m.userName} className="size-8" />
          <span className="min-w-0">
            <span className="block truncate font-medium">{m.displayName}</span>
            <span className="block truncate text-xs text-muted-foreground">
              {m.userName} · {m.email}
            </span>
          </span>
        </Link>
      ),
    },
    { id: 'status', header: 'Status', accessorFn: (m) => (m.isDisabled ? 'Disabled' : 'Active'), cell: ({ row: { original: m } }) => (m.isDisabled ? <Badge variant="warning">Disabled</Badge> : null) },
    ...(app
      ? [
          {
            id: 'remove',
            header: () => <span className="sr-only">Remove</span>,
            cell: ({ row: { original: m } }) => (
              <Button variant="ghost" size="sm" onClick={() => remove.mutate(m.id)} aria-label={`Remove ${m.displayName} from ${g.name}`}>
                <UserMinus /> Remove
              </Button>
            ),
          } satisfies ColumnDef<GroupMember>,
        ]
      : []),
  ]

  return (
    <>
      <Button variant="ghost" size="sm" asChild className="mb-4 -ml-2 text-muted-foreground">
        <Link to="/admin/groups">
          <ArrowLeft /> Groups
        </Link>
      </Button>
      <div className="mb-6 flex flex-wrap items-start gap-4">
        <div className="min-w-0 flex-1">
          <h1 className="text-2xl font-semibold tracking-tight">{g.name}</h1>
          {g.description && <p className="mt-1 text-muted-foreground">{g.description}</p>}
          <div className="mt-2 flex flex-wrap items-center gap-1.5">
            {app ? <Badge variant="secondary">App group</Badge> : <Badge variant="outline">Directory group</Badge>}
            {!app && <span className="font-mono text-xs text-muted-foreground">{g.directory}</span>}
          </div>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button variant="outline" onClick={() => setEditing(true)}>
            <Pencil /> Edit
          </Button>
          <Button
            variant="outline"
            className="text-destructive-ink"
            onClick={async () => {
              if (await confirm({ title: `Delete ${g.name}?`, description: 'Tools and models given to this group stop being available to its members (admins keep them).', confirm: 'Delete', destructive: true }))
                del.mutate()
            }}
          >
            <Trash2 /> Delete
          </Button>
        </div>
      </div>
      <Card>
        <CardHeader className="flex flex-row flex-wrap items-start justify-between gap-2">
          <div>
            <CardTitle>Members ({g.members.length})</CardTitle>
            <CardDescription>
              {app ? 'The people you added.' : 'Whoever the directory puts in this group, as of their last sign-in or directory check.'}
            </CardDescription>
          </div>
          {app && (
            <Button onClick={() => setAdding(true)}>
              <UserPlus /> Add people
            </Button>
          )}
        </CardHeader>
        <CardContent>
          <DataTable columns={columns} data={g.members} noun="members" getRowId={(m) => m.id} initialSorting={[{ id: 'person', desc: false }]} empty={app ? 'Nobody yet. Add people to give them what this group may use.' : 'Nobody here is in this directory group yet.'} />
        </CardContent>
      </Card>
      <EditDialog group={g} open={editing} onOpenChange={setEditing} />
      {app && <AddPeopleDialog group={g} open={adding} onOpenChange={setAdding} />}
    </>
  )
}

function EditDialog({ group, open, onOpenChange }: { group: GroupDetail; open: boolean; onOpenChange: (o: boolean) => void }) {
  const queryClient = useQueryClient()
  const [name, setName] = useState(group.name)
  const [description, setDescription] = useState(group.description ?? '')
  const [directory, setDirectory] = useState(group.directory ?? '')
  const [error, setError] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: () => api(`/api/admin/groups/${group.id}`, { method: 'PATCH', body: { name, description, ...(group.directory !== null ? { directory } : {}) } }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['admin', 'groups'] })
      onOpenChange(false)
    },
    onError: (e) => setError(errorMessage(e)),
  })
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Edit {group.name}</DialogTitle>
          <DialogDescription>{group.directory !== null ? 'A directory group stays one; its members follow the directory.' : 'Members are kept.'}</DialogDescription>
        </DialogHeader>
        <form
          className="grid gap-4"
          onSubmit={(e) => {
            e.preventDefault()
            setError(null)
            save.mutate()
          }}
        >
          {error && <Alert variant="destructive">{error}</Alert>}
          <Field label="Name">
            <Input value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} />
          </Field>
          {group.directory !== null && (
            <Field label="Directory group" hint="Its name (cn) or full DN.">
              <Input value={directory} onChange={(e) => setDirectory(e.target.value)} required maxLength={1000} />
            </Field>
          )}
          <Field label="Description">
            <Textarea rows={2} value={description} onChange={(e) => setDescription(e.target.value)} maxLength={500} />
          </Field>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" loading={save.isPending}>
              Save
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

function AddPeopleDialog({ group, open, onOpenChange }: { group: GroupDetail; open: boolean; onOpenChange: (o: boolean) => void }) {
  const queryClient = useQueryClient()
  const people = useQuery({ ...peopleQuery, enabled: open })
  const [search, setSearch] = useState('')
  const [chosen, setChosen] = useState<Set<string>>(new Set())
  const inGroup = new Set(group.members.map((m) => m.id))
  const q = search.trim().toLowerCase()
  const candidates = (people.data?.people ?? []).filter((p) => !inGroup.has(p.id) && (!q || `${p.displayName} ${p.userName} ${p.email}`.toLowerCase().includes(q)))
  const add = useMutation({
    mutationFn: () => api(`/api/admin/groups/${group.id}/members`, { body: { userIds: [...chosen] } }),
    onSuccess: async () => {
      toast.success(`Added ${chosen.size} to ${group.name}.`)
      await queryClient.invalidateQueries({ queryKey: ['admin', 'groups'] })
      setChosen(new Set())
      setSearch('')
      onOpenChange(false)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const toggle = (id: string, on: boolean) => setChosen((s) => (on ? new Set(s).add(id) : new Set([...s].filter((x) => x !== id))))
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Add people to {group.name}</DialogTitle>
          <DialogDescription>They can use what this group may use at once.</DialogDescription>
        </DialogHeader>
        <Input type="search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search people" aria-label="Search people" />
        <ScrollRegion label="People to add" className="max-h-72 rounded-md border">
          {people.isPending ? (
            <p className="p-3 text-sm text-muted-foreground">Loading…</p>
          ) : candidates.length === 0 ? (
            <p className="p-3 text-sm text-muted-foreground">{q ? 'Nobody matches.' : 'Everyone is already in this group.'}</p>
          ) : (
            <ul className="divide-y">
              {candidates.map((p) => (
                <li key={p.id} className="flex items-center gap-3 px-3 py-2 hover:bg-accent">
                  <Checkbox id={`add-${p.id}`} checked={chosen.has(p.id)} onCheckedChange={(v) => toggle(p.id, v === true)} />
                  <label htmlFor={`add-${p.id}`} className="flex min-w-0 flex-1 cursor-pointer items-center gap-3">
                    <Avatar name={p.displayName || p.userName} className="size-7" />
                    <span className="min-w-0">
                      <span className="block truncate text-sm font-medium">{p.displayName}</span>
                      <span className="block truncate text-xs text-muted-foreground">
                        {p.userName} · {p.email}
                      </span>
                    </span>
                  </label>
                </li>
              ))}
            </ul>
          )}
        </ScrollRegion>
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button onClick={() => add.mutate()} disabled={chosen.size === 0} loading={add.isPending}>
            {chosen.size ? `Add ${chosen.size}` : 'Add'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
