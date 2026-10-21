import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Copy, Download, Link2, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { Segmented } from '@/components/app/segmented'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'

interface Invite {
  id: string
  role: 'member' | 'viewer'
  createdAt: string
  expiresAt: string
  uses: number
  maxUses: number | null
  by: string | null
  mine: boolean
}

/** Links to join the room: made with a role, how long they work and for how many; each stopped at any time. */
export function InviteLinks({ chatId, owner }: { chatId: string; owner: boolean }) {
  const queryClient = useQueryClient()
  const key = ['rooms', chatId, 'invites']
  const invites = useQuery({ queryKey: key, queryFn: ({ signal }) => api<Invite[]>(`/api/rooms/${chatId}/invites`, { signal }) })
  const [role, setRole] = useState<'member' | 'viewer'>('member')
  const [days, setDays] = useState('7')
  const [uses, setUses] = useState('')
  const [made, setMade] = useState<string | null>(null)
  const make = useMutation({
    mutationFn: () => api<{ link: string }>(`/api/rooms/${chatId}/invites`, { body: { role, days: Number(days) || 7, uses: uses.trim() ? Number(uses) : null } }),
    onSuccess: (r) => {
      setMade(`${window.location.origin}${r.link}`)
      void queryClient.invalidateQueries({ queryKey: key })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const stop = useMutation({
    mutationFn: (id: string) => api(`/api/rooms/${chatId}/invites/${id}`, { method: 'DELETE' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: key }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <section className="grid gap-3" aria-label="Invite links">
      <h3 className="text-sm font-medium">Invite links</h3>
      <p className="text-xs text-muted-foreground">Whoever opens one, signed in, joins with its role. The link is shown once.</p>
      <Segmented label="They join as" value={role} onChange={setRole} options={[{ value: 'member', label: 'Members' }, { value: 'viewer', label: 'Viewers' }]} />
      <div className="grid grid-cols-2 gap-2">
        <Field label="Works for (days)">
          <Input inputMode="numeric" value={days} onChange={(e) => setDays(e.target.value)} />
        </Field>
        <Field label="For how many">
          <Input inputMode="numeric" value={uses} onChange={(e) => setUses(e.target.value)} placeholder="anyone" />
        </Field>
      </div>
      <div>
        <Button size="sm" variant="outline" onClick={() => make.mutate()} loading={make.isPending}>
          <Link2 /> Make a link
        </Button>
      </div>
      {made && (
        <div className="flex items-center gap-2 rounded-md border bg-muted/40 p-2">
          <Input readOnly value={made} aria-label="The new link" className="h-8 font-mono text-xs" onFocus={(e) => e.target.select()} />
          <Button size="icon-sm" variant="ghost" aria-label="Copy the link" onClick={() => void navigator.clipboard.writeText(made).then(() => toast.success('Link copied'))}>
            <Copy />
          </Button>
        </div>
      )}
      {!!invites.data?.length && (
        <ul className="grid gap-1.5 text-xs" aria-label="Links that work">
          {invites.data.map((i) => (
            <li key={i.id} className="flex items-center gap-2 rounded-md border px-2 py-1.5">
              <span className="flex-1">
                As {i.role}s, until {new Date(i.expiresAt).toLocaleDateString()} · {i.uses}
                {i.maxUses ? ` of ${i.maxUses}` : ''} joined{i.by ? ` · by ${i.by}` : ''}
              </span>
              {(owner || i.mine) && (
                <Button size="icon-sm" variant="ghost" className="size-7" aria-label="Stop this link" onClick={() => stop.mutate(i.id)}>
                  <Trash2 />
                </Button>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

/** The room as a file, with who wrote each message. */
export function RoomExport({ chatId }: { chatId: string }) {
  return (
    <section className="grid gap-2" aria-label="Export">
      <h3 className="text-sm font-medium">Export</h3>
      <div className="flex gap-2">
        <Button size="sm" variant="outline" asChild>
          <a href={`/api/rooms/${chatId}/export`} download>
            <Download /> Markdown
          </a>
        </Button>
        <Button size="sm" variant="outline" asChild>
          <a href={`/api/rooms/${chatId}/export?format=json`} download>
            <Download /> JSON
          </a>
        </Button>
      </div>
    </section>
  )
}
