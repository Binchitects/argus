import { zodResolver } from '@hookform/resolvers/zod'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { FolderTree, UsersRound } from 'lucide-react'
import { useId, useState } from 'react'
import { useForm } from 'react-hook-form'
import { useNavigate } from 'react-router'
import { z } from 'zod'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { DataTable, SortHeader, type ColumnDef } from '@/components/ui/data-table'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { api, errorMessage } from '@/lib/api'
import { directoryGroupsQuery, groupsQuery, type GroupSummary } from './groups-api'

const columns: ColumnDef<GroupSummary>[] = [
  {
    id: 'name',
    accessorFn: (g) => `${g.name} ${g.description ?? ''} ${g.directory ?? ''}`,
    header: ({ column }) => <SortHeader column={column} title="Group" />,
    sortingFn: (a, b) => a.original.name.localeCompare(b.original.name),
    cell: ({ row: { original: g } }) => (
      <div className="min-w-48">
        <p className="font-medium">{g.name}</p>
        {g.description && <p className="truncate text-xs text-muted-foreground">{g.description}</p>}
      </div>
    ),
  },
  {
    id: 'kind',
    header: 'Kind',
    accessorFn: (g) => (g.directory ? 'Directory' : 'App'),
    cell: ({ row: { original: g } }) =>
      g.directory ? (
        <span className="flex flex-wrap items-center gap-1.5">
          <Badge variant="outline">Directory</Badge>
          <span className="font-mono text-xs text-muted-foreground">{g.directory}</span>
        </span>
      ) : (
        <Badge variant="secondary">App</Badge>
      ),
  },
  {
    id: 'members',
    header: ({ column }) => <SortHeader column={column} title="Members" />,
    accessorFn: (g) => g.members,
    cell: ({ row: { original: g } }) => <span className="tabular-nums">{g.members}</span>,
  },
]

export function GroupsPage() {
  const navigate = useNavigate()
  const groups = useQuery(groupsQuery)
  const [adding, setAdding] = useState(false)
  return (
    <>
      <PageHeader
        title="Groups"
        description="Who may use which tools and models. An app group has the people you add; a directory group follows the company directory."
        actions={
          <Button onClick={() => setAdding(true)}>
            <UsersRound /> New group
          </Button>
        }
      />
      {groups.error ? (
        <QueryError error={groups.error} retry={() => groups.refetch()} />
      ) : (
        <DataTable
          columns={columns}
          data={groups.isPending ? undefined : groups.data}
          loading={groups.isPending}
          noun="groups"
          getRowId={(g) => g.id}
          onRowClick={(g) => navigate(`/admin/groups/${g.id}`)}
          initialSorting={[{ id: 'name', desc: false }]}
          empty={
            <span className="flex flex-col items-center gap-2 py-6 text-muted-foreground">
              <FolderTree className="size-6" aria-hidden="true" />
              No groups yet. Make one to give a tool or a model to some people only.
            </span>
          }
        />
      )}
      <NewGroupDialog open={adding} onOpenChange={setAdding} onMade={(id) => navigate(`/admin/groups/${id}`)} />
    </>
  )
}

const schema = z
  .object({ kind: z.enum(['app', 'directory']), name: z.string().trim().min(1, 'Give it a name.').max(100), description: z.string().max(500), directory: z.string().max(1000) })
  .refine((v) => v.kind === 'app' || v.directory.trim().length > 0, { path: ['directory'], message: 'Which directory group? Its name or full DN.' })

function NewGroupDialog({ open, onOpenChange, onMade }: { open: boolean; onOpenChange: (o: boolean) => void; onMade: (id: string) => void }) {
  const queryClient = useQueryClient()
  const listId = useId()
  const [error, setError] = useState<string | null>(null)
  const form = useForm({ resolver: zodResolver(schema), defaultValues: { kind: 'app' as 'app' | 'directory', name: '', description: '', directory: '' } })
  const kind = form.watch('kind')
  const seen = useQuery({ ...directoryGroupsQuery, enabled: open && kind === 'directory' })
  const close = () => {
    onOpenChange(false)
    setTimeout(() => {
      setError(null)
      form.reset()
    }, 200)
  }
  const submit = form.handleSubmit(async (v) => {
    setError(null)
    try {
      const made = await api<{ id: string }>('/api/admin/groups', {
        body: { name: v.name, description: v.description || null, directory: v.kind === 'directory' ? v.directory : null },
      })
      await queryClient.invalidateQueries({ queryKey: ['admin', 'groups'] })
      close()
      onMade(made.id)
    } catch (e) {
      setError(errorMessage(e))
    }
  })
  const { errors, isSubmitting } = form.formState
  return (
    <Dialog open={open} onOpenChange={(o) => (o ? onOpenChange(true) : close())}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>New group</DialogTitle>
          <DialogDescription>Tools and models can then be given to its members only.</DialogDescription>
        </DialogHeader>
        <form onSubmit={submit} noValidate className="grid gap-4">
          {error && <Alert variant="destructive">{error}</Alert>}
          <Segmented
            label="Kind"
            value={kind}
            onChange={(k) => form.setValue('kind', k)}
            options={[
              { value: 'app', label: 'App group' },
              { value: 'directory', label: 'Directory group' },
            ]}
          />
          <Field label="Name" error={errors.name?.message}>
            <Input autoComplete="off" autoFocus {...form.register('name')} />
          </Field>
          {kind === 'directory' && (
            <Field
              label="Directory group"
              hint={seen.data?.length ? 'Its name (cn) or full DN. Suggestions are the groups people here are in.' : 'Its name (cn) or full DN. Members follow the directory at each sign-in and check.'}
              error={errors.directory?.message}
            >
              <Input autoComplete="off" list={listId} placeholder="e.g. data-science" {...form.register('directory')} />
            </Field>
          )}
          <datalist id={listId}>
            {seen.data?.map((g) => (
              <option key={g.dn} value={g.name}>
                {g.people} {g.people === 1 ? 'person' : 'people'} · {g.dn}
              </option>
            ))}
          </datalist>
          <Field label="Description" error={errors.description?.message}>
            <Textarea rows={2} {...form.register('description')} />
          </Field>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={close}>
              Cancel
            </Button>
            <Button type="submit" loading={isSubmitting}>
              Make group
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
