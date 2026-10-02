import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, Bell, CalendarClock, Coins, Download, MessageSquareText, type LucideIcon } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import { Button } from '@/components/ui/button'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api } from '@/lib/api'
import { desktopState, setDesktop, tellDesktop, type DesktopState } from '@/lib/desktop'
import { cn } from '@/lib/utils'

export interface NewsItem {
  id: string
  /** answer (finished while no page watched), task, usage (credit), alert (the system's, for admins), download. */
  kind: string
  title: string
  body: string | null
  link: string | null
  createdAt: string
  read: boolean
}

interface Notifications {
  unread: number
  items: NewsItem[]
}

const icons: Record<string, LucideIcon> = { answer: MessageSquareText, task: CalendarClock, usage: Coins, alert: AlertTriangle, download: Download }

/**
 * The bell: answers that finished while you were away, scheduled tasks, your credit,
 * the system's alerts (admins), model downloads. Checked every 15 seconds and when the
 * tab comes back; what arrives meanwhile also shows as a toast, or on the desktop when
 * the tab is hidden and desktop notifications are on.
 */
export function NotificationBell() {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const [open, setOpen] = useState(false)
  const [desktop, setDesktopState] = useState<DesktopState>(desktopState)
  const data = useQuery({
    queryKey: ['notifications'],
    queryFn: ({ signal }) => api<Notifications>('/api/notifications', { signal }),
    refetchInterval: 15_000,
    refetchOnWindowFocus: true,
  })
  const seen = useRef<Set<string> | null>(null)
  const items = data.data?.items
  useEffect(() => {
    if (!items) return
    // Not on the first load: only what arrives while the page is open.
    if (seen.current) {
      for (const n of items.filter((n) => !n.read && !seen.current!.has(n.id)).reverse()) {
        if (document.hidden) tellDesktop(n.title, n.body, n.id, () => n.link && void navigate(n.link))
        else toast(n.title, { description: n.body?.slice(0, 140), action: n.link ? { label: 'Open', onClick: () => void navigate(n.link!) } : undefined })
      }
    }
    seen.current = new Set(items.map((n) => n.id))
  }, [items, navigate])
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
      <PopoverContent align="end" className="w-[22rem] max-w-[calc(100vw-1rem)] p-0" aria-label="Notifications">
        <div className="flex items-center justify-between border-b px-3 py-2">
          <p className="text-sm font-medium">Notifications</p>
          {unread > 0 && (
            <Button variant="ghost" size="sm" className="h-7 px-2 text-xs" onClick={() => read.mutate(undefined)}>
              Mark all read
            </Button>
          )}
        </div>
        {data.data?.items.length ? (
          <ul className="max-h-96 overflow-y-auto" aria-label="News">
            {data.data.items.map((n) => {
              const Icon = icons[n.kind] ?? Bell
              return (
                <li key={n.id}>
                  <button
                    type="button"
                    className={cn('flex w-full gap-2.5 border-b px-3 py-2 text-left outline-none last:border-b-0 hover:bg-accent focus-visible:bg-accent', !n.read && 'bg-primary/5')}
                    onClick={() => {
                      if (!n.read) read.mutate(n.id)
                      setOpen(false)
                      if (n.link) void navigate(n.link)
                    }}
                  >
                    <Icon className={cn('mt-0.5 size-4 shrink-0', n.kind === 'alert' ? 'text-warning-ink' : 'text-muted-foreground')} aria-hidden="true" />
                    <span className="grid min-w-0 flex-1 gap-0.5">
                      <span className="flex items-center gap-2 text-sm font-medium">
                        <span dir="auto" className="truncate">
                          {n.title}
                        </span>
                        {!n.read && <span className="ml-auto size-1.5 shrink-0 rounded-full bg-primary" aria-label="New" />}
                      </span>
                      {n.body && (
                        <span dir="auto" className="line-clamp-2 text-xs text-muted-foreground">
                          {n.body}
                        </span>
                      )}
                      <span className="text-[0.6875rem] text-muted-foreground">{new Date(n.createdAt).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })}</span>
                    </span>
                  </button>
                </li>
              )
            })}
          </ul>
        ) : (
          <p className="px-3 py-6 text-center text-sm text-muted-foreground">Nothing yet. Answers that finish while you are away, scheduled tasks and your credit report here.</p>
        )}
        {desktop !== 'unsupported' && (
          <div className="flex items-center gap-2 border-t px-3 py-2">
            <label htmlFor="desktop-notifications" className="min-w-0 flex-1 text-xs">
              <span className="font-medium">Desktop notifications</span>
              <span className="block text-muted-foreground">{desktop === 'blocked' ? "Blocked in this browser's settings for this site." : 'When this tab is hidden.'}</span>
            </label>
            <Switch
              id="desktop-notifications"
              checked={desktop === 'on'}
              disabled={desktop === 'blocked'}
              onCheckedChange={async (on) => {
                const now = await setDesktop(on)
                setDesktopState(now)
                if (on && now === 'blocked') toast.error('The browser blocked desktop notifications for this site.')
              }}
            />
          </div>
        )}
      </PopoverContent>
    </Popover>
  )
}
