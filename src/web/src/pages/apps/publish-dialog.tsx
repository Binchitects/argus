import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Copy } from 'lucide-react'
import { useId, useState } from 'react'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { publishApp, reachLabels, type AppReach } from './apps-api'

interface MyGroup {
  id: string
  name: string
}

/** What a preview needs to be published: its code and kind, a name to start from, the chat it was built in. */
export interface ToPublish {
  code: string
  kind: 'html' | 'react'
  name: string
  conversationId?: string
}

/** Publish as app: a name, a line about it, who may open it; then its link, to share. */
export function PublishDialog({ what, onClose }: { what: ToPublish | null; onClose: () => void }) {
  return (
    <Dialog open={what !== null} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-lg">{what && <PublishForm key={what.code.length + what.name} what={what} onClose={onClose} />}</DialogContent>
    </Dialog>
  )
}

function PublishForm({ what, onClose }: { what: ToPublish; onClose: () => void }) {
  const queryClient = useQueryClient()
  const id = useId()
  const [name, setName] = useState(what.name.replace(/\.(html?|jsx|tsx)$/i, '').replace(/[-_]+/g, ' ').trim() || 'My app')
  const [description, setDescription] = useState('')
  const [reach, setReach] = useState<AppReach>('company')
  const [groupIds, setGroupIds] = useState<string[]>([])
  const [error, setError] = useState<string | null>(null)
  const [link, setLink] = useState<string | null>(null)
  const groups = useQuery({ queryKey: ['sharing', 'groups'], queryFn: ({ signal }) => api<MyGroup[]>('/api/sharing/groups', { signal }), enabled: reach === 'groups' })
  const publish = useMutation({
    mutationFn: () => publishApp({ name: name.trim(), description: description.trim(), kind: what.kind, code: what.code, reach, groupIds: reach === 'groups' ? groupIds : [], conversationId: what.conversationId }),
    onSuccess: (app) => {
      setLink(`${window.location.origin}/apps/${app.id}`)
      void queryClient.invalidateQueries({ queryKey: ['apps'] })
    },
    onError: (e) => setError(errorMessage(e)),
  })
  if (link) {
    return (
      <>
        <DialogHeader>
          <DialogTitle>{name} is published</DialogTitle>
          <DialogDescription>{reach === 'private' ? 'Only you open it.' : 'People open it at its link, and find it in Apps.'}</DialogDescription>
        </DialogHeader>
        <div className="flex items-center gap-2">
          <Input readOnly value={link} aria-label="The app's link" className="font-mono text-xs" onFocus={(e) => e.target.select()} />
          <Button size="icon-sm" variant="ghost" aria-label="Copy the link" onClick={() => void navigator.clipboard.writeText(link).then(() => toast.success('Link copied'))}>
            <Copy />
          </Button>
        </div>
        <DialogFooter>
          <Button variant="outline" asChild>
            <a href={link}>Open it</a>
          </Button>
          <Button onClick={onClose}>Done</Button>
        </DialogFooter>
      </>
    )
  }
  return (
    <form
      className="grid gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        setError(null)
        publish.mutate()
      }}
    >
      <DialogHeader>
        <DialogTitle>Publish as app</DialogTitle>
        <DialogDescription>It runs as here, at its own link. It asks the model and keeps its data as the person using it.</DialogDescription>
      </DialogHeader>
      {error && <Alert variant="destructive">{error}</Alert>}
      <Field label="Name">
        <Input value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />
      </Field>
      <Field label="What it is for (optional)">
        <Textarea rows={2} maxLength={500} value={description} onChange={(e) => setDescription(e.target.value)} />
      </Field>
      <Segmented label="Who may open it" value={reach} onChange={setReach} options={(['private', 'groups', 'company'] as const).map((r) => ({ value: r, label: reachLabels[r] }))} />
      {reach === 'groups' && (
        <fieldset className="grid gap-1.5">
          <legend className="mb-1 text-sm font-medium">Groups</legend>
          {(groups.data ?? []).map((g) => (
            <div key={g.id} className="flex items-center gap-2 text-sm">
              <Checkbox id={`${id}-${g.id}`} checked={groupIds.includes(g.id)} onCheckedChange={(on) => setGroupIds(on ? [...groupIds, g.id] : groupIds.filter((x) => x !== g.id))} />
              <label htmlFor={`${id}-${g.id}`}>{g.name}</label>
            </div>
          ))}
          {groups.data?.length === 0 && <p className="text-xs text-muted-foreground">You are in no group.</p>}
        </fieldset>
      )}
      <DialogFooter>
        <Button type="button" variant="outline" onClick={onClose}>
          Cancel
        </Button>
        <Button type="submit" loading={publish.isPending} disabled={!name.trim() || (reach === 'groups' && groupIds.length === 0)}>
          Publish
        </Button>
      </DialogFooter>
    </form>
  )
}
