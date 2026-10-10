import 'katex/dist/katex.min.css'
import { memo, use, useLayoutEffect, useMemo, useRef } from 'react'
import { AnswerTable } from './answer-table'
import { CodeBlock } from './code-block'
import { FileRefs, openRef, tagRefs } from './file-refs'
import { toBlocks } from './markdown-blocks'

/**
 * A model's answer (or the person's question) as formatted text. While it is being
 * written (live), its last block may be half a diagram: that one is drawn once it is done.
 */
export const Markdown = memo(function Markdown({ text, onOpenFile, onPreview, live }: { text: string; onOpenFile?: (name: string) => void; onPreview?: (code: string) => void; live?: boolean }) {
  const blocks = useMemo(() => toBlocks(text), [text])
  // In Code Arena's IDE, the folder's files cited are links that open in its editor.
  const refs = use(FileRefs)
  const box = useRef<HTMLDivElement>(null)
  useLayoutEffect(() => {
    const el = box.current
    if (!refs || !el) return
    tagRefs(el, refs)
    // A part drawn again by itself (a table sorted or filtered) gets its links again.
    let soon = 0
    const watch = new MutationObserver(() => {
      cancelAnimationFrame(soon)
      soon = requestAnimationFrame(() => tagRefs(el, refs))
    })
    watch.observe(el, { childList: true, subtree: true })
    return () => {
      cancelAnimationFrame(soon)
      watch.disconnect()
    }
  }, [blocks, refs])
  // One listener for every citation in it (they are tagged in its HTML, not drawn by React).
  useLayoutEffect(() => {
    const el = box.current
    if (!refs || !el) return
    const click = (e: MouseEvent) => openRef(e, refs)
    const key = (e: KeyboardEvent) => e.key === 'Enter' && openRef(e, refs)
    el.addEventListener('click', click)
    el.addEventListener('keydown', key)
    return () => {
      el.removeEventListener('click', click)
      el.removeEventListener('keydown', key)
    }
  }, [refs])
  return (
    <div ref={box} className="md">
      {blocks.map((b, i) =>
        b.kind === 'code' ? (
          <CodeBlock key={i} code={b.code} lang={b.lang} name={b.name} onOpen={onOpenFile} preview={b.preview} onPreview={onPreview && b.preview ? () => onPreview(b.code) : undefined} draw={!live || i < blocks.length - 1} />
        ) : b.kind === 'table' ? (
          <AnswerTable key={i} head={b.head} align={b.align} rows={b.rows} />
        ) : (
          // Sanitised above; the answer is data, never markup we trust.
          <div key={i} dangerouslySetInnerHTML={{ __html: b.html }} />
        ),
      )}
    </div>
  )
})
