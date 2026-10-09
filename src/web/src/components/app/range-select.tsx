import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { metricPresets } from '@/lib/time'

/** A time range ending now, from five minutes to a month (live metrics and logs), or the ranges given. */
export function RangeSelect({
  value,
  onChange,
  className = 'h-9 w-44',
  options = metricPresets.map((p) => ({ value: p.from, label: p.label })),
}: {
  value: string
  onChange: (from: string) => void
  className?: string
  options?: { value: string; label: string }[]
}) {
  return (
    <Select value={value} onValueChange={onChange}>
      <SelectTrigger className={className} aria-label="Time range">
        <SelectValue placeholder="Time range" />
      </SelectTrigger>
      <SelectContent>
        {options.map((p) => (
          <SelectItem key={p.value} value={p.value}>
            {p.label}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  )
}
