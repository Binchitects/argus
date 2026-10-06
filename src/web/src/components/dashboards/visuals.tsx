import { formatValue } from '@/lib/format'
import { cn } from '@/lib/utils'
import { thresholdColor, tone } from './colors'
import { ems, fitting } from './fit'
import type { Threshold } from './types'

/** A small line under a stat: how the value moved over the range. */
export function Sparkline({ points, className }: { points: (number | null)[][]; className?: string }) {
  const ys = points.map((p) => p[1]).filter((v): v is number => typeof v === 'number')
  if (ys.length < 2) return null
  const min = Math.min(...ys)
  const max = Math.max(...ys)
  const span = max - min || 1
  const step = 100 / (ys.length - 1)
  const d = ys.map((y, i) => `${i ? 'L' : 'M'}${(i * step).toFixed(2)},${(28 - ((y - min) / span) * 26).toFixed(2)}`).join('')
  return (
    <svg viewBox="0 0 100 30" preserveAspectRatio="none" className={cn('h-8 w-full', className)} aria-hidden="true">
      <path d={`${d}L100,30L0,30Z`} fill="currentColor" opacity="0.12" />
      <path d={d} fill="none" stroke="currentColor" strokeWidth="1.5" vectorEffect="non-scaling-stroke" />
    </svg>
  )
}

/**
 * A gauge: an arc from min to max, filled to the value in its threshold's
 * colour, with the threshold steps marked on the rim. Small when it shares its
 * panel with others (a gauge per GPU).
 */
export function Gauge({ value, min, max, steps, unit, decimals, label, small, em }: {
  value: number | null
  min: number
  max: number
  steps?: Threshold[]
  unit?: string
  decimals?: number
  label: string
  small?: boolean
  /** The widest caption among the gauges beside it, in em, so all of them are one size. */
  em?: number
}) {
  const ratio = value === null ? 0 : Math.min(1, Math.max(0, (value - min) / (max - min || 1)))
  const color = tone(thresholdColor(value, steps))?.stroke ?? 'var(--primary)'
  // A 240° arc, open at the bottom.
  const arc = (from: number, to: number) => {
    const a = (t: number) => ((150 + 240 * t) * Math.PI) / 180
    const p = (t: number) => [50 + 40 * Math.cos(a(t)), 52 + 40 * Math.sin(a(t))]
    const [x1, y1] = p(from)
    const [x2, y2] = p(to)
    return `M${x1!.toFixed(2)},${y1!.toFixed(2)} A40,40 0 ${to - from > 0.75 ? 1 : 0} 1 ${x2!.toFixed(2)},${y2!.toFixed(2)}`
  }
  const text = formatValue(value, unit, decimals)
  return (
    // The ring fills the figure, as wide as its cell up to its full size, so in a narrow cell all of it shrinks
    // together. The caption sits at the same place in it at any size, between the ring's two ends, which are 0.62 of
    // the width apart (100cqi: the figure's own width).
    <figure className={cn('relative aspect-[100/78] w-full @container', small ? 'max-w-[93px]' : 'max-w-[130px]')} aria-label={`${label}: ${text}`}>
      <svg viewBox="0 0 100 78" className="absolute inset-0 size-full" aria-hidden="true">
        <path d={arc(0, 1)} fill="none" stroke="var(--muted)" strokeWidth="8" strokeLinecap="round" />
        {ratio > 0 && <path d={arc(0, Math.max(ratio, 0.005))} fill="none" stroke={color} strokeWidth="8" strokeLinecap="round" />}
        {(steps ?? []).filter((s) => s.value !== null && s.value > min && s.value < max).map((s) => {
          const t = ((s.value as number) - min) / (max - min)
          const a = ((150 + 240 * t) * Math.PI) / 180
          return <line key={s.value} x1={50 + 46 * Math.cos(a)} y1={52 + 46 * Math.sin(a)} x2={50 + 50 * Math.cos(a)} y2={52 + 50 * Math.sin(a)} stroke={tone(s.color)?.stroke ?? 'var(--muted-foreground)'} strokeWidth="1.5" />
        })}
      </svg>
      <figcaption
        className={cn('absolute inset-x-0 top-[77%] -translate-y-1/2 text-center font-semibold whitespace-nowrap tabular-nums', small ? 'text-[length:min(1rem,var(--fit))]' : 'text-[length:min(1.25rem,var(--fit))]')}
        style={fitting(Math.max(ems(text), em ?? 0), '100cqi * 0.62')}
      >
        {text}
      </figcaption>
    </figure>
  )
}
