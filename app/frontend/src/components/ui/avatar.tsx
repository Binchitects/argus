import { cn } from '@/lib/utils'

/** Initials on a colour picked from the name, so a person keeps the same colour everywhere. */
export function Avatar({ name, className }: { name: string; className?: string }) {
  const initials = name.split(/[\s._@-]+/).filter(Boolean).slice(0, 2).map((p) => p[0]!.toUpperCase()).join('') || '?'
  let h = 0
  for (const c of name) h = (h * 31 + c.charCodeAt(0)) % 360
  return (
    <span
      aria-hidden="true"
      className={cn('inline-flex size-8 shrink-0 items-center justify-center rounded-full text-xs font-semibold text-white select-none', className)}
      style={{ backgroundColor: `oklch(0.55 0.12 ${h})` }}
    >
      {initials}
    </span>
  )
}
