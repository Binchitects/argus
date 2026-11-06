import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { ContextWindowView, type ContextWindowData, type Freeable } from '@/pages/chat/context-window'
import { changeSettings, stateQuery, type CodeState } from './api'
import { api } from './http'

/** What fills the next request, as Code Arena builds it (GET /api/context): as before the turn while one runs. */
interface CodeWindowAnswer {
  window: ContextWindowData | null
  running: boolean
}

const codeWindowQuery = (stamp: number | string) => ({
  queryKey: ['code', 'context', stamp] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<CodeWindowAnswer>('/api/context', { signal }),
})

/**
 * Code Arena's view of what fills the window, piece by piece, as the web chat's Context panel shows its own: counted by
 * Arena with the model's tokenizer, else estimated. A server's tools never called turn off for this session from here.
 */
function CodeContextWindow({ state, onCompact }: { state: CodeState; onCompact?: () => void }) {
  const queryClient = useQueryClient()
  // Read again with each turn and each change of the session's settings.
  const query = useQuery(codeWindowQuery(`${state.turn ?? 0}-${state.contextUsed}-${state.model}`))
  const [acting, setActing] = useState(false)
  const free = async (f: Freeable) => {
    if (f.action === 'compact') {
      onCompact?.()
      return
    }
    if (f.action !== 'tool-off' || !f.ref) return
    setActing(true)
    try {
      const on = (state.toolGroups ?? []).filter((g) => g.on && g.id !== f.ref).map((g) => g.id)
      queryClient.setQueryData(stateQuery.queryKey, await changeSettings({ tools: on }))
      await queryClient.invalidateQueries({ queryKey: ['code', 'context'] })
      toast.success(`${f.ref} is off in this session: its definitions leave the next request.`)
    } catch (e) {
      toast.error(errorMessage(e))
    } finally {
      setActing(false)
    }
  }
  if (query.isPending) {
    return (
      <div className="grid gap-2" aria-busy="true" aria-label="Reading what fills the window">
        <Skeleton className="h-4 w-40" />
        <Skeleton className="h-3 w-full" />
        <Skeleton className="h-24 w-full" />
      </div>
    )
  }
  if (query.isError) {
    return (
      <p role="alert" className="text-sm text-destructive">
        {errorMessage(query.error)}
      </p>
    )
  }
  if (!query.data.window) return <p className="text-sm text-muted-foreground">What fills the window is shown once the session has started.</p>
  return (
    <ContextWindowView
      window={query.data.window}
      busy={acting || query.data.running}
      onFree={(f) => void free(f)}
      footer={
        <p className="text-xs text-muted-foreground">
          {query.data.running ? 'A turn runs: this is the window as it was before it. ' : ''}A big result is kept aside (the model reads its start and end, and read_result the rest); before compacting, earlier results are cleared to a note.
        </p>
      }
    />
  )
}

/** The chat's Context panel in the IDE (the gauge's What fills it, or /context). */
export function CodeContextSheet({ open, onOpenChange, state, onCompact }: { open: boolean; onOpenChange: (open: boolean) => void; state: CodeState; onCompact?: () => void }) {
  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent className="w-full overflow-y-auto text-foreground sm:max-w-lg" aria-describedby="code-context-about">
        <SheetHeader>
          <SheetTitle>Context</SheetTitle>
          <SheetDescription id="code-context-about">What fills the model's window in the next request ({state.model}): each part, its pieces and their tokens.</SheetDescription>
        </SheetHeader>
        <div className="px-5 pb-5">
          {open && (
            <CodeContextWindow
              state={state}
              onCompact={
                onCompact &&
                (() => {
                  onOpenChange(false)
                  onCompact()
                })
              }
            />
          )}
        </div>
      </SheetContent>
    </Sheet>
  )
}
