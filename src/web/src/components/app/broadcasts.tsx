import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Info, Megaphone, TriangleAlert, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'

/** A broadcast on now for the person: an admin's message to everyone, or to one of their groups. */
export interface ActiveBroadcast {
  id: string
  title: string
  body: string | null
  level: 'info' | 'warning' | 'critical'
  createdAt: string
  until: string | null
  /** A critical one shows while it lasts. */
  dismissable: boolean
}

const look = {
  info: { icon: Info, box: 'border-primary/30 bg-primary/5', ink: 'text-primary-ink' },
  warning: { icon: Megaphone, box: 'border-warning/40 bg-warning/10', ink: 'text-warning-ink' },
  critical: { icon: TriangleAlert, box: 'border-destructive/40 bg-destructive/10', ink: 'text-destructive-ink' },
} as const

/**
 * The broadcasts on now, above every page (the Arena's, and Code Arena's with its own calls): newest first, each dismissed
 * by its X (a critical one stays while it lasts). <paramref name="get"/> and <paramref name="dismiss"/> are the app's calls.
 */
export function BroadcastBanner({ get, dismiss, queryKey = ['broadcasts'] }: { get: (signal: AbortSignal) => Promise<ActiveBroadcast[]>; dismiss: (id: string) => Promise<unknown>; queryKey?: readonly unknown[] }) {
  const queryClient = useQueryClient()
  const list = useQuery({ queryKey, queryFn: ({ signal }) => get(signal), refetchInterval: 60_000, staleTime: 30_000 })
  const gone = useMutation({
    mutationFn: dismiss,
    onMutate: (id) => queryClient.setQueryData<ActiveBroadcast[]>(queryKey, (now) => now?.filter((b) => b.id !== id)),
    onError: (e) => {
      toast.error(errorMessage(e))
      void queryClient.invalidateQueries({ queryKey })
    },
  })
  const shown = list.data ?? []
  if (shown.length === 0) return null
  return (
    <section aria-label="Messages from the admins" className="grid gap-2">
      {shown.map((b) => {
        const l = look[b.level] ?? look.info
        const Icon = l.icon
        return (
          <div key={b.id} role={b.level === 'critical' ? 'alert' : 'status'} className={cn('flex items-start gap-3 rounded-lg border px-3 py-2 text-sm', l.box)}>
            <Icon className={cn('mt-0.5 size-4 shrink-0', l.ink)} aria-hidden="true" />
            <div className="grid min-w-0 flex-1 gap-0.5">
              <p dir="auto" className="font-medium">
                {b.title}
              </p>
              {b.body && (
                <p dir="auto" className="whitespace-pre-wrap text-muted-foreground">
                  {b.body}
                </p>
              )}
            </div>
            {b.dismissable && (
              <Button variant="ghost" size="icon-sm" className="-my-1 size-7 shrink-0" aria-label={`Dismiss: ${b.title}`} onClick={() => gone.mutate(b.id)}>
                <X />
              </Button>
            )}
          </div>
        )
      })}
    </section>
  )
}
