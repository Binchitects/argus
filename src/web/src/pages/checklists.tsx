import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ListChecks, Pencil, Plus, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { checklistsQuery, type Checklist } from './checklists-api'

/** Workspace → Checklists: your own lists of what is done before work counts as done, turned on in a chat or a Code Arena session. */
export function ChecklistsPage() {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const lists = useQuery(checklistsQuery)
  const [editing, setEditing] = useState<Checklist | 'new' | null>(null)
  const remove = useMutation({
    mutationFn: (id: string) => api(`/api/checklists/${id}`, { method: 'DELETE' }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['checklists'] }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <>
      <PageHeader
        title="Checklists"
        description="Lists of what must be done before work counts as done (tests run, docs updated, nothing left behind). Turn one on in a chat, or in Code Arena with /checklist: the model goes through it and ticks each item, saying how."
        actions={
          <Button onClick={() => setEditing('new')}>
            <Plus /> New checklist
          </Button>
        }
      />
      {lists.error && <QueryError error={lists.error} retry={() => lists.refetch()} />}
      {lists.isPending && <Skeleton className="h-40" />}
      {lists.data?.length === 0 && (
        <EmptyState icon={ListChecks} title="No checklists yet" action={<Button onClick={() => setEditing('new')}>New checklist</Button>}>
          Write the steps you want every piece of work to go through.
        </EmptyState>
      )}
      <div className="grid gap-4 lg:grid-cols-2">
        {lists.data?.map((c) => (
          <Card key={c.id}>
            <CardHeader className="flex flex-row items-start gap-2">
              <div className="min-w-0 flex-1">
                <CardTitle className="text-base">{c.name}</CardTitle>
                {c.description && <CardDescription>{c.description}</CardDescription>}
              </div>
              <Button size="icon-sm" variant="ghost" aria-label={`Change ${c.name}`} onClick={() => setEditing(c)}>
                <Pencil />
              </Button>
              <Button
                size="icon-sm"
                variant="ghost"
                className="text-destructive-ink"
                aria-label={`Delete ${c.name}`}
                onClick={async () => {
                  if (await confirm({ title: `Delete ${c.name}?`, description: 'Chats it is on in keep their copy of it.', confirm: 'Delete', destructive: true })) remove.mutate(c.id)
                }}
              >
                <Trash2 />
              </Button>
            </CardHeader>
            <CardContent>
              <ol className="list-decimal space-y-1 pl-5 text-sm">
                {c.items.map((item, n) => (
                  <li key={n}>{item}</li>
                ))}
              </ol>
            </CardContent>
          </Card>
        ))}
      </div>
      <Dialog open={editing !== null} onOpenChange={(o) => !o && setEditing(null)}>
        <DialogContent className="sm:max-w-lg">{editing !== null && <ChecklistForm list={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}</DialogContent>
      </Dialog>
    </>
  )
}

function ChecklistForm({ list, onClose }: { list: Checklist | null; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [name, setName] = useState(list?.name ?? '')
  const [description, setDescription] = useState(list?.description ?? '')
  const [items, setItems] = useState(list?.items.join('\n') ?? '')
  const [error, setError] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: () => {
      const body = { name: name.trim(), description: description.trim(), items: items.split('\n').map((i) => i.trim()).filter(Boolean) }
      return list ? api<Checklist>(`/api/checklists/${list.id}`, { method: 'PUT', body }) : api<Checklist>('/api/checklists', { body })
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['checklists'] })
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  return (
    <form
      className="grid gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        setError(null)
        save.mutate()
      }}
    >
      <DialogHeader>
        <DialogTitle>{list ? `Change ${list.name}` : 'New checklist'}</DialogTitle>
        <DialogDescription>One item a line, in the order they are gone through.</DialogDescription>
      </DialogHeader>
      {error && <Alert variant="destructive">{error}</Alert>}
      <Field label="Name">
        <Input value={name} maxLength={100} onChange={(e) => setName(e.target.value)} placeholder="Done means done" />
      </Field>
      <Field label="What it is for (optional)">
        <Input value={description} maxLength={500} onChange={(e) => setDescription(e.target.value)} />
      </Field>
      <Field label="Items" hint="Up to 50, each up to 500 characters.">
        <Textarea rows={8} value={items} onChange={(e) => setItems(e.target.value)} placeholder={'The tests pass\nThe docs say what changed\nNo debugging left in the code'} />
      </Field>
      <DialogFooter>
        <Button type="button" variant="outline" onClick={onClose}>
          Cancel
        </Button>
        <Button type="submit" loading={save.isPending} disabled={!name.trim() || !items.trim()}>
          Save
        </Button>
      </DialogFooter>
    </form>
  )
}
