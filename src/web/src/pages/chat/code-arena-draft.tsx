import { useQuery } from '@tanstack/react-query'
import { SquareTerminal } from 'lucide-react'
import { api } from '@/lib/api'
import { Markdown } from './markdown'
import { Thinking } from './parts'

export interface CodeArenaDraftView {
  text: string | null
  reasoning: string | null
  at: string | null
}

/** The query of what Code Arena is writing in a chat: read again when the chat's watch says it changed ("draft"). */
export const codeArenaDraftQuery = (id: string) => ({
  queryKey: ['chat', 'draft', id],
  queryFn: ({ signal }: { signal: AbortSignal }) => api<CodeArenaDraftView>(`/api/chat/conversations/${id}/code-arena-draft`, { signal }),
})

/**
 * The answer Code Arena is writing in this chat, as it last sent it (every few seconds): its thinking and its words, at
 * the end of the thread, while its turn holds the chat. Its message takes its place once the answer is in.
 */
export function CodeArenaDraft({ chatId, on }: { chatId: string; on: boolean }) {
  // Asked at once and whenever the watch says it moved; every 10 s too, in case the watch is not open.
  const draft = useQuery({ ...codeArenaDraftQuery(chatId), enabled: on, refetchInterval: on ? 10_000 : false })
  const d = on ? draft.data : null
  if (!d || (!d.text && !d.reasoning)) return null
  return (
    <section aria-label="Code Arena's answer as it comes" className="grid min-w-0 gap-2" aria-busy="true">
      <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
        <SquareTerminal className="size-3.5 shrink-0 animate-pulse" aria-hidden="true" /> Code Arena is writing this answer
      </p>
      {d.reasoning && <Thinking text={d.reasoning} live={!d.text} ms={null} since={null} />}
      {d.text && <Markdown text={d.text} live />}
    </section>
  )
}
