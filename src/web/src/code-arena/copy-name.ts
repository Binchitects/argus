/**
 * A copy's name beside the original, as VS Code makes it: "name copy.ext", then "name copy 2.ext" and on (a folder, or a
 * name with no extension: "name copy").
 */
export function copyName(name: string, taken: ReadonlySet<string>, folder = false): string {
  const dot = folder ? -1 : name.lastIndexOf('.')
  const [base, ext] = dot > 0 ? [name.slice(0, dot), name.slice(dot)] : [name, '']
  for (let n = 1; ; n++) {
    const candidate = `${base} copy${n === 1 ? '' : ` ${n}`}${ext}`
    if (!taken.has(candidate)) return candidate
  }
}
