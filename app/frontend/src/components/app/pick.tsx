import { Check, ChevronDown } from 'lucide-react'
import { useId, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { cn } from '@/lib/utils'

/**
 * A labelled list to choose one or several from, with an optional "All" row
 * (`allValue`) and a filter box once the list is long. Choosing nothing in a
 * multi list falls back to All when there is one.
 */
export function Pick({ label, options, chosen, onChange, multi = false, allValue, allLabel = 'All', error, empty = 'Nothing to choose from.', className }: {
  label: string
  options: string[]
  chosen: string[]
  onChange: (values: string[]) => void
  multi?: boolean
  allValue?: string
  allLabel?: string
  error?: string | null
  empty?: string
  className?: string
}) {
  const id = useId()
  const [filter, setFilter] = useState('')
  const all = allValue !== undefined && chosen.includes(allValue)
  const text = all ? allLabel : chosen.length === 0 ? 'None' : chosen.length > 2 ? `${chosen.slice(0, 2).join(', ')} +${chosen.length - 2}` : chosen.join(', ')
  const toggle = (option: string) => {
    if (!multi) return onChange([option])
    if (option === allValue) return onChange([option])
    const without = chosen.filter((c) => c !== allValue)
    const next = without.includes(option) ? without.filter((c) => c !== option) : [...without, option]
    onChange(next.length ? next : allValue !== undefined ? [allValue] : [])
  }
  const needle = filter.trim().toLowerCase()
  const shown = needle ? options.filter((o) => o.toLowerCase().includes(needle)) : options
  const rows = [...(allValue !== undefined && !needle ? [allValue] : []), ...shown]
  return (
    <div className={cn('grid gap-1', className)}>
      <Label htmlFor={id} className="text-xs text-muted-foreground">
        {label}
      </Label>
      <Popover onOpenChange={(open) => !open && setFilter('')}>
        <PopoverTrigger asChild>
          <Button id={id} variant="outline" size="sm" className="h-9 max-w-64 justify-between gap-2 font-normal" title={error ?? undefined}>
            <span className="truncate">{text}</span>
            <ChevronDown className="opacity-60" />
          </Button>
        </PopoverTrigger>
        <PopoverContent align="start" className="max-h-80 w-64 overflow-y-auto p-1">
          {error && <p className="px-2 py-1.5 text-xs text-destructive-ink">{error}</p>}
          {options.length > 10 && (
            <Input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Filter" aria-label={`Filter ${label.toLowerCase()}`} className="mb-1 h-8" autoComplete="off" />
          )}
          <div role="menu" aria-label={label}>
            {rows.map((o) => {
              const on = o === allValue ? all : chosen.includes(o)
              return (
                <button
                  key={o}
                  type="button"
                  role={multi ? 'menuitemcheckbox' : 'menuitemradio'}
                  aria-checked={on}
                  onClick={() => toggle(o)}
                  className="flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-left text-sm outline-none hover:bg-accent focus-visible:bg-accent"
                >
                  {multi ? (
                    <span className="flex size-4 shrink-0 items-center justify-center rounded-[4px] border border-input" aria-hidden="true">
                      {on && <Check className="size-3" />}
                    </span>
                  ) : (
                    <Check className={on ? 'size-4' : 'size-4 opacity-0'} aria-hidden="true" />
                  )}
                  <span className="truncate">{o === allValue ? allLabel : o}</span>
                </button>
              )
            })}
          </div>
          {!error && shown.length === 0 && <p className="px-2 py-1.5 text-xs text-muted-foreground">{needle ? 'No match.' : empty}</p>}
        </PopoverContent>
      </Popover>
    </div>
  )
}
