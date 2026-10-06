import { CircleHelp } from 'lucide-react'
import { lazy, Suspense, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle, SheetTrigger } from '@/components/ui/sheet'
import { Spinner } from '@/components/ui/spinner'
import { Tooltip } from '@/components/ui/tooltip'

// The help's words load when it is first opened, not with every page.
const HelpBody = lazy(() => import('./help-body').then((m) => ({ default: m.HelpBody })))

/**
 * The ? at the top of a page: the page's help in a panel beside it. It stays open while you
 * work on the page (and follows you to the next one); Esc or the cross closes it.
 */
export function HelpButton({ className }: { className?: string }) {
  const [open, setOpen] = useState(false)
  return (
    <Sheet open={open} onOpenChange={setOpen} modal={false}>
      <Tooltip content="Help for this page">
        <SheetTrigger asChild>
          <Button variant="ghost" size="icon-sm" className={className} aria-label="Help for this page">
            <CircleHelp />
          </Button>
        </SheetTrigger>
      </Tooltip>
      <SheetContent className="gap-0" onInteractOutside={(e) => e.preventDefault()}>
        <Suspense
          fallback={
            <SheetHeader>
              <SheetTitle>Help</SheetTitle>
              <SheetDescription className="sr-only">Loading the help for this page</SheetDescription>
              <Spinner />
            </SheetHeader>
          }
        >
          <HelpBody onNavigate={() => setOpen(false)} />
        </Suspense>
      </SheetContent>
    </Sheet>
  )
}
