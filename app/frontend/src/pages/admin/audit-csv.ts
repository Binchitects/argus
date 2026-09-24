import type { AuditEvent } from './audit'

/** CSV for a spreadsheet: quoted where needed, and never a formula. */
export function toCsv(rows: AuditEvent[]): string {
  const cell = (v: unknown) => {
    const s = v === null || v === undefined ? '' : String(v)
    // A leading = + - @ would run as a formula in a spreadsheet.
    const safe = /^[=+\-@]/.test(s) ? `'${s}` : s
    return /[",\n]/.test(safe) ? `"${safe.replace(/"/g, '""')}"` : safe
  }
  const head = ['at', 'actor', 'action', 'target', 'success', 'ip', 'detail']
  return [head.join(','), ...rows.map((r) => [r.at, r.actor, r.action, r.target, r.success, r.ip, r.detail].map(cell).join(','))].join('\n')
}
