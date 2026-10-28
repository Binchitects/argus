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
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { appsQuery, changeApp, publishApp, reachLabels, type AppBrief, type AppReach } from './apps-api'

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

/** Publish as app: a new version of one published before (from this chat, first), or a new app; then its link, to share. */
export function PublishDialog({ what, onClose }: { what: ToPublish | null; onClose: () => void }) {
  return (
    <Dialog open={what !== null} onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-lg">{what && <PublishLoader key={what.code.length + what.name} what={what} onClose={onClose} />}</DialogContent>
    </Dialog>
  )
}

/** Your apps of the same kind it can be a new version of: those from this chat first, then the latest changed. */
function earlier(apps: AppBrief[], what: ToPublish): AppBrief[] {
  const fromHere = (a: AppBrief) => (what.conversationId && a.conversationId === what.conversationId ? 0 : 1)
  return apps.filter((a) => a.mine && a.kind === what.kind).sort((a, b) => fromHere(a) - fromHere(b) || b.updatedAt.localeCompare(a.updatedAt)).slice(0, 30)
}

function PublishLoader({ what, onClose }: { what: ToPublish; onClose: () => void }) {
  const apps = useQuery(appsQuery)
  if (apps.isPending) return <Skeleton className="h-64" />
  return <PublishForm what={what} mine={earlier(apps.data ?? [], what)} onClose={onClose} />
}

const NEW = 'new'

function PublishForm({ what, mine, onClose }: { what: ToPublish; mine: AppBrief[]; onClose: () => void }) {
  const queryClient = useQueryClient()
  const id = useId()
  const fresh = { name: what.name.replace(/\.(html?|jsx|tsx)$/i, '').replace(/[-_]+/g, ' ').trim() || 'My app', description: '', reach: 'company' as AppReach, groupIds: [] as string[] }
  // Published from this chat before: its new version, unless they choose a new app.
  const first = mine.find((a) => what.conversationId && a.conversationId === what.conversationId)
  const [target, setTarget] = useState(first?.id ?? NEW)
  const start = first ?? fresh
  const [name, setName] = useState(start.name)
  const [description, setDescription] = useState(start.description)
  const [reach, setReach] = useState<AppReach>(start.reach)
  const [groupIds, setGroupIds] = useState<string[]>(start.groupIds)
  const [note, setNote] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState<{ link: string; version: number | null } | null>(null)
  const groups = useQuery({ queryKey: ['sharing', 'groups'], queryFn: ({ signal }) => api<MyGroup[]>('/api/sharing/groups', { signal }), enabled: reach === 'groups' })
  const choose = (t: string) => {
    setTarget(t)
    const from = mine.find((a) => a.id === t) ?? fresh
    setName(from.name)
    setDescription(from.description)
    setReach(from.reach)
    setGroupIds(from.groupIds)
  }
  const publish = useMutation({
    mutationFn: () => {
      const fields = { name: name.trim(), description: description.trim(), code: what.code, reach, groupIds: reach === 'groups' ? groupIds : [] }
      return target === NEW
        ? publishApp({ ...fields, kind: what.kind, conversationId: what.conversationId })
        : changeApp(target, { ...fields, note: note.trim() || 'Published from the chat' })
    },
    onSuccess: (app) => {
      setDone({ link: `${window.location.origin}/apps/${app.id}`, version: target === NEW ? null : app.version })
      void queryClient.invalidateQueries({ queryKey: ['apps'] })
    },
    onError: (e) => setError(errorMessage(e)),
  })
  if (done) {
    const link = done.link
    return (
      <>
        <DialogHeader>
          <DialogTitle>{done.version !== null ? `Version ${done.version} of ${name} is published` : `${name} is published`}</DialogTitle>
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
        <DialogDescription>
          It runs as here, at its own link. It asks the model and keeps its data as the person using it.
          {target !== NEW && ' A new version keeps the app’s link and its data; Apps → Versions puts an earlier one back.'}
        </DialogDescription>
      </DialogHeader>
      {error && <Alert variant="destructive">{error}</Alert>}
      {mine.length > 0 && (
        <Field label="Publish as">
          <Select value={target} onValueChange={choose}>
            <SelectTrigger>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {mine.map((a) => (
                <SelectItem key={a.id} value={a.id}>
                  A new version of {a.name} (now version {a.version}
                  {what.conversationId && a.conversationId === what.conversationId ? ', from this chat' : ''})
                </SelectItem>
              ))}
              <SelectItem value={NEW}>A new app</SelectItem>
            </SelectContent>
          </Select>
        </Field>
      )}
      <Field label="Name">
        <Input value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />
      </Field>
      <Field label="What it is for (optional)">
        <Textarea rows={2} maxLength={500} value={description} onChange={(e) => setDescription(e.target.value)} />
      </Field>
      {target !== NEW && (
        <Field label="What changed (optional)">
          <Input value={note} maxLength={200} onChange={(e) => setNote(e.target.value)} placeholder="Published from the chat" />
        </Field>
      )}
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
          {target === NEW ? 'Publish' : 'Publish the new version'}
        </Button>
      </DialogFooter>
    </form>
  )
}
