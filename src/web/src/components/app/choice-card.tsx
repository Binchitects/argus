import type { LucideIcon } from 'lucide-react'
import { cn } from '@/lib/utils'

/** One of a few choices as a card with a line of explanation: a radio button underneath. */
export function ChoiceCard({ name, checked, onChange, icon: Icon, title, hint, disabled }: {
  name: string
  checked: boolean
  onChange: () => void
  icon: LucideIcon
  title: string
  hint: string
  disabled?: boolean
}) {
  return (
    <label
      className={cn(
        'grid cursor-pointer gap-1 rounded-lg border p-3 text-left hover:bg-accent has-[:focus-visible]:ring-[3px] has-[:focus-visible]:ring-ring',
        checked && 'border-primary bg-primary/5',
        disabled && 'cursor-not-allowed opacity-50 hover:bg-transparent',
      )}
    >
      <input type="radio" name={name} className="sr-only" checked={checked} onChange={onChange} disabled={disabled} />
      <span className="flex items-center gap-1.5 text-sm font-medium">
        <Icon className="size-4" aria-hidden="true" /> {title}
      </span>
      <span className="text-xs text-muted-foreground">{hint}</span>
    </label>
  )
}
