import { describe, expect, it } from 'vitest'
import { globMatch } from './glob'

describe('files.associations patterns', () => {
  it('reads * ? and ** as VS Code does', () => {
    expect(globMatch('*.inc', 'config.inc')).toBe(true)
    expect(globMatch('*.inc', 'a/config.inc')).toBe(false)
    expect(globMatch('**/build/*.txt', 'src/build/out.txt')).toBe(true)
    expect(globMatch('**/build/*.txt', 'build/out.txt')).toBe(true)
    expect(globMatch('**/build/*.txt', 'src/build/deep/out.txt')).toBe(false)
    expect(globMatch('Dockerfile.?', 'dockerfile.x')).toBe(true)
    expect(globMatch('docs/**', 'docs/a/b.md')).toBe(true)
  })

  it('takes no time whatever the pattern', () => {
    const started = performance.now()
    expect(globMatch('**/**/**/**/**/**/**/**/**/**/**/**/x', 'a/'.repeat(40) + 'y')).toBe(false)
    expect(globMatch('*a*a*a*a*a*a*a*a*a*a*a*b', 'a'.repeat(200))).toBe(false)
    expect(performance.now() - started).toBeLessThan(500)
  })
})
