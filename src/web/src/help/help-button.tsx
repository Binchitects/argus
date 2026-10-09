import { CircleHelp, X } from 'lucide-react'
import { Component, lazy, Suspense, useCallback, useEffect, useRef, useState, type ElementType, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { Spinner } from '@/components/ui/spinner'
import { Tooltip } from '@/components/ui/tooltip'
import { useMedia } from '@/lib/use-media'

/** Wide enough for the help to sit beside the page, which makes room for it. */
export const besideQuery = '(min-width: 1280px)'

const panelId = 'help-panel'

/**
 * The help's words, fetched when it is first opened, not with every page. One retry, half a
 * second later, as for the pages (app/routes.tsx); after that the panel says it did not load
 * and offers another try, and the page stays as it was.
 */
const loadBody = () => import('./help-body')
const lazyBody = () => lazy(() => loadBody().catch(() => new Promise<void>((r) => setTimeout(r, 500)).then(loadBody)).then((m) => ({ default: m.HelpBody })))
// Replaced by Try again: a lazy component keeps its failure.
let HelpBody = lazyBody()

/** The ? at the top of a page: opens and closes the page's help (HelpPanel). */
export function HelpButton({ open, onOpenChange, className }: { open: boolean; onOpenChange: (open: boolean) => void; className?: string }) {
  return (
    <Tooltip content={open ? 'Close the help' : 'Help for this page'}>
      <Button
        variant="ghost"
        size="icon-sm"
        className={className}
        aria-label="Help for this page"
        aria-expanded={open}
        aria-controls={open ? panelId : undefined}
        onClick={() => onOpenChange(!open)}
        onKeyDown={(e) => {
          if (e.key === 'Escape' && open) onOpenChange(false)
        }}
      >
        <CircleHelp />
      </Button>
    </Tooltip>
  )
}

/**
 * The help of the page on screen. On a wide screen it sits beside the page, which makes room
 * for it: it stays open while you work and follows you to the next page, and Esc closes it from
 * inside it or from its ? (an Esc in the page is the page's). On a narrower screen it opens
 * over the page, as the other panels do.
 */
export function HelpPanel({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const beside = useMedia(besideQuery)
  const close = useCallback(() => onOpenChange(false), [onOpenChange])
  if (beside) return open ? <Beside onClose={close} /> : null
  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent id={panelId} className="gap-0 sm:max-w-[22rem]">
        <HelpContent dialog onNavigate={() => onOpenChange(false)} />
      </SheetContent>
    </Sheet>
  )
}

function Beside({ onClose }: { onClose: () => void }) {
  const ref = useRef<HTMLElement>(null)
  const close = useCallback(() => {
    const inside = ref.current?.contains(document.activeElement)
    // Its ?, which names the panel while it is open.
    const button = document.querySelector<HTMLElement>(`[aria-controls="${panelId}"]`)
    onClose()
    if (inside) button?.focus()
  }, [onClose])
  // Focus goes into the panel as it opens; Tab goes on through its links.
  useEffect(() => ref.current?.focus(), [])
  useEffect(() => {
    const panel = ref.current
    if (!panel) return
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented) return
      e.preventDefault()
      close()
    }
    panel.addEventListener('keydown', onKey)
    return () => panel.removeEventListener('keydown', onKey)
  }, [close])
  return (
    <aside ref={ref} id={panelId} aria-label="Help" tabIndex={-1} className="w-[22rem] shrink-0 border-l bg-card text-card-foreground outline-none">
      {/* The column stretches with the page; the panel inside stays in view. */}
      <div className="sticky top-0 flex h-dvh flex-col">
        <HelpContent onNavigate={close} />
        <button
          type="button"
          onClick={close}
          className="absolute top-4 right-4 rounded-sm opacity-70 transition-opacity hover:opacity-100 focus-visible:ring-[3px] focus-visible:ring-ring focus-visible:outline-none"
        >
          <X className="size-4" aria-hidden="true" />
          <span className="sr-only">Close the help</span>
        </button>
      </div>
    </aside>
  )
}

/** The panel's words: the page's help, or while it loads (or if it does not) a line that says so. */
function HelpContent({ dialog = false, onNavigate }: { dialog?: boolean; onNavigate: () => void }) {
  const [attempt, setAttempt] = useState(0)
  return (
    <LoadBoundary
      key={attempt}
      fallback={
        <>
          <PanelHeader dialog={dialog} title="The help did not load" about="The connection dropped, or a newer version was deployed." />
          <div className="grid content-start gap-3 p-5 text-sm">
            <p className="text-muted-foreground">Try again. If it still does not load, reload the page: what you typed and did not send is lost.</p>
            <div className="flex flex-wrap gap-2">
              <Button
                size="sm"
                onClick={() => {
                  HelpBody = lazyBody()
                  setAttempt((n) => n + 1)
                }}
              >
                Try again
              </Button>
              <Button size="sm" variant="outline" onClick={() => window.location.reload()}>
                Reload the page
              </Button>
            </div>
          </div>
        </>
      }
    >
      <Suspense
        fallback={
          <PanelHeader dialog={dialog} title="Help" about="Loading the help for this page" aboutHidden>
            <Spinner />
          </PanelHeader>
        }
      >
        <HelpBody dialog={dialog} onNavigate={onNavigate} />
      </Suspense>
    </LoadBoundary>
  )
}

/** The panel's title and line under it: a sheet's own (its name for a screen reader), or plain headings beside the page. */
function PanelHeader({ dialog, title, about, aboutHidden, children }: { dialog: boolean; title: string; about: string; aboutHidden?: boolean; children?: ReactNode }) {
  const Title: ElementType = dialog ? SheetTitle : 'h2'
  const About: ElementType = dialog ? SheetDescription : 'p'
  return (
    <SheetHeader>
      <Title className="text-lg font-semibold">{title}</Title>
      <About className={aboutHidden ? 'sr-only' : 'text-sm text-muted-foreground'}>{about}</About>
      {children}
    </SheetHeader>
  )
}

/** What the help throws (its code did not arrive) stays in the panel: the page around it is untouched. */
class LoadBoundary extends Component<{ children: ReactNode; fallback: ReactNode }, { failed: boolean }> {
  state = { failed: false }

  static getDerivedStateFromError() {
    return { failed: true }
  }

  render() {
    return this.state.failed ? this.props.fallback : this.props.children
  }
}
