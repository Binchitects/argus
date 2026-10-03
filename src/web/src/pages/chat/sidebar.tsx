import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Archive, ArchiveRestore, ArrowLeft, ChevronRight, FolderKanban, FolderPlus, GitFork, MessageSquarePlus, MoreHorizontal, Pencil, Search, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { NavLink, useNavigate } from 'react-router'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { api, errorMessage } from '@/lib/api'
import { Button } from '@/components/ui/button'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { cn } from '@/lib/utils'
import { listQuery, projectsQuery } from './api'
import { useChatActions } from './chat-actions'
import { ChatSearch } from './chat-search'
import { bucket } from './format'
import type { ConversationSummary } from './types'

export function ChatList({ activeId, activeProject, onNew, onNavigate }: { activeId?: string; activeProject?: string; onNew: () => void; onNavigate?: () => void }) {
  const [search, setSearch] = useState('')
  const [archived, setArchived] = useState(false)
  const list = useQuery(listQuery(search.trim(), archived))
  const groups = new Map<string, ConversationSummary[]>()
  for (const c of list.data ?? []) {
    const b = archived ? 'Archived' : bucket(c.updatedAt)
    groups.set(b, [...(groups.get(b) ?? []), c])
  }
  return (
    <nav aria-label="Chats" className="flex h-full min-h-0 flex-col">
      <div className="grid gap-2 p-3">
        {archived ? (
          <Button variant="ghost" onClick={() => setArchived(false)} className="justify-start">
            <ArrowLeft /> All chats
          </Button>
        ) : (
          <Button onClick={onNew} className="justify-start">
            <MessageSquarePlus /> New chat
          </Button>
        )}
        <div className="flex items-center gap-1">
          <div className="relative min-w-0 flex-1">
            <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
            <Input type="search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder={archived ? 'Search archived chats' : 'Search chats'} aria-label="Search chats" className="h-8 pl-8" />
          </div>
          <ChatSearch onNavigate={onNavigate} />
        </div>
      </div>
      {/* Titles are cut with an ellipsis: the list never scrolls sideways. */}
      <div className="min-h-0 flex-1 overflow-x-hidden overflow-y-auto px-2 pb-3">
        {!archived && !search && <Projects active={activeProject} onNavigate={onNavigate} />}
        {list.isPending && Array.from({ length: 6 }, (_, i) => <Skeleton key={i} className="mx-1 mb-2 h-7" />)}
        {list.data?.length === 0 && (
          <p className="px-2 py-4 text-sm text-muted-foreground">{search ? 'No chat matches.' : archived ? 'No archived chats.' : 'No chats yet.'}</p>
        )}
        {[...groups].map(([title, items]) => (
          <div key={title} className="mb-3 min-w-0">
            <p className="px-2 pb-1 text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">{title}</p>
            <ul className="flex min-w-0 flex-col gap-0.5">
              {items.map((c) => (
                <ChatItem key={c.id} chat={c} active={c.id === activeId} archived={archived} onNavigate={onNavigate} />
              ))}
            </ul>
          </div>
        ))}
      </div>
      {!archived && (
        <div className="border-t p-2">
          <Button variant="ghost" size="sm" className="w-full justify-start text-muted-foreground" onClick={() => setArchived(true)}>
            <Archive /> Archived chats
          </Button>
        </div>
      )}
    </nav>
  )
}

