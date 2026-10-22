import { ChevronDown, SquareSlash } from 'lucide-react'
import { useId, useState } from 'react'
import { parameterLabel, type SkillCall } from '@/lib/skills'
import { cn } from '@/lib/utils'

/**
 * The skill a question used, above its words: its name and title, the parameters it was given, and its instructions
 * (as the model read them, filled in) on demand.
 */
export function SkillUsed({ skill }: { skill: SkillCall }) {
  const [open, setOpen] = useState(false)
  const id = useId()
  const args = Object.entries(skill.args).filter(([, v]) => v)
  return (
    <figure className="grid max-w-[85%] min-w-0 justify-items-end gap-1 text-sm" aria-label={`Used the skill /${skill.name}`}>
      <div className="flex min-w-0 flex-wrap items-center justify-end gap-1.5">
        <span className="inline-flex shrink-0 items-center gap-1.5 rounded-md bg-primary/10 px-2 py-0.5 text-primary-ink">
          <SquareSlash className="size-3.5" aria-hidden="true" />
          <span className="font-mono text-[0.8125rem] font-medium">{skill.name}</span>
        </span>
        <span dir="auto" className="min-w-0 truncate text-muted-foreground">
          {skill.title}
        </span>
        {skill.instructions && (
          <button
            type="button"
            className="inline-flex items-center gap-0.5 rounded text-xs text-muted-foreground underline-offset-2 outline-none hover:underline focus-visible:ring-[3px] focus-visible:ring-ring"
            aria-expanded={open}
            aria-controls={id}
            onClick={() => setOpen(!open)}
          >
            Instructions
            <ChevronDown className={cn('size-3 transition-transform', open && 'rotate-180')} aria-hidden="true" />
          </button>
        )}
      </div>
      {args.length > 0 && (
        <dl className="flex min-w-0 flex-wrap justify-end gap-1.5 text-xs">
          {args.map(([k, v]) => (
            <div key={k} className="flex min-w-0 max-w-full gap-1 rounded-md border bg-card px-1.5 py-0.5">
              <dt className="shrink-0 text-muted-foreground">{parameterLabel(k)}</dt>
              <dd dir="auto" className="min-w-0 truncate">
                {v}
              </dd>
            </div>
          ))}
        </dl>
      )}
      {open && (
        <pre id={id} dir="auto" className="max-h-64 w-full overflow-auto rounded-lg border bg-muted/40 px-3 py-2 font-mono text-xs whitespace-pre-wrap [overflow-wrap:anywhere]">
          {skill.instructions}
        </pre>
      )}
    </figure>
  )
}
