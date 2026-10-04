import { BookOpen, Bot, Briefcase, Building2, ChartColumn, Check, Code2, FlaskConical, Globe, GraduationCap, Lock, PenLine, Shield, Sparkles, Users, Wrench, type LucideIcon } from 'lucide-react'
import { cn } from '@/lib/utils'
import type { Reach } from './types'

/** The icons an assistant can have (the server knows the same names). */
const icons: Record<string, LucideIcon> = {
  bot: Bot,
  sparkles: Sparkles,
  code: Code2,
  book: BookOpen,
  briefcase: Briefcase,
  flask: FlaskConical,
  shield: Shield,
  pen: PenLine,
  chart: ChartColumn,
  globe: Globe,
  graduation: GraduationCap,
  wrench: Wrench,
}

/** Its colours: a tint behind the icon, readable in both themes. Whole class names, so Tailwind keeps them. */
const colors: Record<string, string> = {
  blue: 'bg-blue-500/15 text-blue-700 dark:text-blue-300',
  green: 'bg-green-500/15 text-green-700 dark:text-green-300',
  amber: 'bg-amber-500/20 text-amber-800 dark:text-amber-300',
  red: 'bg-red-500/15 text-red-700 dark:text-red-300',
  violet: 'bg-violet-500/15 text-violet-700 dark:text-violet-300',
  pink: 'bg-pink-500/15 text-pink-700 dark:text-pink-300',
  teal: 'bg-teal-500/15 text-teal-700 dark:text-teal-300',
  slate: 'bg-slate-500/15 text-slate-700 dark:text-slate-300',
}

/** An assistant's icon on its colour. */
export function AssistantIcon({ icon, color, size = 'md', className }: { icon: string; color: string; size?: 'sm' | 'md' | 'lg'; className?: string }) {
  const Icon = icons[icon] ?? Bot
  return (
    <span
      className={cn(
        'inline-flex shrink-0 items-center justify-center rounded-lg',
        colors[color] ?? colors.blue,
        size === 'sm' ? 'size-5 rounded-md [&_svg]:size-3' : size === 'lg' ? 'size-11 [&_svg]:size-6' : 'size-8 [&_svg]:size-4',
        className,
      )}
      aria-hidden="true"
    >
      <Icon />
    </span>
  )
}

/** Choosing an assistant's icon and colour: radio buttons drawn as swatches. */
export function AssistantLook({ icon, color, onChange }: { icon: string; color: string; onChange: (look: { icon: string; color: string }) => void }) {
  return (
    <div className="grid gap-3">
      <fieldset className="flex flex-wrap gap-1.5">
        <legend className="sr-only">Icon</legend>
        {Object.entries(icons).map(([name, Icon]) => (
          <label
            key={name}
            className={cn(
              'flex size-9 cursor-pointer items-center justify-center rounded-lg border hover:bg-accent has-[:focus-visible]:ring-[3px] has-[:focus-visible]:ring-ring',
              name === icon && 'border-primary bg-primary/10 text-primary-ink',
            )}
          >
            <input type="radio" name="assistant-icon" className="sr-only" checked={name === icon} onChange={() => onChange({ icon: name, color })} aria-label={name} />
            <Icon className="size-4" aria-hidden="true" />
          </label>
        ))}
      </fieldset>
      <fieldset className="flex flex-wrap gap-1.5">
        <legend className="sr-only">Colour</legend>
        {Object.entries(colors).map(([name, tint]) => (
          <label
            key={name}
            className={cn('flex size-8 cursor-pointer items-center justify-center rounded-full has-[:focus-visible]:ring-[3px] has-[:focus-visible]:ring-ring', tint, name === color && 'ring-2 ring-current')}
          >
            <input type="radio" name="assistant-color" className="sr-only" checked={name === color} onChange={() => onChange({ icon, color: name })} aria-label={name} />
            {name === color && <Check className="size-4" aria-hidden="true" />}
          </label>
        ))}
      </fieldset>
    </div>
  )
}

/** Who may use an assistant, in words and with its icon. */
export function ReachLabel({ reach }: { reach: Reach }) {
  const [Icon, text] = reach === 'Company' ? [Building2, 'Everyone'] : reach === 'Groups' ? [Users, 'Groups'] : [Lock, 'Private']
  return (
    <span className="inline-flex items-center gap-1">
      <Icon className="size-3" aria-hidden="true" /> {text}
    </span>
  )
}
