import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { metricPresets } from '@/lib/time'

/** A time range ending now, from five minutes to a month: live metrics and logs. */
export function RangeSelect({ value, onChange, className = 'h-9 w-44' }: { value: string; onChange: (from: string) => void; className?: string }) {
  return (
    <Select value={value} onValueChange={onChange}>
      <SelectTrigger className={className} aria-label="Time range">
        <SelectValue placeholder="Time range" />
      </SelectTrigger>
      <SelectContent>
        {metricPresets.map((p) => (
          <SelectItem key={p.from} value={p.from}>
            {p.label}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  )
}
