import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Brain, Check, FolderGit2, Pencil, Pin, PinOff, Plus, Search, Trash2, X } from 'lucide-react'
import { useId, useState } from 'react'
import { Link } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { ago } from '@/lib/format'
import { changeMemory, forgetMemory, keepMemory, kindLabel, memoriesQuery, scopeLabel, type MemoryInput, type MemoryItem, type MemoryKind, type MemoryScope } from '@/lib/memory'

const kinds = Object.keys(kindLabel) as MemoryKind[]

/** Where a memory came from, in a few words (a chat's, a link to it). */
function Source({ m }: { m: MemoryItem }) {
  if (m.source === 'chat' && m.sourceId)
    return (
      <Link to={`/chat/${m.sourceId}`} className="underline-offset-2 hover:underline">
        from a chat
      </Link>
    )
  return <span>{m.source === 'code-arena' ? 'from Code Arena' : m.source === 'import' ? 'brought in' : m.mine ? 'typed' : 'kept by an admin'}</span>
}

/** One memory: its words, kind and whose, where it came from and how often answers read it; pin, edit and forget it. */
function MemoryRow({ m, maxChars, onChange, onForget }: { m: MemoryItem; maxChars: number; onChange: (c: MemoryInput) => void; onForget: () => void }) {
  const [editing, setEditing] = useState<string | null>(null)
  return (
    <li className="grid gap-1.5 rounded-lg border bg-card p-3 text-sm" aria-label={m.text}>
      {editing !== null ? (
        <form
          className="flex min-w-0 items-center gap-1"
          onSubmit={(e) => {
            e.preventDefault()
            if (editing.trim()) onChange({ text: editing.trim() })
            setEditing(null)
          }}
        >
          <Input dir="auto" className="h-8" value={editing} maxLength={maxChars} onChange={(e) => setEditing(e.target.value)} aria-label="Memory" />
          <Button type="submit" size="icon-sm" variant="ghost" aria-label="Save memory">
            <Check />
          </Button>
          <Button type="button" size="icon-sm" variant="ghost" aria-label="Cancel" onClick={() => setEditing(null)}>
            <X />
          </Button>
        </form>
      ) : (
        <p dir="auto" className="[overflow-wrap:anywhere]">
          {m.text}
        </p>
      )}
      <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-muted-foreground">
        {m.canEdit ? (
          <select
            aria-label={`Kind: ${m.text}`}
            value={m.kind}
            onChange={(e) => onChange({ kind: e.target.value as MemoryKind })}
            className="h-6 rounded-md border bg-background px-1 text-xs"
          >
            {kinds.map((k) => (
              <option key={k} value={k}>
                {kindLabel[k]}
              </option>
            ))}
          </select>
        ) : (
          <Badge variant="secondary">{kindLabel[m.kind]}</Badge>
        )}
        {m.scope === 'company' && <Badge variant="outline">The company&apos;s</Badge>}
        {m.scope === 'project' && (
          <Badge variant="outline" className="max-w-56 truncate" title={m.project ?? undefined}>
            <FolderGit2 aria-hidden="true" /> {m.project?.split('/').slice(-2).join('/')}
          </Badge>
        )}
        <Source m={m} />
        <span>· {m.uses === 0 ? 'not read yet' : `read ${m.uses} ${m.uses === 1 ? 'time' : 'times'}, last ${ago(m.usedAt!)}`}</span>
        <span className="hidden sm:inline">· {ago(m.updatedAt)}</span>
        {m.canEdit && (
          <span className="ms-auto flex items-center">
            <Button
              size="icon-sm"
              variant="ghost"
              className="size-7"
              aria-pressed={m.pinned}
              aria-label={m.pinned ? `Unpin: ${m.text}` : `Pin: ${m.text}`}
              title={m.pinned ? 'Pinned: every answer reads it' : 'Pin: every answer reads it'}
              onClick={() => onChange({ pinned: !m.pinned })}
            >
              {m.pinned ? <PinOff /> : <Pin />}
            </Button>
            <Button size="icon-sm" variant="ghost" className="size-7" aria-label={`Edit: ${m.text}`} onClick={() => setEditing(m.text)}>
              <Pencil />
            </Button>
            <Button size="icon-sm" variant="ghost" className="size-7" aria-label={`Forget: ${m.text}`} onClick={onForget}>
              <Trash2 />
            </Button>
          </span>
        )}
      </div>
    </li>
  )
}

