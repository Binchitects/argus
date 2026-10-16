import { describe, expect, it } from 'vitest'
import { attachedOf, typedOf } from './attached'

// As Code Arena writes a question with @files and lines chosen in its editor (src/CodeArena/Mentions.cs).
const asked =
  'Why does this fail?' +
  '\n\n<attached>' +
  '\n<file path="src/app.ts">\nconst a = 1\n// <file path="not/a/block.ts"> inside the file is its text\n</file>' +
  '\n<selection path="src/app.ts" lines="3-3">\nreturn a\n</selection>' +
  '\n<selection path="src/util.ts" lines="10-20">\nexport const b = 2\n</selection>' +
  '\n<file path="logo.png">(a binary file: 2 KB)</file>' +
  '\n</attached>'

describe('what went with a question asked in Code Arena', () => {
  it('is shown apart from what was typed, each file or selection once, as path or path:lines', () => {
    expect(typedOf(asked)).toBe('Why does this fail?')
    expect(attachedOf(asked)).toEqual(['src/app.ts', 'src/app.ts:3', 'src/util.ts:10-20', 'logo.png'])
  })

  it('leaves a message without it as it is', () => {
    expect(typedOf('Just a question <file path="x">')).toBe('Just a question <file path="x">')
    expect(attachedOf('Just a question')).toEqual([])
  })
})
