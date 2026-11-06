import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Bot, ChevronDown } from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { DropdownMenu, DropdownMenuContent, DropdownMenuLabel, DropdownMenuRadioGroup, DropdownMenuRadioItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { stateQuery, type CodeState } from './api'
import { assistantsQuery, chooseAssistant } from './tasks-api'

const NONE = 'none'

/**
 * The Arena assistant the session works as, in the chat's header: its instructions, model, thinking, Arena tools and skills.
 * Your assistants (yours, shared with you, the company's) are asked of Arena when the menu opens.
 */
export function AssistantPicker({ state }: { state: CodeState }) {
  const queryClient = useQueryClient()
  const [open, setOpen] = useState(false)
  const list = useQuery({ ...assistantsQuery, enabled: open })
  const current = state.assistant
  const choose = async (id: string) => {
    try {
      const { said } = await chooseAssistant(id === NONE ? null : id)
      toast.success(said)
      await queryClient.invalidateQueries({ queryKey: stateQuery.queryKey })
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }
  return (
    <DropdownMenu open={open} onOpenChange={setOpen}>
      <DropdownMenuTrigger asChild>
        <Button
          variant="ghost"
          size="sm"
          disabled={state.busy}
          aria-label={current ? `Assistant: ${current.name}` : 'Assistant: none'}
          className={cn('h-8 max-w-44 gap-1.5 px-2', !current && 'text-muted-foreground')}
        >
          <Bot />
          <span className="hidden truncate @md:inline">{current?.name ?? 'Assistant'}</span>
          <ChevronDown className="size-3.5 opacity-60" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="w-72">
        <DropdownMenuLabel>Work as an assistant from Arena</DropdownMenuLabel>
        {list.isPending && (
          <div className="grid gap-1 p-1">
            <Skeleton className="h-7" />
            <Skeleton className="h-7" />
          </div>
        )}
        {list.data?.assistants === null && <p className="px-2 py-1.5 text-xs text-muted-foreground">Sign in to Arena (code-arena login) to use your assistants here.</p>}
        {list.error && <p className="px-2 py-1.5 text-xs text-destructive-ink">{errorMessage(list.error)}</p>}
        <DropdownMenuRadioGroup value={current?.id ?? NONE} onValueChange={(v) => void choose(v)}>
          <DropdownMenuRadioItem value={NONE}>None: Code Arena's own</DropdownMenuRadioItem>
          {(list.data?.assistants ?? []).length > 0 && <DropdownMenuSeparator />}
          {(list.data?.assistants ?? []).map((a) => (
            <DropdownMenuRadioItem key={a.id} value={a.id}>
              <span className="grid min-w-0">
                <span className="truncate">{a.name}</span>
                {(a.description || a.model) && <span className="truncate text-xs text-muted-foreground">{[a.description, a.model].filter(Boolean).join(' · ')}</span>}
              </span>
            </DropdownMenuRadioItem>
          ))}
        </DropdownMenuRadioGroup>
        {list.data?.assistants?.length === 0 && <p className="px-2 py-1.5 text-xs text-muted-foreground">You have no assistants in Arena yet: make one there, under Assistants.</p>}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
