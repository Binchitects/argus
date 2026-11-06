import { Download, FileSpreadsheet, FileText, Sheet } from 'lucide-react'
import { useId, useState } from 'react'
import { Button } from '@/components/ui/button'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Popover, PopoverAnchor, PopoverContent } from '@/components/ui/popover'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { dayOf, rangeKey, rangeLabel, reportPresets, resolveRange, type Preset, type TimeRange } from '@/lib/time'
import { customProblem } from '@/lib/use-time-window'

const CUSTOM = '__custom'

/**
 * The time window's control: its presets, the window shown when it is none of them (a link's), and Custom range, two
 * days in a popover (the last one included). Keyboard and screen readers have the select's own behaviour.
 */
export function TimeWindow({
  value,
  onChange,
  presets = reportPresets,
  className = 'h-9 w-auto min-w-44 max-w-full',
}: {
  value: TimeRange
  onChange: (r: TimeRange) => void
  presets?: Preset[]
  className?: string
}) {
  const id = useId()
  const [open, setOpen] = useState(false)
  const current = resolveRange(value)
  const [from, setFrom] = useState(() => dayOf(current.from))
  const [to, setTo] = useState(() => dayOf(new Date(current.to.getTime() - 1)))
  const options = presets.map((p) => ({ value: rangeKey({ from: p.from, to: p.to ?? 'now' }), label: p.label }))
  const key = rangeKey(value)
  const shown = options.some((o) => o.value === key) ? options : [...options, { value: key, label: rangeLabel(value) }]
  const problem = customProblem(from, to)
  const choose = (v: string) => {
    if (v === CUSTOM) {
      const r = resolveRange(value)
      setFrom(dayOf(r.from))
      setTo(dayOf(new Date(r.to.getTime() - 1)))
      setOpen(true)
      return
    }
    const [f = '', t = 'now'] = v.split('|')
    onChange({ from: f, to: t })
  }
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverAnchor asChild>
        <div className="inline-flex max-w-full">
          <Select value={key} onValueChange={choose}>
            <SelectTrigger className={className} aria-label="Time range">
              <SelectValue placeholder="Time range" />
            </SelectTrigger>
            <SelectContent>
              {shown.map((o) => (
                <SelectItem key={o.value} value={o.value}>
                  {o.label}
                </SelectItem>
              ))}
              <SelectItem value={CUSTOM}>Custom range…</SelectItem>
            </SelectContent>
          </Select>
        </div>
      </PopoverAnchor>
      <PopoverContent align="start" className="w-80">
        <form
          className="grid gap-3"
          aria-label="Custom range"
          onSubmit={(e) => {
            e.preventDefault()
            if (problem) return
            onChange({ from, to })
            setOpen(false)
          }}
        >
          <div className="grid grid-cols-2 gap-2">
            <div className="grid gap-1">
              <Label htmlFor={`${id}-from`} className="text-xs">
                First day
              </Label>
              <Input id={`${id}-from`} type="date" className="h-9" value={from} max={to || undefined} onChange={(e) => setFrom(e.target.value)} />
            </div>
            <div className="grid gap-1">
              <Label htmlFor={`${id}-to`} className="text-xs">
                Last day
              </Label>
              <Input id={`${id}-to`} type="date" className="h-9" value={to} min={from || undefined} onChange={(e) => setTo(e.target.value)} />
            </div>
          </div>
          <p className={problem ? 'text-xs text-destructive' : 'text-xs text-muted-foreground'} role={problem ? 'alert' : undefined}>
            {problem ?? 'Both days are in it, from the first one’s start to the last one’s end, in your time zone.'}
          </p>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" size="sm" onClick={() => setOpen(false)}>
              Cancel
            </Button>
            <Button type="submit" size="sm" disabled={!!problem}>
              Apply
            </Button>
          </div>
        </form>
      </PopoverContent>
    </Popover>
  )
}

/** An export of a report for the window: PDF, Excel or CSV, each a download from <paramref name="path"/>. */
export function ExportMenu({ path, query, label = 'Export', disabled }: { path: string; query: () => URLSearchParams; label?: string; disabled?: boolean }) {
  const href = (format: string) => {
    const q = query()
    q.set('format', format)
    return `${path}?${q}`
  }
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="outline" size="sm" className="h-9" disabled={disabled}>
          <Download /> {label}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-60">
        <DropdownMenuLabel className="text-xs font-normal text-muted-foreground">For the time window shown</DropdownMenuLabel>
        <DropdownMenuSeparator />
        <DropdownMenuItem asChild>
          <a href={href('pdf')} download>
            <FileText /> PDF report
          </a>
        </DropdownMenuItem>
        <DropdownMenuItem asChild>
          <a href={href('xlsx')} download>
            <FileSpreadsheet /> Excel workbook (.xlsx)
          </a>
        </DropdownMenuItem>
        <DropdownMenuItem asChild>
          <a href={href('csv')} download>
            <Sheet /> CSV
          </a>
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
