import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Bell } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import { Button } from '@/components/ui/button'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { toast } from '@/components/ui/toaster'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'

interface Notifications {
  unread: number
  items: { id: string; title: string; body: string | null; link: string | null; createdAt: string; read: boolean }[]
}

/** The bell: what scheduled tasks brought. Checked every minute; a new one also shows as a toast. */
export function NotificationBell() {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const [open, setOpen] = useState(false)
  const data = useQuery({ queryKey: ['notifications'], queryFn: ({ signal }) => api<Notifications>('/api/notifications', { signal }), refetchInterval: 60_000 })
  const seen = useRef<string | null>(null)
  const newest = data.data?.items[0]
  useEffect(() => {
    if (!newest) return
    // Not on the first load: only what arrives while the page is open.
    if (seen.current !== null && seen.current !== newest.id && !newest.read) toast(newest.title, { description: newest.body?.slice(0, 140) })
    seen.current = newest.id
  }, [newest])
  const read = useMutation({
    mutationFn: (id?: string) => api(id ? `/api/notifications/${id}/read` : '/api/notifications/read', { method: 'POST' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['notifications'] }),
  })
  const unread = data.data?.unread ?? 0
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button variant="ghost" size="icon-sm" className="relative" aria-label={unread ? `Notifications, ${unread} new` : 'Notifications'}>
          <Bell />
          {unread > 0 && (
            <span aria-hidden="true" className="absolute -top-0.5 -right-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-primary px-1 text-[0.625rem] font-semibold text-primary-foreground tabular-nums">
              {unread > 9 ? '9+' : unread}
            </span>
          )}
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-80 p-0" aria-label="Notifications">
        <div className="flex items-center justify-between border-b px-3 py-2">
          <p className="text-sm font-medium">Notifications</p>
          {unread > 0 && (
            <Button variant="ghost" size="sm" className="h-7 px-2 text-xs" onClick={() => read.mutate(undefined)}>
              Mark all read
            </Button>
          )}
        </div>
        {data.data?.items.length ? (
          <ul className="max-h-96 overflow-y-auto">
            {data.data.items.map((n) => (
              <li key={n.id}>
                <button
                  type="button"
                  className={cn('grid w-full gap-0.5 border-b px-3 py-2 text-left outline-none last:border-b-0 hover:bg-accent focus-visible:bg-accent', !n.read && 'bg-primary/5')}
                  onClick={() => {
                    if (!n.read) read.mutate(n.id)
                    setOpen(false)
                    if (n.link) void navigate(n.link)
                  }}
                >
                  <span className="flex items-center gap-2 text-sm font-medium">
                    {!n.read && <span className="size-1.5 shrink-0 rounded-full bg-primary" aria-label="New" />}
                    <span className="truncate">{n.title}</span>
                  </span>
                  {n.body && (
                    <span dir="auto" className="line-clamp-2 text-xs text-muted-foreground">
                      {n.body}
                    </span>
                  )}
                  <span className="text-[0.6875rem] text-muted-foreground">{new Date(n.createdAt).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })}</span>
                </button>
              </li>
            ))}
          </ul>
        ) : (
          <p className="px-3 py-6 text-center text-sm text-muted-foreground">Nothing yet. Scheduled tasks report here.</p>
        )}
      </PopoverContent>
    </Popover>
  )
}