/**
 * Memory (Workspace → Memory): everything the chat and Code Arena remember for the person — about them, about their
 * projects, and the company's — searched, filtered, pinned, kept in its kind, edited and forgotten.
 */
export function MemoryPage() {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const switchId = useId()
  const view = useQuery(memoriesQuery)
  const [find, setFind] = useState('')
  const [scope, setScope] = useState<'all' | MemoryScope>('all')
  const [kind, setKind] = useState<'all' | MemoryKind>('all')
  const [adding, setAdding] = useState<{ text: string; kind: MemoryKind; company: boolean; pinned: boolean }>({ text: '', kind: 'fact', company: false, pinned: false })
  const changed = () => queryClient.invalidateQueries({ queryKey: memoriesQuery.queryKey })
  const failed = (e: unknown) => toast.error(errorMessage(e))
  const turn = useMutation({
    mutationFn: (on: boolean) => api<{ on: boolean }>('/api/account/memories/settings', { method: 'PUT', body: { on } }),
    onSuccess: changed,
    onError: failed,
  })
  const add = useMutation({
    mutationFn: () => keepMemory({ text: adding.text.trim(), kind: adding.kind, scope: adding.company ? 'company' : 'person', pinned: adding.pinned }),
    onSuccess: async () => {
      setAdding((a) => ({ ...a, text: '' }))
      await changed()
    },
    onError: failed,
  })
  const change = useMutation({ mutationFn: ({ id, c }: { id: string; c: MemoryInput }) => changeMemory(id, c), onSuccess: changed, onError: failed })
  const forget = useMutation({ mutationFn: forgetMemory, onSuccess: changed, onError: failed })
  const clear = useMutation({ mutationFn: () => api('/api/account/memories', { method: 'DELETE' }), onSuccess: changed, onError: failed })

  if (view.isPending) return <PageSkeleton />
  if (view.error) return <QueryError error={view.error} retry={() => view.refetch()} />
  const v = view.data
  const words = find.trim().toLowerCase()
  const shown = v.memories.filter(
    (m) => (scope === 'all' || m.scope === scope) && (kind === 'all' || m.kind === kind) && (!words || `${m.text} ${m.project ?? ''}`.toLowerCase().includes(words)),
  )
  const mine = v.memories.filter((m) => m.mine && m.scope !== 'company').length
  return (
    <>
      <PageHeader
        title="Memory"
        description="What the chat and Code Arena remember for you: about you, about your projects, and the company's. Each answer reads the pinned ones and those that matter to its question."
      />
      {!v.enabled && <Alert className="mb-4">Memory is off for everyone here: answers do not read these. An admin turns it on under Settings → Chat.</Alert>}
      <div className="mb-6 flex items-start gap-2">
        <Switch id={switchId} checked={v.on} disabled={turn.isPending} onCheckedChange={(on) => turn.mutate(on)} />
        <div className="grid gap-0.5">
          <Label htmlFor={switchId}>Use memory</Label>
          <p className="text-xs text-muted-foreground">Answers read these, and the chat offers to remember what would help later. Off: it does neither.</p>
        </div>
      </div>
      <Card className="mb-6">
        <CardHeader>
          <CardTitle className="text-base">Remember something</CardTitle>
          <CardDescription>One or two short sentences. Its kind helps answers use it: how you like things, a fact, a decision, how to do something, a lesson.</CardDescription>
        </CardHeader>
        <CardContent>
          <form
            className="grid gap-2"
            onSubmit={(e) => {
              e.preventDefault()
              if (adding.text.trim()) add.mutate()
            }}
          >
            <Input
              dir="auto"
              value={adding.text}
              maxLength={v.maxChars}
              onChange={(e) => setAdding((a) => ({ ...a, text: e.target.value }))}
              placeholder="I deploy with Podman, never Docker."
              aria-label="Something to remember"
            />
            <div className="flex flex-wrap items-center gap-3 text-sm">
              <select
                aria-label="Its kind"
                value={adding.kind}
                onChange={(e) => setAdding((a) => ({ ...a, kind: e.target.value as MemoryKind }))}
                className="h-9 rounded-md border bg-background px-2 text-sm"
              >
                {kinds.map((k) => (
                  <option key={k} value={k}>
                    {kindLabel[k]}
                  </option>
                ))}
              </select>
              <label className="flex items-center gap-1.5">
                <input type="checkbox" className="size-4 accent-primary" checked={adding.pinned} onChange={(e) => setAdding((a) => ({ ...a, pinned: e.target.checked }))} />
                Pinned
              </label>
              {v.keepsCompany && (
                <label className="flex items-center gap-1.5">
                  <input type="checkbox" className="size-4 accent-primary" checked={adding.company} onChange={(e) => setAdding((a) => ({ ...a, company: e.target.checked }))} />
                  The company&apos;s (everyone&apos;s answers read it)
                </label>
              )}
              <Button type="submit" variant="outline" className="ms-auto" loading={add.isPending} disabled={!adding.text.trim()}>
                <Plus /> Remember
              </Button>
            </div>
          </form>
        </CardContent>
      </Card>

      <div className="mb-4 flex flex-wrap items-center gap-3">
        <div className="relative w-full max-w-xs">
          <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
          <Input className="ps-8" value={find} onChange={(e) => setFind(e.target.value)} placeholder="Find a memory" aria-label="Find a memory" />
        </div>
        <Segmented
          label="Whose"
          value={scope}
          onChange={setScope}
          options={[{ value: 'all', label: 'All' }, ...(['person', 'project', 'company'] as MemoryScope[]).map((s) => ({ value: s, label: scopeLabel[s] }))]}
        />
        <select aria-label="Kind" value={kind} onChange={(e) => setKind(e.target.value as 'all' | MemoryKind)} className="h-9 rounded-md border bg-background px-2 text-sm">
          <option value="all">Every kind</option>
          {kinds.map((k) => (
            <option key={k} value={k}>
              {kindLabel[k]}
            </option>
          ))}
        </select>
      </div>
      {shown.length === 0 ? (
        <p className="flex items-center gap-2 text-sm text-muted-foreground">
          <Brain className="size-4 shrink-0" aria-hidden="true" />
          {v.memories.length === 0 ? 'Nothing remembered yet. Tell the chat “remember that …”, or add it above.' : 'No memory is like that.'}
        </p>
      ) : (
        <ul className="grid gap-2" aria-label="Memories">
          {shown.map((m) => (
            <MemoryRow key={`${m.id}:${m.updatedAt}`} m={m} maxChars={v.maxChars} onChange={(c) => change.mutate({ id: m.id, c })} onForget={() => forget.mutate(m.id)} />
          ))}
        </ul>
      )}
      <div className="mt-4 flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
        <span>
          {mine} of yours (of {v.max}). Pinned ones are read by every answer; the others when they matter to the question.
        </span>
        {mine > 0 && (
          <Button
            variant="link"
            size="sm"
            className="ml-auto h-auto p-0 text-xs"
            onClick={async () => {
              if (await confirm({ title: 'Forget all of yours?', description: 'Every memory of yours is deleted (the company’s stay). Answers no longer know any of it.', confirm: 'Forget all of mine', destructive: true }))
                clear.mutate()
            }}
          >
            Forget all of mine
          </Button>
        )}
      </div>
    </>
  )
}
