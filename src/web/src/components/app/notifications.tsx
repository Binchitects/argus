import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, AudioLines, BadgeCheck, Bell, Brain, BellOff, CalendarClock, Coins, Download, GitPullRequest, Image as ImageIcon, MessageSquareText, SquareSlash, type LucideIcon, Workflow, Wrench, X } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import { Button } from '@/components/ui/button'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { Switch } from '@/components/ui/switch'
import { toast } from '@/components/ui/toaster'
import { api } from '@/lib/api'
import { desktopState, setDesktop, tellDesktop, type DesktopState } from '@/lib/desktop'
import { cn } from '@/lib/utils'
import { sections, when } from './news'

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

/** Each kind's icon and its tint (the icon's circle). */
const kinds: Record<string, { icon: LucideIcon; tint: string }> = {
  answer: { icon: MessageSquareText, tint: 'bg-primary/10 text-primary' },
  task: { icon: CalendarClock, tint: 'bg-violet-500/10 text-violet-600 dark:text-violet-400' },
  usage: { icon: Coins, tint: 'bg-amber-500/10 text-amber-700 dark:text-amber-400' },
  alert: { icon: AlertTriangle, tint: 'bg-destructive/10 text-destructive-ink' },
  compliance: { icon: BadgeCheck, tint: 'bg-sky-500/10 text-sky-700 dark:text-sky-400' },
  download: { icon: Download, tint: 'bg-muted text-muted-foreground' },
  workflow: { icon: Workflow, tint: 'bg-teal-500/10 text-teal-700 dark:text-teal-400' },
  fix: { icon: Wrench, tint: 'bg-emerald-500/10 text-emerald-700 dark:text-emerald-400' },
  review: { icon: GitPullRequest, tint: 'bg-sky-500/10 text-sky-700 dark:text-sky-400' },
  media: { icon: ImageIcon, tint: 'bg-fuchsia-500/10 text-fuchsia-700 dark:text-fuchsia-400' },
  meetings: { icon: AudioLines, tint: 'bg-orange-500/10 text-orange-700 dark:text-orange-400' },
  memory: { icon: Brain, tint: 'bg-indigo-500/10 text-indigo-700 dark:text-indigo-400' },
  skill: { icon: SquareSlash, tint: 'bg-violet-500/10 text-violet-700 dark:text-violet-400' },
}

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
  const clear = useMutation({
    mutationFn: (id?: string) => api(id ? `/api/notifications/${id}` : '/api/notifications', { method: 'DELETE' }),
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
      <PopoverContent align="end" className="w-[24rem] max-w-[calc(100vw-1rem)] overflow-hidden p-0" aria-label="Notifications">
        <div className="flex items-center gap-1 border-b px-3 py-2">
          <p className="me-auto flex items-center gap-2 text-sm font-semibold">
            Notifications
            {unread > 0 && <span className="rounded-full bg-primary px-1.5 text-[0.6875rem] font-semibold text-primary-foreground tabular-nums">{unread}</span>}
          </p>
          {unread > 0 && (
            <Button variant="ghost" size="sm" className="h-7 px-2 text-xs" onClick={() => read.mutate(undefined)}>
              Mark all read
            </Button>
          )}
          {!!data.data?.items.length && (
            <Button variant="ghost" size="sm" className="h-7 px-2 text-xs" disabled={clear.isPending} onClick={() => clear.mutate(undefined)}>
              Clear all
            </Button>
          )}
        </div>
        {data.data?.items.length ? (
          <div className="max-h-[28rem] overflow-x-hidden overflow-y-auto overscroll-contain" aria-label="News">
            {sections(data.data.items).map(({ name: section, groups }) => (
              <section key={section} aria-label={section}>
                <h3 className="sticky top-0 z-10 bg-popover/95 px-3 pt-2 pb-1 text-[0.6875rem] font-semibold tracking-wide text-muted-foreground uppercase backdrop-blur">{section}</h3>
                <ul className="px-1.5 pb-1.5">
                  {groups.map(({ item: n, more }) => {
                    const kind = kinds[n.kind] ?? { icon: Bell, tint: 'bg-muted text-muted-foreground' }
                    const Icon = kind.icon
                    const unreadHere = !n.read || more.some((m) => !m.read)
                    return (
                      <li key={n.id} className="group relative">
                        <button
                          type="button"
                          className={cn('flex w-full min-w-0 items-start gap-3 rounded-lg py-2 ps-2 pe-9 text-left outline-none transition-colors hover:bg-accent focus-visible:bg-accent', unreadHere && 'bg-primary/[0.04]')}
                          onClick={() => {
                            for (const x of [n, ...more]) if (!x.read) read.mutate(x.id)
                            setOpen(false)
                            if (n.link) void navigate(n.link)
                          }}
                        >
                          <span className={cn('flex size-8 shrink-0 items-center justify-center rounded-full', kind.tint)} aria-hidden="true">
                            <Icon className="size-4" />
                          </span>
                          <span className="grid min-w-0 flex-1 grid-cols-[minmax(0,1fr)] gap-0.5">
                            <span className="flex items-baseline gap-2">
                              <span dir="auto" className={cn('min-w-0 flex-1 truncate text-sm', unreadHere ? 'font-semibold' : 'font-medium')}>
                                {n.title}
                              </span>
                              <time dateTime={n.createdAt} title={new Date(n.createdAt).toLocaleString()} className="shrink-0 text-[0.6875rem] text-muted-foreground tabular-nums">
                                {when(n.createdAt)}
                              </time>
                            </span>
                            {n.body && (
                              <span dir="auto" className="line-clamp-2 text-xs [overflow-wrap:anywhere] text-muted-foreground">
                                {n.body}
                              </span>
                            )}
                            {more.length > 0 && <span className="text-[0.6875rem] font-medium text-muted-foreground">+{more.length} more like it</span>}
                          </span>
                          {unreadHere && <span className="absolute top-3.5 left-0.5 size-1.5 rounded-full bg-primary" aria-label="New" />}
                        </button>
                        <Button
                          variant="ghost"
                          size="icon-sm"
                          className="absolute end-1 top-1.5 size-7 opacity-100 transition-opacity md:opacity-0 md:group-focus-within:opacity-100 md:group-hover:opacity-100"
                          onClick={() => {
                            for (const x of [n, ...more]) clear.mutate(x.id)
                          }}
                          aria-label={`Clear: ${n.title}`}
                        >
                          <X />
                        </Button>
                      </li>
                    )
                  })}
                </ul>
              </section>
            ))}
          </div>
        ) : (
          <div className="flex flex-col items-center gap-2 px-6 py-10 text-center">
            <span className="flex size-10 items-center justify-center rounded-full bg-muted" aria-hidden="true">
              <BellOff className="size-5 text-muted-foreground" />
            </span>
            <p className="text-sm font-medium">You are all caught up</p>
            <p className="text-xs text-muted-foreground">Answers that finish while you are away, your tasks and workflows, fixes and your credit report here.</p>
          </div>
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
