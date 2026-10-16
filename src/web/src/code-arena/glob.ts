/**
 * A file name or path against a glob, as VS Code's files.associations reads them: `*` any characters but `/`, `?` one,
 * `**` any folders (a `**` followed by a slash: none or more); letters of any case. Worked out by positions, once each: no pattern can take
 * long, whatever a project's settings.json holds.
 */
export function globMatch(pattern: string, text: string): boolean {
  const p = pattern.toLowerCase()
  const t = text.toLowerCase()
  const seen = new Map<number, boolean>()
  const go = (i: number, j: number): boolean => {
    const key = i * (t.length + 1) + j
    const known = seen.get(key)
    if (known !== undefined) return known
    let ok: boolean
    if (i === p.length) ok = j === t.length
    else if (p.startsWith('**/', i)) {
      // None or more whole folders.
      ok = go(i + 3, j)
      for (let k = j; !ok && k < t.length; k++) if (t[k] === '/') ok = go(i + 3, k + 1)
    } else if (p.startsWith('**', i)) ok = true
    else if (p[i] === '*') ok = go(i + 1, j) || (j < t.length && t[j] !== '/' && go(i, j + 1))
    else if (p[i] === '?') ok = j < t.length && t[j] !== '/' && go(i + 1, j + 1)
    else ok = j < t.length && p[i] === t[j] && go(i + 1, j + 1)
    seen.set(key, ok)
    return ok
  }
  return go(0, 0)
}
