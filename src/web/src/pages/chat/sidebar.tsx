import { useQuery } from '@tanstack/react-query'
import { Archive, ArchiveRestore, ArrowLeft, ChevronRight, GitFork, LayoutGrid, MessageSquarePlus, MoreHorizontal, PanelLeftClose, PanelLeftOpen, Pencil, Plus, Search, Trash2 } from 'lucide-react'
import { useId, useState, type ReactNode } from 'react'
import { Link, NavLink } from 'react-router'
import { Tooltip } from '@/components/ui/tooltip'
import { Button } from '@/components/ui/button'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { cn } from '@/lib/utils'
import { assistantsQuery, listQuery } from './api'
import { AssistantIcon } from './assistant-icon'
import { NewAssistant } from './assistant-new'
import { useChatActions } from './chat-actions'
import { ChatSearch } from './chat-search'
import { bucket } from './format'
import { useListFolds } from './list-folds'
import type { ConversationSummary } from './types'

/**
 * The person's chats by date, their assistants above. Each group folds under its
 * heading, the open chat staying in sight; beside the chat on a wide screen
 * (`foldable`) the whole list folds to a narrow rail. What is folded is
 * remembered for the person on this browser.
 */
export function ChatList({ activeId, activeAssistant, onNew, onNavigate, foldable = false }: { activeId?: string; activeAssistant?: string; onNew: () => void; onNavigate?: () => void; foldable?: boolean }) {
  const [search, setSearch] = useState('')
  const [archived, setArchived] = useState(false)
  const list = useQuery(listQuery(search.trim(), archived))
  const folds = useListFolds()
  const folded = foldable && folds.listFolded
  const listId = useId()
  const groups = new Map<string, ConversationSummary[]>()
  for (const c of list.data ?? []) {
    const b = archived ? 'Archived' : bucket(c.updatedAt)
    groups.set(b, [...(groups.get(b) ?? []), c])
  }
  const searching = search.trim() !== ''

  return (
    <nav aria-label="Chats" className="flex h-full min-h-0 flex-col">
      <div className={folded ? 'grid justify-items-center gap-1 py-3' : 'grid gap-2 p-3'}>
        <div className={cn('flex items-center gap-1', folded && 'flex-col')}>
          {/* One button folds and unfolds, in the same place: the focus stays on it, and its hint closes with the click. */}
          {foldable && (
            <Tooltip content={folded ? 'Expand chat list' : 'Collapse chat list'} side={folded ? 'right' : undefined}>
              <Button variant="ghost" size="icon-sm" className="shrink-0 text-muted-foreground" onClick={() => folds.setListFolded(!folded)} aria-label={folded ? 'Expand chat list' : 'Collapse chat list'} aria-expanded={!folded} aria-controls={folded ? undefined : listId}>
                {folded ? <PanelLeftOpen /> : <PanelLeftClose />}
              </Button>
            </Tooltip>
          )}
          {folded ? (
            <Tooltip content="New chat" side="right">
              <Button size="icon-sm" onClick={onNew} aria-label="New chat">
                <MessageSquarePlus />
              </Button>
            </Tooltip>
          ) : archived ? (
            <Button variant="ghost" onClick={() => setArchived(false)} className="min-w-0 flex-1 justify-start">
              <ArrowLeft /> All chats
            </Button>
          ) : (
            <Button onClick={onNew} className="min-w-0 flex-1 justify-start">
              <MessageSquarePlus /> New chat
            </Button>
          )}
        </div>
        {folded ? (
          <ChatSearch onNavigate={onNavigate} />
        ) : (
          <div className="flex items-center gap-1">
            <div className="relative min-w-0 flex-1">
              <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
              <Input type="search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder={archived ? 'Search archived chats' : 'Search chats'} aria-label="Search chats" className="h-8 pl-8" />
            </div>
            <ChatSearch onNavigate={onNavigate} />
          </div>
        )}
      </div>
      {!folded && (
        <>
          {/* Titles are cut with an ellipsis: the list never scrolls sideways. */}
          <div id={listId} className="min-h-0 flex-1 overflow-x-hidden overflow-y-auto px-2 pb-3">
            {!archived && !search && <Assistants active={activeAssistant} onNavigate={onNavigate} />}
            {list.isPending && Array.from({ length: 6 }, (_, i) => <Skeleton key={i} className="mx-1 mb-2 h-7" />)}
            {list.data?.length === 0 && (
              <p className="px-2 py-4 text-sm text-muted-foreground">{search ? 'No chat matches.' : archived ? 'No archived chats.' : 'No chats yet.'}</p>
            )}
            {[...groups].map(([title, items]) => (
              // A search shows every match: folded groups stay open while it lasts.
              <Group
                key={title}
                title={title}
                items={items}
                activeId={activeId}
                folded={!searching && folds.isFolded(title)}
                onToggle={searching ? undefined : () => folds.toggleGroup(title)}
                item={(c) => <ChatItem key={c.id} chat={c} active={c.id === activeId} archived={archived} onNavigate={onNavigate} />}
              />
            ))}
          </div>
          {!archived && (
            <div className="border-t p-2">
              <Button variant="ghost" size="sm" className="w-full justify-start text-muted-foreground" onClick={() => setArchived(true)}>
                <Archive /> Archived chats
              </Button>
            </div>
          )}
        </>
      )}
    </nav>
  )
}

