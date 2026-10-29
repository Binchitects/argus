import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, ArrowRight, BadgeCheck, Cpu, ExternalLink, KeyRound, LineChart, ListOrdered, ListTodo, MessageSquareHeart, ScrollText, type LucideIcon } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { api, infoQuery, meQuery } from '@/lib/api'
import { cn } from '@/lib/utils'
import { findNavItem } from './nav'

/** One page of a release's tour: what changed for people, in a few words, and where to try it. */
interface Slide {
  icon: LucideIcon
  title: string
  text: string
  /** Where to try it, and what the button says. */
  to: string
  action: string
  /** Shown only to those who hold it (an admin page's permission); everyone else skips the page. */
  permission?: string
}

/** A release's tour: shown once to each person after it is deployed (the newest one only). */
interface Tour {
  /** The release line it is for (7.0): its patch releases (7.0.1) share it. */
  release: string
  title: string
  slides: Slide[]
}

const latestTour: Tour = {
  release: '7.5',
  title: "What's new in Argus Arena 7.5",
  slides: [
    {
      icon: ListTodo,
      title: 'Checklists',
      text: 'Write a checklist once (your definition of done, a release\'s steps) and turn it on in a chat or in Code Arena: the model works through it and ticks each item with what it did, and the list is the same in both.',
      to: '/checklists',
      action: 'Open Checklists',
    },
    {
      icon: KeyRound,
      title: 'API keys of your own',
      text: 'Account → API keys: a key for each tool or script, named, each with its own limits under yours, its own requests at once and a monthly budget, and each revoked on its own.',
      to: '/account',
      action: 'Make a key',
    },
    {
      icon: BadgeCheck,
      title: 'Code Arena, signed',
      text: "Sign in to Code Arena with your user name and password, open and add folders, and reference files outside the folder with @. Its downloads are signed by your server's own CA, and its installers and code-arena update check that before they install.",
      to: '/help/code-arena',
      action: 'Read about Code Arena',
    },
    {
      icon: MessageSquareHeart,
      title: 'Say what you think',
      text: 'Rate any answer and say why, or Send feedback from the account menu about Arena itself: both reach the people who run it.',
      to: '/',
      action: 'Open the chat',
    },
    {
      icon: LineChart,
      title: 'A dashboard for each role',
      text: 'LLM and users, System and Hardware, each with its own permission. Usage by group shows each group\'s requests, tokens and spend with its members.',
      to: '/admin/dashboards',
      action: 'Open Dashboards',
      permission: 'usage.read',
    },
    {
      icon: ListOrdered,
      title: 'Fair when it is busy',
      text: "Requests at once for each person and group, API keys' requests in the same fair line as the chat's, and a longest line past which a request is told to try again. Admin → Line shows who runs and who waits, in order.",
      to: '/admin/line',
      action: 'Open the line',
      permission: 'usage.read',
    },
    {
      icon: ScrollText,
      title: 'The prompt log',
      text: 'Every prompt and answer, the chat\'s and the API\'s, searchable by those with its own permission, and each search audited. Kept as long as the retention settings say.',
      to: '/admin/prompts',
      action: 'Open the prompt log',
      permission: 'prompts.review',
    },
    {
      icon: Cpu,
      title: 'SGLang beside vLLM',
      text: 'A third engine, chosen at install, upgrade or repair: SGLang runs one server a model on its GPUs, as vLLM does. Models on disk are listed with what uses them, and removed with their files.',
      to: '/help/deployment',
      action: 'Read how',
      permission: 'settings.read',
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
  const me = useQuery(meQuery).data
  const off = new Set(me?.featuresOff ?? [])
  // A page about a feature that is off here (deploy/features) is skipped too.
  const slides = latestTour.slides.filter((s) => (!s.permission || me?.isAdmin || me?.permissions?.includes(s.permission)) && !off.has(findNavItem(s.to)?.feature ?? ''))
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
  // Every page about a feature that is off here: no tour.
  if (slides.length === 0) return null
  const slide = slides[Math.min(page, slides.length - 1)]!
  const Icon = slide.icon
  const last = page >= slides.length - 1
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
            {slides.map((s, i) => (
              <button
                key={s.title}
                type="button"
                onClick={() => setPage(i)}
                aria-label={`${s.title} (${i + 1} of ${slides.length})`}
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
