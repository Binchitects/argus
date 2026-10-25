import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, ArrowRight, Brain, ExternalLink, GraduationCap, MessagesSquare, SquareTerminal, type LucideIcon } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { api, infoQuery } from '@/lib/api'
import { cn } from '@/lib/utils'

/** One page of a release's tour: what changed for people, in a few words, and where to try it. */
interface Slide {
  icon: LucideIcon
  title: string
  text: string
  /** Where to try it, and what the button says. */
  to: string
  action: string
}

/** A release's tour: shown once to each person after it is deployed (the newest one only). */
interface Tour {
  /** The release line it is for (7.0): its patch releases (7.0.1) share it. */
  release: string
  title: string
  slides: Slide[]
}

const latestTour: Tour = {
  release: '7.0',
  title: "What's new in Argus Arena 7.0",
  slides: [
    {
      icon: Brain,
      title: 'Memory that lasts',
      text: 'The chat and Code Arena now share one memory. It brings what matters to each question, learns from chats that went quiet, and tidies itself, every change with Undo.',
      to: '/memory',
      action: 'Open Memory',
    },
    {
      icon: MessagesSquare,
      title: 'Chats that never lose the thread',
      text: 'A long chat keeps every word: the model reads earlier messages back when it needs them, finds your past chats by what they were about, and you can pin a message or leave one out.',
      to: '/chat',
      action: 'Start a chat',
    },
    {
      icon: GraduationCap,
      title: 'Skills that learn',
      text: 'Twenty-one skills come with Arena, and it learns new ones from your work. When one goes wrong or you correct it, a better version waits for you, with Undo.',
      to: '/skills',
      action: 'See your skills',
    },
    {
      icon: SquareTerminal,
      title: 'Code Arena in one line',
      text: 'Our coding agent and its IDE install with one command from Connect your tools, and keep themselves up to date from this Arena.',
      to: '/setup',
      action: 'Install Code Arena',
    },
  ],
}

/** Whether release a (7.0) comes before release b: by their numbers, so a later one seen counts as this one seen. */
const before = (a: string, b: string) => {
  const x = a.split('.').map(Number)
  const y = b.split('.').map(Number)
  for (let i = 0; i < Math.max(x.length, y.length); i++) {
    if ((x[i] ?? 0) !== (y[i] ?? 0)) return (x[i] ?? 0) < (y[i] ?? 0)
  }
  return false
}

/** Whether a person who has seen <seen> (null: none) should see the newest tour. */
export const wantsTour = (seen: string | null | undefined) => !seen || before(seen, latestTour.release)

const seenQuery = {
  queryKey: ['account', 'whats-new'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<{ seen: string | null }>('/api/account/whats-new', { signal }),
}

/**
 * "What's new": the newest release's tour, opened once for each person who has not seen it (then kept as seen), and again
 * from the account menu or the command palette. Each page says what changed and takes them where to try it.
 */
export function WhatsNew({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const seen = useQuery(seenQuery)
  const info = useQuery(infoQuery).data
  const [page, setPage] = useState(0)
  const opened = useRef(false)
  const markSeen = useMutation({
    mutationFn: () => api('/api/account/whats-new', { method: 'PUT', body: { release: latestTour.release } }),
    onSuccess: () => queryClient.setQueryData(seenQuery.queryKey, { seen: latestTour.release }),
  })

  // Once per release: the first page after it is deployed opens it.
  useEffect(() => {
    if (opened.current || !seen.data || !wantsTour(seen.data.seen)) return
    opened.current = true
    onOpenChange(true)
  }, [seen.data, onOpenChange])

  const close = (to?: string) => {
    onOpenChange(false)
    setPage(0)
    if (wantsTour(seen.data?.seen)) markSeen.mutate()
    if (to) navigate(to)
  }
  const slide = latestTour.slides[page]!
  const Icon = slide.icon
  const last = page === latestTour.slides.length - 1
  const notes = info?.source && info.version ? `${info.source.replace(/\/$/, '')}/releases/tag/v${info.version}` : null

  return (
    <Dialog open={open} onOpenChange={(o) => (o ? onOpenChange(true) : close())}>
      <DialogContent className="gap-0 overflow-hidden p-0 sm:max-w-lg">
        <div className="relative grid h-40 place-items-center overflow-hidden bg-gradient-to-br from-primary/90 via-primary/70 to-violet-600/80" aria-hidden="true">
          <div className="absolute -top-10 -left-10 size-40 rounded-full bg-white/10" />
          <div className="absolute -right-8 -bottom-12 size-48 rounded-full bg-white/10" />
          <div key={page} className="grid size-20 animate-pop place-items-center rounded-3xl bg-white/15 shadow-lg ring-1 ring-white/30 backdrop-blur">
            <Icon className="size-10 text-white" />
          </div>
        </div>
        <div className="grid gap-4 p-6">
          <DialogHeader className="gap-1 text-left">
            <p className="text-xs font-medium tracking-wide text-primary-ink uppercase">{latestTour.title}</p>
            <DialogTitle className="text-xl">{slide.title}</DialogTitle>
            <DialogDescription className="text-sm leading-relaxed">{slide.text}</DialogDescription>
          </DialogHeader>
          <nav className="flex items-center gap-1.5" aria-label="Pages">
            {latestTour.slides.map((s, i) => (
              <button
                key={s.title}
                type="button"
                onClick={() => setPage(i)}
                aria-label={`${s.title} (${i + 1} of ${latestTour.slides.length})`}
                aria-current={i === page ? 'step' : undefined}
                className={cn('h-1.5 rounded-full transition-all', i === page ? 'w-6 bg-primary' : 'w-1.5 bg-muted-foreground/30 hover:bg-muted-foreground/60')}
              />
            ))}
          </nav>
          <DialogFooter className="flex-row flex-wrap items-center gap-2 sm:justify-between">
            <Button variant="outline" size="sm" onClick={() => close(slide.to)}>
              {slide.action}
            </Button>
            <div className="ms-auto flex items-center gap-2">
              {page > 0 && (
                <Button variant="ghost" size="sm" onClick={() => setPage(page - 1)} aria-label="Back">
                  <ArrowLeft />
                </Button>
              )}
              {last ? (
                <Button size="sm" onClick={() => close()}>
                  Done
                </Button>
              ) : (
                <Button size="sm" onClick={() => setPage(page + 1)}>
                  Next <ArrowRight />
                </Button>
              )}
            </div>
          </DialogFooter>
          {notes && (
            <a href={notes} target="_blank" rel="noopener noreferrer" className="flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground">
              All of what changed in {info!.version} <ExternalLink className="size-3" aria-hidden="true" />
            </a>
          )}
        </div>
      </DialogContent>
    </Dialog>
  )
}
