import 'katex/dist/katex.min.css'
import { memo, useMemo } from 'react'
import { AnswerTable } from './answer-table'
import { CodeBlock } from './code-block'
import { toBlocks } from './markdown-blocks'

/**
 * A model's answer (or the person's question) as formatted text. While it is being
 * written (live), its last block may be half a diagram: that one is drawn once it is done.
 */
export const Markdown = memo(function Markdown({ text, onOpenFile, onPreview, live }: { text: string; onOpenFile?: (name: string) => void; onPreview?: (code: string) => void; live?: boolean }) {
  const blocks = useMemo(() => toBlocks(text), [text])
  return (
    <div className="md">
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
