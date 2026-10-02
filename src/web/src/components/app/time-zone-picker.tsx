import { Check, ChevronsUpDown, Globe } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Command, CommandEmpty, CommandInput, CommandItem, CommandList } from '@/components/ui/command'
import { useFieldControl } from '@/components/ui/field'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { cn } from '@/lib/utils'

/** "UTC+03:30": a zone's offset from UTC now. */
function offsetOf(zone: string, at = new Date()): string {
  try {
    const part = new Intl.DateTimeFormat('en-US', { timeZone: zone, timeZoneName: 'longOffset' }).formatToParts(at).find((p) => p.type === 'timeZoneName')?.value
    return !part || part === 'GMT' ? 'UTC' : part.replace('GMT', 'UTC')
  } catch {
    return ''
  }
}

/** Every IANA zone the browser knows, UTC first; a zone saved but unknown here still shows. */
function zonesWith(current: string): string[] {
  const all = typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : []
  return [...new Set(['UTC', current, ...all].filter(Boolean))]
}

/**
 * A time zone, chosen from a searchable list (by city, region or offset), each with its
 * offset now. Inside a <Field>, the button takes the field's label.
 */
export function TimeZonePicker({ value, onChange, className }: { value: string; onChange: (zone: string) => void; className?: string }) {
  const [open, setOpen] = useState(false)
  const field = useFieldControl()
  const zones = useMemo(() => zonesWith(value).map((z) => ({ zone: z, offset: offsetOf(z) })), [value])
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button type="button" variant="outline" {...field} className={cn('h-9 w-full justify-between px-3 font-normal', className)}>
          <span className="flex min-w-0 items-center gap-2">
            <Globe className="text-muted-foreground" aria-hidden="true" />
            <span className="truncate">{value || 'Choose a time zone'}</span>
            {value && <span className="shrink-0 text-xs text-muted-foreground tabular-nums">{offsetOf(value)}</span>}
          </span>
          <ChevronsUpDown className="opacity-50" aria-hidden="true" />
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-[min(22rem,calc(100vw-2rem))] p-0" aria-label="Time zones">
        <Command filter={(item, search) => (item.toLowerCase().replace(/_/g, ' ').includes(search.toLowerCase().replace(/_/g, ' ')) ? 1 : 0)}>
          <CommandInput placeholder="City, region or UTC+03:30" aria-label="Find a time zone" />
          <CommandList>
            <CommandEmpty>No time zone matches.</CommandEmpty>
            {zones.map(({ zone, offset }) => (
              <CommandItem
                key={zone}
                value={`${zone} ${offset}`}
                onSelect={() => {
                  onChange(zone)
                  setOpen(false)
                }}
              >
                <Check className={cn(zone === value ? 'opacity-100' : 'opacity-0')} aria-hidden="true" />
                <span className="min-w-0 flex-1 truncate">{zone.replace(/_/g, ' ')}</span>
                <span className="text-xs text-muted-foreground tabular-nums">{offset}</span>
              </CommandItem>
            ))}
          </CommandList>
        </Command>
      </PopoverContent>
    </Popover>
  )
}
