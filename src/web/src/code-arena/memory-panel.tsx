import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { BookOpen, Brain, FolderGit2, Pin, Plus, RotateCw, Search, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { memoryQuery, type CodeMemory } from './api'
import { useEditor } from './editor-state'
import { api } from './http'

const action =
  'grid size-6 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50 [&_svg]:size-3.5'

const whose: { scope: CodeMemory['scope']; title: string }[] = [
  { scope: 'project', title: 'This project' },
  { scope: 'person', title: 'About you' },
  { scope: 'company', title: "The company's" },
]

/**
 * The side bar's Memory: what Arena remembers for this project and the person (the web's Memory page shows the same),
 * found by its words; one kept from here (for this project, or for every one) or forgotten.
 */
export function MemoryPanel() {
  const queryClient = useQueryClient()
  const editor = useEditor()
  const memory = useQuery(memoryQuery)
  const [find, setFind] = useState('')
  const [text, setText] = useState('')
  const [project, setProject] = useState(true)
  const changed = () => queryClient.invalidateQueries({ queryKey: memoryQuery.queryKey })
  const keep = useMutation({
    mutationFn: () => api<{ added: boolean; where: string }>('/api/memory', { body: { text: text.trim(), project } }),
    onSuccess: async (r) => {
      toast.success(r.added ? `Remembered ${r.where}.` : 'Remembered already.')
      setText('')
      await changed()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const forget = useMutation({ mutationFn: (id: string) => api(`/api/memory/${encodeURIComponent(id)}`, { method: 'DELETE' }), onSuccess: changed, onError: (e) => toast.error(errorMessage(e)) })
  const words = find.trim().toLowerCase()
  const all = memory.data?.memories ?? []
  const shown = all.filter((m) => !words || m.text.toLowerCase().includes(words))
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex h-9 shrink-0 items-center gap-1 pr-1.5 pl-3">
        <h2 className="min-w-0 flex-1 truncate text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">Memory</h2>
        <Tooltip content="Read again from Arena">
          <button type="button" aria-label="Read the memories again" onClick={() => void memory.refetch()} disabled={memory.isFetching} className={action}>
            <RotateCw className={cn(memory.isFetching && 'animate-spin')} />
          </button>
        </Tooltip>
      </div>
      <div className="grid gap-2 px-3 pb-2">
        {memory.data?.notebook && (
          <button type="button" onClick={() => void editor.open(memory.data!.notebook!)} className="flex items-center gap-1.5 rounded px-1 py-0.5 text-left text-xs hover:bg-accent">
            <BookOpen className="size-3.5 shrink-0" aria-hidden="true" />
            <span className="min-w-0 truncate">The project&apos;s long memory: {memory.data.notebook}</span>
          </button>
        )}
        <label className="flex items-center gap-1.5 rounded border bg-background px-2">
          <Search className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
          <input value={find} onChange={(e) => setFind(e.target.value)} placeholder="Find a memory" aria-label="Find a memory" className="h-7 min-w-0 flex-1 bg-transparent text-xs outline-none" />
        </label>
        <form
          className="grid gap-1.5"
          onSubmit={(e) => {
            e.preventDefault()
            if (text.trim()) keep.mutate()
          }}
        >
          <textarea value={text} onChange={(e) => setText(e.target.value)} rows={2} maxLength={500} placeholder="Remember something…" aria-label="Remember something" className="resize-none rounded border bg-background px-2 py-1 text-xs outline-none focus-visible:ring-1 focus-visible:ring-ring" />
          <div className="flex items-center gap-2 text-xs">
            <label className="flex items-center gap-1.5 text-muted-foreground">
              <input type="checkbox" checked={project} onChange={(e) => setProject(e.target.checked)} /> For this project only
            </label>
            <button type="submit" disabled={!text.trim() || keep.isPending || memory.data?.online === false} className="ms-auto flex h-6 items-center gap-1 rounded bg-primary px-2 text-primary-foreground disabled:opacity-50">
              <Plus className="size-3.5" aria-hidden="true" /> Remember
            </button>
          </div>
        </form>
      </div>
      <div className="min-h-0 flex-1 overflow-auto px-3 pb-3 text-xs">
        {memory.isPending ? (
          <p className="text-muted-foreground">Reading…</p>
        ) : memory.data?.online === false ? (
          <p className="text-muted-foreground">Arena does not answer: the memories are Arena&apos;s, and this session uses those it gave at its start.</p>
        ) : all.length === 0 ? (
          <p className="flex items-center gap-1.5 text-muted-foreground">
            <Brain className="size-3.5" aria-hidden="true" /> Nothing remembered yet. Tell the agent &ldquo;remember that …&rdquo;, or add it above.
          </p>
        ) : (
          whose.map(({ scope, title }) => {
            const these = shown.filter((m) => m.scope === scope)
            if (these.length === 0) return null
            return (
              <section key={scope} className="mb-3" aria-label={title}>
                <h3 className="mb-1 flex items-center gap-1 font-medium">
                  {scope === 'project' && <FolderGit2 className="size-3.5" aria-hidden="true" />} {title}
                  {scope === 'project' && memory.data?.project ? <span className="truncate font-normal text-muted-foreground">{memory.data.project}</span> : null}
                </h3>
                <ul className="grid gap-1">
                  {these.map((m) => (
                    <li key={m.id} className="group flex items-start gap-1.5 rounded px-1 py-0.5 hover:bg-accent/50">
                      {m.pinned && <Pin className="mt-0.5 size-3 shrink-0 text-primary" aria-label="Pinned" />}
                      <span className="min-w-0 flex-1">
                        {m.text} <span className="text-muted-foreground">({m.kind === 'howto' ? 'how to' : m.kind})</span>
                      </span>
                      {m.mine && (
                        <button type="button" aria-label={`Forget: ${m.text}`} onClick={() => forget.mutate(m.id)} disabled={forget.isPending} className={cn(action, 'opacity-0 group-hover:opacity-100 focus-visible:opacity-100')}>
                          <Trash2 />
                        </button>
                      )}
                    </li>
                  ))}
                </ul>
              </section>
            )
          })
        )}
      </div>
    </div>
  )
}
