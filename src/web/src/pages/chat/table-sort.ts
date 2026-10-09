import type { TableCell } from './markdown-blocks'

/** A number when the whole cell reads as one ("1,204", "35%", "$4.20", "-3"); else null, and the cell sorts as text. */
export function cellNumber(text: string): number | null {
  const t = text.trim().replace(/^[$€£¥]/, '').replace(/%$/, '').replaceAll(',', '')
  if (!/^[-+−]?(\d+\.?\d*|\.\d+)(e[-+]?\d+)?$/i.test(t)) return null
  return Number(t.replace('−', '-'))
}

const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' })

/** Rows in order of one column: numbers as numbers when both cells are numbers, else text as people read it (2 before 10). */
export function sortRows(rows: TableCell[][], column: number, desc: boolean): TableCell[][] {
  const sorted = [...rows].sort((a, b) => {
    const x = a[column]?.text ?? ''
    const y = b[column]?.text ?? ''
    const [nx, ny] = [cellNumber(x), cellNumber(y)]
    // Empty cells go last whichever way it sorts.
    if (!x.trim() || !y.trim()) return x.trim() ? -1 : y.trim() ? 1 : 0
    const order = nx !== null && ny !== null ? nx - ny : collator.compare(x, y)
    return desc ? -order : order
  })
  return sorted
}
