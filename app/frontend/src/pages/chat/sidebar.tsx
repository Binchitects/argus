import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { MessageSquarePlus, MoreHorizontal, Pencil, Search, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { NavLink, useNavigate } from 'react-router'
import { Button } from '@/components/ui/button'
import { useConfirm } from '@/components/ui/confirm'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { listQuery } from './api'
import { bucket } from './format'
import type { ConversationSummary } from './types'

export function ChatList({ activeId, onNew, onNavigate }: { activeId?: string; onNew: () => void; onNavigate?: () => void }) {
  const [search, setSearch] = useState('')
  const list = useQuery(listQuery(search.trim()))
  const groups = new Map<string, ConversationSummary[]>()
  for (const c of list.data ?? []) {
    const b = bucket(c.updatedAt)
    groups.set(b, [...(groups.get(b) ?? []), c])
  }
  return (
    <nav aria-label="Chats" className="flex h-full min-h-0 flex-col">
      <div className="grid gap-2 p-3">
        <Button onClick={onNew} className="justify-start">
          <MessageSquarePlus /> New chat
        </Button>
        <div className="relative">
          <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
          <Input type="search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search chats" aria-label="Search chats" className="h-8 pl-8" />
        </div>
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto px-2 pb-3">
        {list.isPending && Array.from({ length: 6 }, (_, i) => <Skeleton key={i} className="mx-1 mb-2 h-7" />)}
        {list.data?.length === 0 && <p className="px-2 py-4 text-sm text-muted-foreground">{search ? 'No chat matches.' : 'No chats yet.'}</p>}
        {[...groups].map(([title, items]) => (
          <div key={title} className="mb-3">
            <p className="px-2 pb-1 text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">{title}</p>
            <ul className="grid gap-0.5">
              {items.map((c) => (
                <ChatItem key={c.id} chat={c} active={c.id === activeId} onNavigate={onNavigate} />
              ))}
            </ul>
          </div>
        ))}
      </div>
    </nav>
  )
}

function ChatItem({ chat, active, onNavigate }: { chat: ConversationSummary; active: boolean; onNavigate?: () => void }) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const confirm = useConfirm()
  const [renaming, setRenaming] = useState<string | null>(null)
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['chat'] })
  const rename = useMutation({
    mutationFn: (title: string) => api(`/api/chat/conversations/${chat.id}`, { method: 'PATCH', body: { title } }),
    onSuccess: refresh,
    onError: (e) => toast.error(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: () => api(`/api/chat/conversations/${chat.id}`, { method: 'DELETE' }),
    onSuccess: () => {
      void refresh()
      if (active) navigate('/chat')
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (renaming !== null)
    return (
      <li>
        <form
          onSubmit={(e) => {
            e.preventDefault()
            rename.mutate(renaming)
            setRenaming(null)
          }}
        >
          <Input value={renaming} onChange={(e) => setRenaming(e.target.value)} onBlur={() => setRenaming(null)} aria-label="Chat name" autoFocus className="h-8" />
        </form>
      </li>
    )
  return (
    <li className="group/item relative">
      <NavLink
        to={`/chat/${chat.id}`}
        onClick={onNavigate}
        className={cn('block truncate rounded-md py-1.5 pr-8 pl-2 text-sm outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring', active ? 'bg-accent font-medium text-foreground' : 'text-foreground/85')}
      >
        {chat.title}
      </NavLink>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="ghost" size="icon-sm" className="absolute top-1/2 right-1 size-6 -translate-y-1/2 opacity-0 group-hover/item:opacity-100 focus-visible:opacity-100 data-[state=open]:opacity-100 [@media(hover:none)]:opacity-100" aria-label={`Actions for ${chat.title}`}>
            <MoreHorizontal />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end">
          <DropdownMenuItem onSelect={() => setRenaming(chat.title)}>
            <Pencil /> Rename
          </DropdownMenuItem>
          <DropdownMenuSeparator />
          <DropdownMenuItem
            variant="destructive"
            onSelect={async () => {
              if (await confirm({ title: 'Delete this chat?', description: `“${chat.title}” and its files go for good.`, confirm: 'Delete', destructive: true })) remove.mutate()
            }}
          >
            <Trash2 /> Delete
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </li>
  )
}
