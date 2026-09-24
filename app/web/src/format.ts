export const money = (n: number | null | undefined) =>
  n === null || n === undefined ? 'unlimited' : `$${n.toFixed(n < 1 ? 4 : 2)}`

export const when = (iso: string | number | null | undefined) => {
  if (iso === null || iso === undefined) return 'never'
  const d = typeof iso === 'number' ? new Date(iso * 1000) : new Date(iso)
  return d.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
}
