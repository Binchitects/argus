import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { toast } from '@/components/ui/toaster'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { Skeleton } from '@/components/ui/skeleton'
import { api, errorMessage } from '@/lib/api'
import { ContextWindowView, type ContextWindowData, type Freeable } from './context-window'

/** What the server says fills the chat's window: its last answer's request, and whether it compacted since. */
export interface ChatContextWindow {
  window: ContextWindowData | null
  at?: string
  model?: string | null
  compactedSince: boolean
  /** The answer kept no record of its request (an older one, or one that failed): rebuilt from the chat, estimated. */
  rebuilt?: boolean
}

const contextWindowQuery = (chat: string, stamp: string | null | undefined) => ({
  queryKey: ['chat', 'context', chat, stamp ?? ''] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<ChatContextWindow>(`/api/chat/conversations/${chat}/context`, { signal }),
})

/**
 * The chat's Context panel (the gauge's "What fills it", or /context): what fills the model's window piece by piece, as
 * the last answer's request held it, and what could be freed: a tool never called turned off for this chat, a big result
 * or an earlier file left out, or the chat compacted now. What is done shows from the next answer on.
 */
export function ContextSheet({
  chat,
  stamp,
  open,
  onOpenChange,
  busy,
  onCompact,
  onToolOff,
}: {
  chat: string
  /** Changes with the chat's last answer: the panel reads it again. */
  stamp: string | null | undefined
  open: boolean
  onOpenChange: (open: boolean) => void
  busy: boolean
  onCompact?: () => void
  onToolOff?: (tool: string) => Promise<void>
}) {
  const queryClient = useQueryClient()
  const query = useQuery({ ...contextWindowQuery(chat, stamp), enabled: open })
  const [acting, setActing] = useState(false)
  const reread = () => Promise.all([queryClient.invalidateQueries({ queryKey: ['chat', 'context', chat] }), queryClient.invalidateQueries({ queryKey: ['chat', 'conversation', chat] })])
  const free = async (f: Freeable) => {
    if (f.action === 'compact') {
      onOpenChange(false)
      onCompact?.()
      return
    }
    setActing(true)
    try {
      if (f.action === 'tool-off' && f.ref && onToolOff) {
        await onToolOff(f.ref)
        toast.success(`${f.ref} is off for this chat: its definitions leave the next request.`)
      } else if (f.action === 'leave-out' && f.ref) {
        await api(`/api/chat/conversations/${chat}/messages/${f.ref}/context`, { method: 'PUT', body: { leftOut: true } })
        toast.success('Left out: the model does not read it from the next answer on. It stays in the chat.')
      } else if (f.action === 'drop-file' && f.ref && f.file) {
        await api(`/api/chat/conversations/${chat}/messages/${f.ref}/file-context`, { method: 'PUT', body: { file: f.file, leftOut: true } })
        toast.success(`${f.detail ?? 'The file'} is left out: the model reads its name only from the next answer on.`)
      }
      await reread()
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setActing(false)
    }
  }
  const data = query.data
  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent className="w-full overflow-y-auto sm:max-w-lg" aria-describedby="context-sheet-about">
        <SheetHeader>
          <SheetTitle>Context</SheetTitle>
          <SheetDescription id="context-sheet-about">
            What fills the model's window, as the last answer's request held it{data?.model ? ` (${data.model})` : ''}: each part, its pieces and their tokens.
          </SheetDescription>
        </SheetHeader>
        <div className="grid gap-4 px-5 pb-5">
          {query.isPending ? (
            <div className="grid gap-2" aria-busy="true" aria-label="Reading what fills the window">
              <Skeleton className="h-4 w-40" />
              <Skeleton className="h-3 w-full" />
              <Skeleton className="h-24 w-full" />
            </div>
          ) : query.isError ? (
            <p role="alert" className="text-destructive">
              {errorMessage(query.error)}
            </p>
          ) : !data?.window ? (
            <p className="text-muted-foreground">What fills the window is shown from the first answer on.</p>
          ) : (
            <>
              {data.rebuilt && (
                <p className="rounded-md border bg-muted/40 p-2 text-xs">
                  This answer kept no record of its request: what filled it is rebuilt from the chat, its messages piece by piece and the rest by how much it held.
                </p>
              )}
              {data.compactedSince && (
                <p className="rounded-md border bg-muted/40 p-2 text-xs">The chat compacted since this answer: the next request starts from the summary, and counts it.</p>
              )}
              <ContextWindowView
                window={data.window}
                busy={busy || acting}
                onFree={(f) => void free(f)}
                footer={<p className="text-xs text-muted-foreground">What you turn off or leave out here shows from the next answer on. Left out stays in the chat; pinned messages are kept word for word when it compacts.</p>}
              />
            </>
          )}
        </div>
      </SheetContent>
    </Sheet>
  )
}
