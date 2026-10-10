import { describe, expect, it } from 'vitest'
import { findRefs, openRef, parseRef, refLabel, tagRefs, type FileRef, type FileRefsApi } from './file-refs'

describe('citations of files in answers', () => {
  it('reads the ways a file and its lines are cited', () => {
    expect(parseRef('src/app.ts')).toEqual({ path: 'src/app.ts' })
    expect(parseRef('src/app.ts:12')).toEqual({ path: 'src/app.ts', line: 12 })
    expect(parseRef('src/app.ts:12-30')).toEqual({ path: 'src/app.ts', line: 12, end: 30 })
    expect(parseRef('src/app.ts:12:5')).toEqual({ path: 'src/app.ts', line: 12, column: 5 })
    expect(parseRef('src/app.ts#L12-L30')).toEqual({ path: 'src/app.ts', line: 12, end: 30 })
    expect(parseRef('Program.cs(44,13)')).toEqual({ path: 'Program.cs', line: 44, column: 13 })
    expect(parseRef('src/app.ts:30-12')).toEqual({ path: 'src/app.ts', line: 30 })
    expect(parseRef('https://example.test/a.ts:3')).toBeNull()
    expect(parseRef('two words')).toBeNull()
    expect(refLabel({ path: 'a.ts', line: 3, end: 9 })).toBe('a.ts:3-9')
  })

  it("links only the folder's files, in code, links and text, and never inside a code block", () => {
    const refs: FileRefsApi = { known: (p) => (p === 'src/app.ts' ? p : null), open: () => undefined }
    const root = document.createElement('div')
    root.innerHTML = '<p>See <code>src/app.ts:4</code>, src/app.ts#L2 and <code>other.ts:1</code>.</p><pre><code>src/app.ts:9</code></pre>'
    tagRefs(root, refs)
    expect([...root.querySelectorAll('[data-ref]')].map((e) => [e.tagName, e.textContent])).toEqual([
      ['CODE', 'src/app.ts:4'],
      ['SPAN', 'src/app.ts#L2'],
    ])
    expect(root.textContent).toBe('See src/app.ts:4, src/app.ts#L2 and other.ts:1.src/app.ts:9')
  })

  it('opens only the links it tagged, and reads a Windows path whole', () => {
    const opened: FileRef[] = []
    const refs: FileRefsApi = { known: (p) => (['src/app.ts', 'src/web/app.ts'].includes(p.replace(/\\/g, '/')) ? p.replace(/\\/g, '/') : null), open: (r) => opened.push(r) }
    const root = document.createElement('div')
    // An answer's own data-ref (kept by the sanitiser) opens nothing.
    root.innerHTML = '<p><span data-ref=\'{"path":"../secret"}\'>forged</span> and <code>src/app.ts:3</code></p>'
    tagRefs(root, refs)
    openRef({ target: root.querySelector('span'), preventDefault: () => undefined }, refs)
    expect(opened).toEqual([])
    openRef({ target: root.querySelector('code'), preventDefault: () => undefined }, refs)
    expect(opened).toEqual([{ path: 'src/app.ts', line: 3 }])
    expect(findRefs('see src\\web\\app.ts:4 here', refs).map((f) => [f.text, f.ref.path])).toEqual([['src\\web\\app.ts:4', 'src/web/app.ts']])
  })
})
