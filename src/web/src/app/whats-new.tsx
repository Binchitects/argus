import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, ArrowRight, Atom, BadgeCheck, ExternalLink, ScanSearch, Server, ShieldAlert, Wrench, type LucideIcon } from 'lucide-react'
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
  release: '7.4',
  title: "What's new in Argus Arena 7.4",
  slides: [
    {
      icon: ShieldAlert,
      title: 'Security reviews',
      text: "Code Studio → Security reads a repository as you: each weakness quotes its line, names its CWE and traces the data to it, with the pages it rests on; a second pass tries to refute it. Its packages are matched against GitHub's advisories, known-exploited first, with the smallest upgrade.",
      to: '/code',
      action: 'Review a repository',
    },
    {
      icon: Wrench,
      title: 'Fix it, for security too',
      text: "Fix it on a security finding: the agent writes a test that shows the weakness, makes the change the cheat sheet describes, and opens the merge request as you. A merge request's review now says when it brings a vulnerable package or a scanner's finding.",
      to: '/code',
      action: 'Open Code Studio',
    },
    {
      icon: ScanSearch,
      title: 'Security across the company',
      text: 'Admin → Security: every repository at a glance, what cannot wait, what changed this week, the risks accepted. The advisories are renewed every day, and old reviews are checked again against them: a new CVE in old code is found without a push.',
      to: '/admin/security',
      action: 'Open Security',
      permission: 'reviews.admin',
    },
    {
      icon: BadgeCheck,
      title: 'GRC and the certifications',
      text: "Fourteen frameworks, each clause mapped: ISO 27001 and 42001, SOC 2, NIST's, the EU AI Act and more. The risk register, the Statements of Applicability, policies, audits, suppliers, AI systems, and how ready you are for each certification, with its auditor's package.",
      to: '/admin/compliance',
      action: 'Open Compliance',
      permission: 'compliance.read',
    },
    {
      icon: Atom,
      title: 'Quantum computing in the lab',
      text: 'Run circuits on simulators with noise, check that two circuits are the same, sample error-correcting codes, follow a qubit as it decays, run VQE and QAOA, and compute molecules with quantum chemistry: drawn and charted in the notebook.',
      to: '/lab',
      action: 'Open the lab',
    },
    {
      icon: Server,
      title: 'Only what you need',
      text: 'Every part of the stack is a feature, turned on or off when installing, upgrading or repairing (installer.sh features lists them, with their sizes); what is off runs nothing and leaves the menus.',
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
