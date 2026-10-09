import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Gauge, UserPlus } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'
import { QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { DataTable, SortHeader, type ColumnDef } from '@/components/ui/data-table'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { peopleQuery, type Person } from './people-api'
import { parseRoom, plural, roomOf, size, type PersonFiles } from './storage-api'

/** Each person with files or a room of their own: what their files take of their room, and theirs to set; anyone else's too. */
export function StoragePeople({ personMegabytes }: { personMegabytes: number | null }) {
  const [editing, setEditing] = useState<PersonFiles | null>(null)
  const [choosing, setChoosing] = useState(false)
  const people = useQuery({
    queryKey: ['admin', 'storage', 'people'],
    queryFn: ({ signal }) => api<{ people: PersonFiles[]; personMegabytes: number | null }>('/api/admin/storage/people', { signal }),
  })
  const company = people.data?.personMegabytes ?? personMegabytes
  const columns: ColumnDef<PersonFiles>[] = [
    {
      id: 'person',
      accessorFn: (p) => `${p.displayName} ${p.userName} ${p.email ?? ''}`,
      header: ({ column }) => <SortHeader column={column} title="Person" />,
      sortingFn: (a, b) => (a.original.displayName || a.original.userName).localeCompare(b.original.displayName || b.original.userName),
      cell: ({ row: { original: p } }) => (
        <div className="min-w-40">
          <Link to={`/admin/people/${p.id}`} className="font-medium underline-offset-2 hover:underline">
            {p.displayName || p.userName}
          </Link>
          <p className="text-xs text-muted-foreground">{p.email}</p>
        </div>
      ),
    },
    { id: 'count', accessorFn: (p) => p.count, header: ({ column }) => <SortHeader column={column} title="Files" />, cell: ({ getValue }) => <span className="tabular-nums">{getValue<number>().toLocaleString('en-US')}</span> },
    {
      id: 'bytes',
      accessorFn: (p) => p.bytes,
      header: ({ column }) => <SortHeader column={column} title="Take" />,
      cell: ({ row: { original: p } }) => {
        const room = roomOf(p, company)
        const share = room ? (100 * p.bytes) / room : null
        return (
          <div className="grid min-w-36 gap-1">
            <span className="tabular-nums">
              {size(p.bytes)}
              {room !== null && <span className="text-muted-foreground"> of {size(room)}</span>}
            </span>
            {share !== null && (
              <div className="h-1.5 w-full overflow-hidden rounded-full bg-muted" aria-hidden="true">
                <div className={cn('h-full rounded-full', share >= 100 ? 'bg-destructive' : share >= 80 ? 'bg-warning' : 'bg-primary')} style={{ width: `${Math.min(100, share)}%` }} />
              </div>
            )}
          </div>
        )
      },
    },
    {
      id: 'room',
      accessorFn: (p) => p.ownMegabytes ?? -1,
      header: 'Room',
      cell: ({ row: { original: p } }) => (
        <span className="inline-flex flex-wrap items-center gap-1.5 whitespace-nowrap">
          {p.ownMegabytes === null ? <span className="text-muted-foreground">The company's</span> : p.ownMegabytes === 0 ? 'No limit' : `${p.ownMegabytes.toLocaleString('en-US')} MB`}
          {p.held && <Badge variant="warning">legal hold</Badge>}
        </span>
      ),
    },
    {
      id: 'set',
      enableSorting: false,
      enableHiding: false,
      header: () => <span className="sr-only">Set the room</span>,
      cell: ({ row: { original: p } }) => (
        <Button variant="outline" size="sm" onClick={() => setEditing(p)} aria-label={`Set the room of ${p.displayName || p.userName}`}>
          <Gauge /> Room
        </Button>
      ),
    },
  ]
  // Someone not listed has no files and the company's room.
  const choose = (p: Person) => {
    setChoosing(false)
    setEditing(
      people.data?.people.find((x) => x.id === p.id) ?? {
        id: p.id, userName: p.userName, displayName: p.displayName, email: p.email, count: 0, bytes: 0, ownMegabytes: null, held: !!p.legalHoldSince,
      },
    )
  }
  return (
    <div className="grid gap-4">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <p className="max-w-3xl text-sm text-muted-foreground">
          {company ? `Each person's files may take ${size(company * 1024 * 1024)}` : "Each person's files may take any room"} (Settings → Storage), unless they have their own here. Past it,
          uploads and the pictures and videos the tools make are refused until they delete chats.
        </p>
        <Button variant="outline" size="sm" onClick={() => setChoosing(true)}>
          <UserPlus /> Give someone a room
        </Button>
      </div>
      {people.error ? (
        <QueryError error={people.error} retry={() => people.refetch()} />
      ) : (
        <DataTable columns={columns} data={people.data?.people} loading={people.isPending} noun="people" getRowId={(p) => p.id} initialSorting={[{ id: 'bytes', desc: true }]} empty="Nobody has files yet." />
      )}
      <RoomDialog person={editing} company={company} onClose={() => setEditing(null)} />
      <ChoosePerson open={choosing} onClose={() => setChoosing(false)} onChoose={choose} />
    </div>
  )
}

/** Anyone, found by name, to give a room of their own before they have files (the list shows only those who have). */
function ChoosePerson({ open, onClose, onChoose }: { open: boolean; onClose: () => void; onChoose: (p: Person) => void }) {
  const [q, setQ] = useState('')
  const people = useQuery({ ...peopleQuery, enabled: open })
  const needle = q.trim().toLowerCase()
  const found = needle
    ? (people.data?.people ?? []).filter((p) => [p.displayName, p.userName, p.email].some((s) => s?.toLowerCase().includes(needle))).slice(0, 20)
    : []
  const close = () => {
    setQ('')
    onClose()
  }
  return (
    <Dialog open={open} onOpenChange={(o) => !o && close()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Give someone a room</DialogTitle>
          <DialogDescription>Their own room for files, before they have any: less than the company's, more, or no limit.</DialogDescription>
        </DialogHeader>
        {people.error ? (
          <QueryError error={people.error} retry={() => people.refetch()} />
        ) : (
          <div className="grid gap-2">
            <Input type="search" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Name, user name or email" aria-label="Find a person" autoComplete="off" autoFocus />
            {needle && (
              <ul className="grid max-h-64 gap-0.5 overflow-y-auto" aria-label="People found">
                {people.isPending && <li className="px-2 py-1 text-sm text-muted-foreground">Loading people…</li>}
                {people.isSuccess && found.length === 0 && <li className="px-2 py-1 text-sm text-muted-foreground">Nobody by that name.</li>}
                {found.map((p) => (
                  <li key={p.id}>
                    <button
                      type="button"
                      onClick={() => {
                        setQ('')
                        onChoose(p)
                      }}
                      className="flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm outline-none hover:bg-accent focus-visible:bg-accent"
                    >
                      <span className="truncate font-medium">{p.displayName || p.userName}</span>
                      <span className="truncate text-xs text-muted-foreground">{p.email || p.userName}</span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}
      </DialogContent>
    </Dialog>
  )
}

function RoomDialog({ person, company, onClose }: { person: PersonFiles | null; company: number | null; onClose: () => void }) {
  const client = useQueryClient()
  const [value, setValue] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const shown = value ?? (person?.ownMegabytes !== null && person?.ownMegabytes !== undefined ? String(person.ownMegabytes) : '')
  const close = () => {
    setValue(null)
    setError(null)
    onClose()
  }
  const save = useMutation({
    mutationFn: (megabytes: number | null) => api(`/api/admin/storage/people/${person!.id}/quota`, { method: 'PUT', body: { megabytes } }),
    onSuccess: (_, megabytes) => {
      toast.success(megabytes === null ? "Back to the company's room" : megabytes === 0 ? 'No limit for them' : `Their room: ${megabytes.toLocaleString('en-US')} MB`)
      void client.invalidateQueries({ queryKey: ['admin', 'storage'] })
      close()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  return (
    <Dialog open={!!person} onOpenChange={(o) => !o && close()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Room for {person?.displayName || person?.userName}'s files</DialogTitle>
          <DialogDescription>
            In megabytes. Empty: the company's ({company ? `${company.toLocaleString('en-US')} MB` : 'no limit'}); 0: no limit. Their files take {size(person?.bytes)} now ({plural(person?.count ?? 0, 'file')}).
          </DialogDescription>
        </DialogHeader>
        <form
          className="grid gap-4"
          onSubmit={(e) => {
            e.preventDefault()
            const mb = parseRoom(shown)
            if (mb === undefined) setError('A whole number of megabytes, 0 for no limit, or empty for the company\'s.')
            else save.mutate(mb)
          }}
        >
          {error && <Alert variant="destructive">{error}</Alert>}
          <Field label="Room (MB)">
            <Input inputMode="numeric" value={shown} onChange={(e) => setValue(e.target.value)} placeholder="the company's" autoFocus />
          </Field>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={close}>
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
