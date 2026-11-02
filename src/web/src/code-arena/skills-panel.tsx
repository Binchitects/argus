import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronsDownUp, MessageSquarePlus, Plus, RotateCw, WandSparkles } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { cn } from '@/lib/utils'
import { chatBridge } from './bridge'
import { rowKeys, useChangeView } from './change-folds'
import { FoldGroup } from './change-tree'
import { useEditor } from './editor-state'
import { PartHelp } from './help'
import { createSkill, skillsQuery, type SkillInfo } from './ide-api'

const action =
  'grid size-6 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50 [&_svg]:size-3.5'

const whose: { source: SkillInfo['source']; title: string }[] = [
  { source: 'project', title: 'This project' },
  { source: 'person', title: 'Yours' },
  { source: 'arena', title: 'From Arena' },
  { source: 'builtin', title: 'Built in' },
]

/**
 * The side bar's Skills: every skill the agent reads, by whose it is: this project's (.arena/skills), yours (every
 * project), Arena's (made there, or a plugin's) and Code Arena's own. The model loads one when a task needs it; /name in
 * the chat uses one. A skill of the project's or yours opens to be edited; any goes into the chat's box as /name. New
 * Skill makes one from a few lines to fill in. Each group folds.
 */
export function SkillsPanel() {
  const queryClient = useQueryClient()
  const editor = useEditor()
  const listed = useQuery(skillsQuery)
  const [making, setMaking] = useState<'project' | 'person' | null>(null)
  const [name, setName] = useState('')
  // The name's box takes the focus once it shows (the menu chosen from would take it back as it closes).
  const nameBox = useRef<HTMLInputElement>(null)
  const chose = useRef(false)
  useEffect(() => {
    if (making) nameBox.current?.focus()
  }, [making])
  const keys = whose.map((w) => w.source)
  const view = useChangeView('skills', keys)
  const make = useMutation({
    mutationFn: () => createSkill(name.trim(), making!),
    onSuccess: async (s) => {
      setMaking(null)
      setName('')
      await queryClient.invalidateQueries({ queryKey: skillsQuery.queryKey })
      if (s.path) await editor.open(s.path)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const skills = listed.data?.skills ?? []
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex h-9 shrink-0 items-center gap-1 pr-1.5 pl-3">
        <h2 className="min-w-0 flex-1 truncate text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">Skills</h2>
        <DropdownMenu>
          <Tooltip content="New Skill">
            <DropdownMenuTrigger asChild>
              <button type="button" aria-label="New Skill" className={action}>
                <Plus />
              </button>
            </DropdownMenuTrigger>
          </Tooltip>
          <DropdownMenuContent
            align="end"
            onCloseAutoFocus={(e) => {
              if (!chose.current) return
              chose.current = false
              e.preventDefault()
              nameBox.current?.focus()
            }}
          >
            {(['project', 'person'] as const).map((scope) => (
              <DropdownMenuItem
                key={scope}
                onSelect={() => {
                  chose.current = true
                  setMaking(scope)
                }}
              >
                {scope === 'project' ? 'For this project (.arena/skills)…' : 'For you, in every project…'}
              </DropdownMenuItem>
            ))}
          </DropdownMenuContent>
        </DropdownMenu>
        <Tooltip content="Collapse All">
          <button type="button" aria-label="Collapse All" onClick={() => view.foldAll(keys)} className={action}>
            <ChevronsDownUp />
          </button>
        </Tooltip>
        <Tooltip content="Read the skills again">
          <button type="button" aria-label="Refresh skills" onClick={() => void listed.refetch()} disabled={listed.isFetching} className={action}>
            <RotateCw className={cn(listed.isFetching && 'animate-spin')} />
          </button>
        </Tooltip>
        <PartHelp part="skills" />
      </div>
      {making && (
        <form
          className="grid gap-1 px-3 pb-2"
          onSubmit={(e) => {
            e.preventDefault()
            if (name.trim()) make.mutate()
          }}
        >
          <input
            ref={nameBox}
            value={name}
            onChange={(e) => setName(e.target.value.toLowerCase().replace(/\s+/g, '-'))}
            onKeyDown={(e) => {
              if (e.key === 'Escape') {
                e.preventDefault()
                setMaking(null)
                setName('')
              }
            }}
            placeholder="release-a-version"
            aria-label={making === 'project' ? "The new skill's name, for this project" : "The new skill's name, for you"}
            disabled={make.isPending}
            className="h-7 rounded border bg-background px-2 font-mono text-xs outline-none focus-visible:ring-1 focus-visible:ring-ring"
          />
          <p className="text-xs text-muted-foreground">
            {making === 'project' ? 'In .arena/skills of the repository: commit it to share it.' : "In code-arena's config folder: yours, in every project."} Enter makes it and opens its SKILL.md;
            Esc does not.
          </p>
        </form>
      )}
      <div className="min-h-0 flex-1 overflow-auto pb-4 text-[0.8125rem]" data-tree>
        {listed.isPending && <p className="px-3 py-2 text-xs text-muted-foreground">Reading…</p>}
        {listed.error && <p className="px-3 py-2 text-xs text-muted-foreground">{errorMessage(listed.error)}</p>}
        {listed.data && skills.length === 0 && (
          <p className="flex items-start gap-1.5 px-3 py-2 text-xs text-muted-foreground">
            <WandSparkles className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" /> No skills yet. New Skill makes one; the agent keeps one it worked out with skill_save.
          </p>
        )}
        {whose.map(({ source, title }) => {
          const these = skills.filter((s) => s.source === source)
          if (these.length === 0) return null
          return (
            <FoldGroup key={source} label={title} count={these.length} noun="skill" open={!view.folded(source)} onToggle={() => view.toggle(source)} className="mb-1">
              <ul aria-label={`Skills: ${title.toLowerCase()}`}>
                {these.map((s) => (
                  <li key={s.name} className="group flex h-[1.375rem] items-center pr-1.5 hover:bg-accent/70 focus-within:bg-accent/70">
                    <button
                      type="button"
                      data-row
                      onKeyDown={rowKeys}
                      onClick={() => (s.path ? void editor.open(s.path) : chatBridge.current?.use(s.name))}
                      title={`/${s.name}: ${s.description}${s.path ? ` (${s.path.replace(/^user:/, '')})` : ''}`}
                      className="flex h-full min-w-0 flex-1 items-center gap-1.5 pl-3 text-left outline-none focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-inset"
                    >
                      <span className="shrink-0 font-mono">/{s.name}</span>
                      <span className="min-w-0 truncate text-xs text-muted-foreground">{s.description}</span>
                    </button>
                    <span className="hidden shrink-0 items-center group-focus-within:flex group-hover:flex">
                      <Tooltip content="Use in the chat (/name in its box)">
                        <button type="button" aria-label={`Use /${s.name} in the chat`} onClick={() => chatBridge.current?.use(s.name)} className={action}>
                          <MessageSquarePlus />
                        </button>
                      </Tooltip>
                    </span>
                  </li>
                ))}
              </ul>
            </FoldGroup>
          )
        })}
      </div>
    </div>
  )
}
