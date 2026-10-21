import type { Pin } from './data-api'

/** The chart as a message in a room the person is in: its title, what it answers, its rows as a table. */
export function pinMessage(pin: Pin, board: string): string {
  const r = pin.result
  const lines = [`**${pin.title}**${pin.question && pin.question !== pin.title ? ` — ${pin.question}` : ''}`]
  if (r && r.columns.length > 0) {
    const cell = (v: unknown) => (v === null ? '' : String(v)).replaceAll('|', '\\|').replace(/\s+/g, ' ')
    lines.push('', `| ${r.columns.map((c) => cell(c.name)).join(' | ')} |`, `|${r.columns.map((c) => (c.type === 'number' ? ' ---: |' : ' --- |')).join('')}`)
    for (const row of r.rows.slice(0, 20)) lines.push(`| ${row.map(cell).join(' | ')} |`)
    if (r.rows.length > 20) lines.push('', `(The first 20 of ${r.rows.length} rows.)`)
  }
  lines.push('', `_From the board ${board}${pin.ranAt ? `, as of ${new Date(pin.ranAt).toLocaleString()}` : ''}._`)
  return lines.join('\n')
}