/** A group's heading that folds it. Folded, it says how many the group holds (and how many of its chats are answering). */
function FoldHeading({ title, folded, controls, count, noun, answering = 0, onToggle, className }: { title: string; folded: boolean; controls?: string; count?: number; noun: [one: string, many: string]; answering?: number; onToggle: () => void; className?: string }) {
  return (
    <button
      type="button"
      onClick={onToggle}
      aria-expanded={!folded}
      aria-controls={controls}
      className={cn('flex min-w-0 items-center gap-1 rounded px-1 text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase outline-none hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring', className)}
    >
      <ChevronRight className={cn('size-3 shrink-0 transition-transform', !folded && 'rotate-90')} aria-hidden="true" />
      <span className="min-w-0 flex-1 truncate text-left">{title}</span>
      {folded && count !== undefined && (
        <span className="flex shrink-0 items-center gap-1.5 font-normal tracking-normal tabular-nums normal-case">
          {answering > 0 && <span className="inline-block size-1.5 animate-pulse rounded-full bg-primary" aria-hidden="true" />}
          <span aria-hidden="true">{count}</span>
          <span className="sr-only">{`, ${count} ${count === 1 ? noun[0] : noun[1]}${answering > 0 ? `, ${answering} answering` : ''}`}</span>
        </span>
      )}
    </button>
  )
}

/** Chats under a heading that folds them (a plain heading while a search runs). Folded, it counts them; the open chat stays in sight. */
function Group({ title, items, activeId, folded, onToggle, item }: { title: string; items: ConversationSummary[]; activeId?: string; folded: boolean; onToggle?: () => void; item: (c: ConversationSummary) => ReactNode }) {
  const id = useId()
  const shown = folded ? items.filter((c) => c.id === activeId) : items
  return (
    <div className="mb-3 min-w-0">
      {onToggle ? (
        <div className="px-1 pb-1">
          <FoldHeading title={title} folded={folded} controls={shown.length > 0 ? id : undefined} count={items.length} noun={['chat', 'chats']} answering={items.filter((c) => c.answering).length} onToggle={onToggle} className="w-full" />
        </div>
      ) : (
        <p className="px-2 pb-1 text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">{title}</p>
      )}
      {shown.length > 0 && (
        <ul id={id} className="flex min-w-0 flex-col gap-0.5">
          {shown.map(item)}
        </ul>
      )}
    </div>
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

/** The person's assistants (theirs, those they edit, those they chat with), above their chats; the gallery a click away. */
function Assistants({ active, onNavigate }: { active?: string; onNavigate?: () => void }) {
  const assistants = useQuery(assistantsQuery)
  const folds = useListFolds()
  const open = !folds.isFolded('Assistants')
  const listId = useId()
  const [creating, setCreating] = useState(false)
  const theirs = (assistants.data ?? []).filter((a) => a.canEdit || a.myChats > 0).sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))
  // Folded, the assistant open in the chat stays in sight.
  const shown = open ? theirs : theirs.filter((a) => a.id === active)
  const listed = open || shown.length > 0
  return (
    <div className="mb-3 min-w-0">
      <div className="flex items-center gap-1 px-1 pb-1">
        <FoldHeading title="Assistants" folded={!open} controls={listed ? listId : undefined} count={assistants.isSuccess ? theirs.length : undefined} noun={['assistant', 'assistants']} onToggle={() => folds.toggleGroup('Assistants')} className="flex-1" />
        <Tooltip content="All assistants">
          <Button variant="ghost" size="icon-sm" className="size-6" asChild>
            <Link to="/assistants" onClick={onNavigate} aria-label="All assistants">
              <LayoutGrid />
            </Link>
          </Button>
        </Tooltip>
        <Tooltip content="New assistant">
          <Button variant="ghost" size="icon-sm" className="size-6" onClick={() => setCreating(true)} aria-label="New assistant">
            <Plus />
          </Button>
        </Tooltip>
      </div>
      {listed && (
        <ul id={listId} className="flex min-w-0 flex-col gap-0.5" aria-label="Assistants">
          {shown.map((a) => (
            <li key={a.id} className="min-w-0">
              <NavLink
                to={`/chat/assistants/${a.id}`}
                onClick={onNavigate}
                title={a.name}
                className={cn(
                  'flex min-w-0 items-center gap-2 rounded-md px-2 py-1.5 text-sm outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring',
                  a.id === active ? 'bg-accent font-medium' : 'text-foreground/85',
                )}
              >
                <AssistantIcon icon={a.icon} color={a.color} size="sm" />
                <bdi className="min-w-0 flex-1 truncate">{a.name}</bdi>
                <span className="shrink-0 text-xs text-muted-foreground tabular-nums">{a.myChats}</span>
              </NavLink>
            </li>
          ))}
          {open && assistants.isSuccess && theirs.length === 0 && (
            <li>
              <Link to="/assistants" onClick={onNavigate} className="block w-full rounded-md px-2 py-1.5 text-left text-sm text-muted-foreground outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring">
                {assistants.data.length > 0 ? `${assistants.data.length} shared with you: browse them…` : 'Instructions and files for chats, yours or your team’s…'}
              </Link>
            </li>
          )}
        </ul>
      )}
      <NewAssistant open={creating} onOpenChange={setCreating} onNavigate={onNavigate} />
    </div>
  )
}
