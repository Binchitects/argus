import { describe, expect, it } from 'vitest'
import { problemFilter, problemLine, type Problem } from './problems-store'

const p = (path: string, message: string, code: string | null = null, severity: Problem['severity'] = 'error'): Problem =>
  ({ source: 'check', path, line: 3, column: 7, severity, code, message })

describe("Problems' filter", () => {
  const all = [p('src/app.ts', "Cannot find name 'totl'.", 'TS2304'), p('src/lib/cart.ts', 'Unused import.', null, 'warning'), p('README.md', 'Line too long.', 'MD013')]
  const kept = (filter: string) => all.filter(problemFilter(filter)).map((x) => x.path)

  it('keeps everything when empty, and matches the message, the code or the file as text, in any case', () => {
    expect(kept('  ')).toEqual(['src/app.ts', 'src/lib/cart.ts', 'README.md'])
    expect(kept('UNUSED')).toEqual(['src/lib/cart.ts'])
    expect(kept('ts2304')).toEqual(['src/app.ts'])
    expect(kept('readme')).toEqual(['README.md'])
  })

  it('reads a term with * ? or / as a glob of the path, or of the name when it has no slash', () => {
    expect(kept('**/*.ts')).toEqual(['src/app.ts', 'src/lib/cart.ts'])
    expect(kept('*.md')).toEqual(['README.md'])
    expect(kept('src/lib/**')).toEqual(['src/lib/cart.ts'])
  })

  it('leaves out what a ! term matches, and keeps what any other term matches', () => {
    expect(kept('!**/lib/**')).toEqual(['src/app.ts', 'README.md'])
    expect(kept('*.ts, !TS2304')).toEqual(['src/lib/cart.ts'])
    expect(kept('unused, line')).toEqual(['src/lib/cart.ts', 'README.md'])
  })

  it('copies a problem as where it is, how bad, its code and what it says', () => {
    expect(problemLine(all[0]!)).toBe("src/app.ts:3:7 error TS2304 Cannot find name 'totl'.")
    expect(problemLine(all[1]!)).toBe('src/lib/cart.ts:3:7 warning Unused import.')
  })
})
