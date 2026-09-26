import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'

/**
 * A box that scrolls on its own (a wide or long table, a log): focusable, so a
 * keyboard can scroll it, and named, so a screen reader says what it is.
 */
export function ScrollRegion({ label, children, className }: { label: string; children: ReactNode; className?: string }) {
  return (
    // oxlint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- a scrollable region must take focus to be scrollable by keyboard (WCAG 2.1.1)
    <section aria-label={label} tabIndex={0} className={cn('overflow-auto outline-none focus-visible:ring-[3px] focus-visible:ring-ring', className)}>
      {children}
    </section>
  )
}
