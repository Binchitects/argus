import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Brain, Check, Pencil, Plus, Trash2, X } from 'lucide-react'
import { useId, useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { useConfirm } from '@/components/ui/confirm'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { ago } from '@/lib/format'
import { memoriesQuery, type MemoryItem } from '@/lib/memory'

/**
 * What the chat remembers about the person: on or off, added, edited and deleted here (Your
 * account, and the chat's Memory button). Every answer reads them; nobody else sees them.
 */
export function MemoryManager() {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const switchId = useId()
  const view = useQuery(memoriesQuery)
  const [adding, setAdding] = useState('')
  const [editing, setEditing] = useState<{ id: string; text: string } | null>(null)
  const changed = () => queryClient.invalidateQueries({ queryKey: memoriesQuery.queryKey })
  const failed = (e: unknown) => toast.error(errorMessage(e))
  const turn = useMutation({
    mutationFn: (on: boolean) => api<{ on: boolean }>('/api/account/memories/settings', { method: 'PUT', body: { on } }),
    onSuccess: async (r) => {
      await changed()
      toast.success(r.on ? 'Memory is on' : 'Memory is off', { description: r.on ? 'Answers read what you asked the chat to remember.' : 'Answers neither read nor offer memories. Yours stay here.' })
    },
    onError: failed,
  })
  const add = useMutation({
    mutationFn: (text: string) => api<MemoryItem>('/api/account/memories', { body: { text } }),
    onSuccess: async () => {
      setAdding('')
      await changed()
    },
    onError: failed,
  })
  const save = useMutation({
    mutationFn: (m: { id: string; text: string }) => api<MemoryItem>(`/api/account/memories/${m.id}`, { method: 'PUT', body: { text: m.text } }),
    onSuccess: async () => {
      setEditing(null)
      await changed()
    },
    onError: failed,
  })
  const remove = useMutation({
    mutationFn: (id: string) => api(`/api/account/memories/${id}`, { method: 'DELETE' }),
    onSuccess: changed,
    onError: failed,
  })
  const clear = useMutation({
    mutationFn: () => api('/api/account/memories', { method: 'DELETE' }),
    onSuccess: changed,
    onError: failed,
  })

  if (view.error) return <Alert variant="destructive">{errorMessage(view.error)}</Alert>
  if (!view.data) return <Skeleton className="h-24" />
  const v = view.data
  const full = v.memories.length >= v.max
  return (
    <div className="grid gap-4">
      {!v.enabled && <Alert>Memory is off for everyone here: answers do not read these. An admin turns it on under Settings → Chat.</Alert>}
      <div className="flex items-start gap-2">
        <Switch id={switchId} checked={v.on} disabled={turn.isPending} onCheckedChange={(on) => turn.mutate(on)} />
        <div className="grid gap-0.5">
          <Label htmlFor={switchId}>Use memory</Label>
          <p className="text-xs text-muted-foreground">Answers read these, and the chat offers to remember what would help later. Off: it does neither.</p>
        </div>
      </div>
      <form
        className="flex gap-2"
        onSubmit={(e) => {
          e.preventDefault()
          if (adding.trim()) add.mutate(adding.trim())
        }}
      >
        <Input
          dir="auto"
          value={adding}
          maxLength={v.maxChars}
          onChange={(e) => setAdding(e.target.value)}
          placeholder="I deploy with Podman."
          aria-label="Something to remember"
          disabled={full}
        />
        <Button type="submit" variant="outline" loading={add.isPending} disabled={full || !adding.trim()}>
          <Plus /> Add
        </Button>
      </form>
      {v.memories.length === 0 ? (
        <p className="flex items-center gap-2 text-sm text-muted-foreground">
          <Brain className="size-4 shrink-0" aria-hidden="true" />
          Nothing remembered yet. Tell the chat “remember that …”, or add it here.
        </p>
      ) : (
        <ul className="grid gap-1.5" aria-label="Memories">
          {v.memories.map((m) => (
            <li key={m.id} className="flex min-w-0 items-center gap-2 rounded-lg border bg-muted/30 py-1.5 ps-3 pe-1.5 text-sm">
              {editing?.id === m.id ? (
                <form
                  className="flex min-w-0 flex-1 items-center gap-1"
                  onSubmit={(e) => {
                    e.preventDefault()
                    save.mutate(editing)
                  }}
                >
                  <Input
                    dir="auto"
                    className="h-8"
                    value={editing.text}
                    maxLength={v.maxChars}
                    onChange={(e) => setEditing({ id: m.id, text: e.target.value })}
                    aria-label="Memory"
                    // oxlint-disable-next-line jsx-a11y/no-autofocus -- opened by its Edit button, to type in at once
                    autoFocus
                  />
                  <Button type="submit" size="icon-sm" variant="ghost" aria-label="Save memory" loading={save.isPending}>
                    <Check />
                  </Button>
                  <Button type="button" size="icon-sm" variant="ghost" aria-label="Cancel" onClick={() => setEditing(null)}>
                    <X />
                  </Button>
                </form>
              ) : (
                <>
                  <span dir="auto" className="min-w-0 flex-1 [overflow-wrap:anywhere]">
                    {m.text}
                  </span>
                  <span className="hidden shrink-0 text-xs text-muted-foreground sm:inline">{ago(m.updatedAt)}</span>
                  <Button size="icon-sm" variant="ghost" className="size-7" aria-label={`Edit: ${m.text}`} onClick={() => setEditing({ id: m.id, text: m.text })}>
                    <Pencil />
                  </Button>
                  <Button size="icon-sm" variant="ghost" className="size-7" aria-label={`Delete: ${m.text}`} onClick={() => remove.mutate(m.id)}>
                    <Trash2 />
                  </Button>
                </>
              )}
            </li>
          ))}
        </ul>
      )}
      {v.memories.length > 0 && (
        <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
          <span>
            {v.memories.length} of {v.max}. The newest go first; answers read as many as fit in a few hundred words.
          </span>
          <Button
            variant="link"
            size="sm"
            className="ml-auto h-auto p-0 text-xs"
            onClick={async () => {
              if (await confirm({ title: 'Forget everything?', description: 'Every memory is deleted. Answers no longer know any of it.', confirm: 'Forget everything', destructive: true })) clear.mutate()
            }}
          >
            Forget everything
          </Button>
        </div>
      )}
    </div>
  )
}