function ChatItem({ chat, active, archived, onNavigate }: { chat: ConversationSummary; active: boolean; archived: boolean; onNavigate?: () => void }) {
  const [renaming, setRenaming] = useState<string | null>(null)
  const { rename, archive, fork, askDelete } = useChatActions(chat, active)
  if (renaming !== null)
    return (
      <li className="min-w-0">
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
    <li className="group/item relative min-w-0 animate-enter">
      <NavLink
        to={`/chat/${chat.id}`}
        onClick={onNavigate}
        title={chat.title}
        className={cn('block truncate rounded-md py-1.5 pr-8 pl-2 text-sm transition-colors duration-150 outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring', active ? 'bg-accent font-medium text-foreground' : 'text-foreground/85')}
      >
        {chat.answering && (
          <>
            <span className="mr-1.5 inline-block size-1.5 animate-pulse rounded-full bg-primary align-middle" aria-hidden="true" />
            <span className="sr-only">Answering: </span>
          </>
        )}
        <bdi>{chat.title}</bdi>
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
          <DropdownMenuItem onSelect={() => fork.mutate(undefined, { onSuccess: () => onNavigate?.() })}>
            <GitFork /> Fork
          </DropdownMenuItem>
          <DropdownMenuItem onSelect={() => archive.mutate(!archived)}>
            {archived ? <ArchiveRestore /> : <Archive />} {archived ? 'Unarchive' : 'Archive'}
          </DropdownMenuItem>
          <DropdownMenuSeparator />
          <DropdownMenuItem variant="destructive" onSelect={() => void askDelete()}>
            <Trash2 /> Delete
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </li>
  )
}

/** The person's projects, above their chats: each opens its page; a new one is a name away. */
function Projects({ active, onNavigate }: { active?: string; onNavigate?: () => void }) {
  const projects = useQuery(projectsQuery)
  const [open, setOpen] = useState(true)
  const [creating, setCreating] = useState(false)
  return (
    <div className="mb-3 min-w-0">
      <div className="flex items-center gap-1 px-1 pb-1">
        <button
          type="button"
          onClick={() => setOpen(!open)}
          aria-expanded={open}
          className="flex min-w-0 flex-1 items-center gap-1 rounded px-1 text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase outline-none hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring"
        >
          <ChevronRight className={cn('size-3 transition-transform', open && 'rotate-90')} aria-hidden="true" /> Projects
        </button>
        <Tooltip content="New project">
          <Button variant="ghost" size="icon-sm" className="size-6" onClick={() => setCreating(true)} aria-label="New project">
            <FolderPlus />
          </Button>
        </Tooltip>
      </div>
      {open && (
        <ul className="flex min-w-0 flex-col gap-0.5" aria-label="Projects">
          {projects.data?.map((p) => (
            <li key={p.id} className="min-w-0">
              <NavLink
                to={`/chat/projects/${p.id}`}
                onClick={onNavigate}
                title={p.name}
                className={cn(
                  'flex min-w-0 items-center gap-2 rounded-md px-2 py-1.5 text-sm outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring',
                  p.id === active ? 'bg-accent font-medium' : 'text-foreground/85',
                )}
              >
                <FolderKanban className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                <bdi className="min-w-0 flex-1 truncate">{p.name}</bdi>
                <span className="shrink-0 text-xs text-muted-foreground tabular-nums">{p.chats}</span>
              </NavLink>
            </li>
          ))}
          {projects.data?.length === 0 && (
            <li>
              <button type="button" onClick={() => setCreating(true)} className="w-full rounded-md px-2 py-1.5 text-left text-sm text-muted-foreground outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring">
                Keep chats together, with instructions and files…
              </button>
            </li>
          )}
        </ul>
      )}
      <NewProject open={creating} onOpenChange={setCreating} onNavigate={onNavigate} />
    </div>
  )
}

function NewProject({ open, onOpenChange, onNavigate }: { open: boolean; onOpenChange: (open: boolean) => void; onNavigate?: () => void }) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const [form, setForm] = useState({ name: '', description: '' })
  const create = useMutation({
    mutationFn: () => api<{ id: string }>('/api/projects', { body: form }),
    onSuccess: async (made) => {
      await queryClient.invalidateQueries({ queryKey: ['projects'] })
      onOpenChange(false)
      setForm({ name: '', description: '' })
      onNavigate?.()
      void navigate(`/chat/projects/${made.id}`)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>New project</DialogTitle>
          <DialogDescription>Chats kept together, with instructions and files every answer in them reads.</DialogDescription>
        </DialogHeader>
        <form
          className="grid gap-4"
          onSubmit={(e) => {
            e.preventDefault()
            create.mutate()
          }}
        >
          <Field label="Name">
            <Input autoFocus required maxLength={100} value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
          </Field>
          <Field label="What it is for" hint="Optional.">
            <Input maxLength={500} value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
          </Field>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" loading={create.isPending}>
              Create
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
