import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ListTodo } from 'lucide-react'
import { Link } from 'react-router'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { api, errorMessage } from '@/lib/api'
import { checklistsQuery } from '@/pages/checklists-api'
import { checklistRunQuery, type ChecklistRun } from './checklist-api'

/** The chat's checklist: one of yours turned on (the answers go through it and tick it), its ticks, and turning it off. */
export function ChecklistButton({ chat, answering }: { chat: string; answering: boolean }) {
  const queryClient = useQueryClient()
  // While an answer runs it ticks items: the page follows.
  const run = useQuery({ ...checklistRunQuery(chat), refetchInterval: answering ? 2000 : false })
  const lists = useQuery(checklistsQuery)
  const changed = (r: ChecklistRun | null) => queryClient.setQueryData(checklistRunQuery(chat).queryKey, r)
  const turnOn = useMutation({
    mutationFn: (checklistId: string) => api<ChecklistRun>(`/api/chat/conversations/${chat}/checklist`, { method: 'PUT', body: { checklistId } }),
    onSuccess: (r) => {
      changed(r)
      toast.success(`${r.name} is on`, { description: 'The answers in this chat go through it and tick each item.' })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const tick = useMutation({
    mutationFn: (t: { item: number; done: boolean }) => api<ChecklistRun>(`/api/chat/conversations/${chat}/checklist/tick`, { body: t }),
    onSuccess: changed,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const off = useMutation({
    mutationFn: () => api(`/api/chat/conversations/${chat}/checklist`, { method: 'DELETE' }),
    onSuccess: () => changed(null),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const r = run.data ?? null
  return (
    <Popover>
      <Tooltip content={r ? `Checklist: ${r.name}, ${r.done} of ${r.total} done` : 'Checklist: turn one on for this chat'}>
        <PopoverTrigger asChild>
          <Button variant={r ? 'secondary' : 'ghost'} size="sm" className="h-8 gap-1.5" aria-label={r ? `Checklist ${r.name} (${r.done} of ${r.total})` : 'Checklist'}>
            <ListTodo /> {r && <span className="hidden tabular-nums sm:inline">{`${r.done}/${r.total}`}</span>}
          </Button>
        </PopoverTrigger>
      </Tooltip>
      <PopoverContent align="end" className="grid w-80 gap-3">
        {r ? (
          <>
            <p className="text-sm font-medium">
              {r.name} <span className="font-normal text-muted-foreground tabular-nums">{`${r.done} of ${r.total}`}</span>
            </p>
            <ul className="grid max-h-80 gap-2 overflow-y-auto" aria-label="Checklist items">
              {r.items.map((i) => (
                <li key={i.number} className="flex items-start gap-2 text-sm">
                  <Checkbox
                    className="mt-0.5"
                    checked={i.done}
                    aria-label={i.text}
                    disabled={tick.isPending}
                    onCheckedChange={(on) => tick.mutate({ item: i.number, done: on === true })}
                  />
                  <span className="min-w-0">
                    <span className={i.done ? 'text-muted-foreground line-through' : undefined}>{i.text}</span>
                    {i.note && <span className="block text-xs text-muted-foreground">{`${i.by === 'person' ? 'You' : 'The model'}: ${i.note}`}</span>}
                  </span>
                </li>
              ))}
            </ul>
            <div className="flex justify-between gap-2">
              <Button size="sm" variant="ghost" loading={off.isPending} onClick={() => off.mutate()}>
                Turn it off
              </Button>
              <Button size="sm" variant="outline" asChild>
                <Link to="/checklists">Your checklists</Link>
              </Button>
            </div>
          </>
        ) : (
          <>
            <p className="text-sm text-muted-foreground">Turn a checklist on: the answers in this chat go through it and tick each item, saying how.</p>
            {lists.data?.length ? (
              <ul className="grid gap-1" aria-label="Your checklists">
                {lists.data.map((c) => (
                  <li key={c.id}>
                    <Button variant="ghost" size="sm" className="w-full justify-start" disabled={turnOn.isPending} onClick={() => turnOn.mutate(c.id)}>
                      {c.name} <span className="ml-auto text-xs text-muted-foreground tabular-nums">{c.items.length}</span>
                    </Button>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="text-sm">You have no checklists yet.</p>
            )}
            <Button size="sm" variant="outline" asChild>
              <Link to="/checklists">{lists.data?.length ? 'Your checklists' : 'Make one'}</Link>
            </Button>
          </>
        )}
      </PopoverContent>
    </Popover>
  )
}
