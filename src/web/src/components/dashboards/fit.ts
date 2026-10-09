import type { CSSProperties } from 'react'

/**
 * About how wide a value's text is, in em, in the app's font at a value's weight: a digit is 0.62em, a point or a
 * space a quarter, "%" and "W" a whole one. Rounded up, so text sized by it fits.
 */
export function ems(text: string): number {
  let sum = 0
  for (const c of text) {
    if (/[ .,:;·'!|ijlI]/.test(c)) sum += 0.25
    else if (/[°/()tfr]/.test(c)) sum += 0.45
    else if (/[%WMmw—]/.test(c)) sum += 1
    else if (/[A-Z]/.test(c)) sum += 0.72
    else if (/[a-z]/.test(c)) sum += 0.6
    else sum += 0.65
  }
  return Math.max(sum, 0.65)
}

/**
 * The font size at which text `em` wide (from ems) fits `room`, a CSS width such as `100cqi` (its own box): a number
 * with its unit in a narrow cell shrinks rather than running past its panel's border. Set as --fit, for a class to
 * cap at the usual size. A panel's values pass their widest, so they shrink alike.
 */
export function fitting(em: number, room: string): CSSProperties {
  return { '--fit': `calc(${room} / ${em.toFixed(2)})` } as CSSProperties
}
