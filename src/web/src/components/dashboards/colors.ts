import type { Threshold } from './types'

/** A threshold colour as the app's colours: status first (never a series colour). */
export function tone(color: string | undefined): { ink: string; fill: string; stroke: string } | null {
  switch ((color ?? '').toLowerCase()) {
    case 'red':
    case 'dark-red':
    case 'semi-dark-red':
      return { ink: 'text-destructive-ink', fill: 'bg-destructive/12', stroke: 'var(--destructive)' }
    case 'orange':
    case 'yellow':
    case 'dark-orange':
    case 'dark-yellow':
      return { ink: 'text-warning-ink', fill: 'bg-warning/15', stroke: 'var(--warning)' }
    case 'green':
    case 'dark-green':
    case 'semi-dark-green':
      return { ink: 'text-success-ink', fill: 'bg-success/12', stroke: 'var(--success)' }
    case 'blue':
    case 'purple':
      return { ink: 'text-primary-ink', fill: 'bg-primary/10', stroke: 'var(--primary)' }
    default:
      return null
  }
}

/** The step a value falls in: the last threshold at or below it. */
export function thresholdColor(value: number | null, steps: Threshold[] | undefined): string | undefined {
  if (value === null || !steps?.length) return undefined
  let color = steps[0]!.color
  for (const s of steps) if (s.value === null || value >= s.value) color = s.color
  return color
}
