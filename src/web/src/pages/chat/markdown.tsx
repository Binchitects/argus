import 'katex/dist/katex.min.css'
import { memo, useMemo } from 'react'
import { CodeBlock } from './code-block'
import { toBlocks } from './markdown-blocks'

/** A model's answer (or the person's question) as formatted text. */
export const Markdown = memo(function Markdown({ text, onOpenFile, onPreview }: { text: string; onOpenFile?: (name: string) => void; onPreview?: (code: string) => void }) {
  const blocks = useMemo(() => toBlocks(text), [text])
  return (
    <div className="md">
      {blocks.map((b, i) =>
        b.kind === 'code' ? (
          <CodeBlock key={i} code={b.code} lang={b.lang} name={b.name} onOpen={onOpenFile} preview={b.preview} onPreview={onPreview && b.preview ? () => onPreview(b.code) : undefined} />
        ) : (
          // Sanitised above; the answer is data, never markup we trust.
          <div key={i} dangerouslySetInnerHTML={{ __html: b.html }} />
        ),
      )}
    </div>
  )
})
