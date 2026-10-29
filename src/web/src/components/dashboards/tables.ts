import type { Column } from './types'

export interface Table {
  columns: Column[]
  rows: unknown[][]
}

/**
 * Grafana's "merge" transformation: several tables become one, a row of each joined to the row of another whose shared
 * text columns hold the same values (a GPU's utilisation, memory and temperature, each from its own query, on one line).
 * The queries' Time columns go: an instant query's time says nothing a reader needs.
 */
export function merge(tables: Table[]): Table {
  const columns: Column[] = []
  const at = new Map<string, number>()
  for (const t of tables)
    for (const c of t.columns) {
      if (c.type === 'time' || at.has(c.name)) continue
      at.set(c.name, columns.length)
      columns.push(c)
    }
  const rows: unknown[][] = []
  for (const t of tables) {
    const texts = t.columns.flatMap((c, i) => (c.type === 'string' ? [{ i, j: at.get(c.name)! }] : []))
    for (const row of t.rows) {
      // The first merged row whose shared text columns agree with this one, and which has none of its values yet.
      const into = rows.find((m) => texts.every(({ i, j }) => m[j] === undefined || m[j] === row[i]) && texts.some(({ j }) => m[j] !== undefined)
        && t.columns.every((c, i) => c.type !== 'number' || m[at.get(c.name)!] === undefined || row[i] === undefined))
      const target = into ?? Array.from({ length: columns.length }, () => undefined as unknown)
      t.columns.forEach((c, i) => {
        if (c.type !== 'time') target[at.get(c.name)!] = row[i]
      })
      if (!into) rows.push(target)
    }
  }
  return { columns, rows: rows.map((r) => r.map((v) => v ?? null)) }
}
