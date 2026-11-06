import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronsDownUp, Download, MessageSquarePlus, MoreHorizontal, Plus, RotateCw, WandSparkles } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import { saveBlob } from '@/lib/zip'
import { cn } from '@/lib/utils'
import { SkillImport } from '@/pages/skills-import'
import { chatBridge } from './bridge'
import { rowKeys, useChangeView } from './change-folds'
import { FoldGroup } from './change-tree'
import { useEditor } from './editor-state'
import { PartHelp } from './help'
import { createSkill, exportIdeSkills, importSkills, previewSkillImport, refreshSkills, skillsQuery, type SkillInfo, type SkillScope } from './ide-api'

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
 * project: in Arena when signed in to it, or on this computer), Arena's others (made there, or a plugin's) and Code
 * Arena's own. The model loads one when a task needs it; /name in the chat uses one. A skill of the project's or yours
 * opens to be edited; any goes into the chat's box as /name. New Skill makes one from a few lines to fill in. Refresh asks
 * Arena at once for one made on its Skills page. Each group folds. Each skill says the folder it was read from
 * (.code-arena/skills, .claude/skills, ~/.claude/skills…); … exports them as a .zip of their folders and imports one.
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
      else toast.success(`Made /${s.name}: Refresh lists it with its file to open.`)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  // Arena asked at once: one made on its Skills page (or turned off there) since is listed now.
  const again = useMutation({
    mutationFn: refreshSkills,
    onSuccess: (data) => queryClient.setQueryData(skillsQuery.queryKey, data),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const [importing, setImporting] = useState(false)
  const [scope, setScope] = useState<SkillScope>('project')
  const download = useMutation({
    mutationFn: ({ names, format }: { names: string[]; format?: 'zip' | 'md' }) => exportIdeSkills(names, format),
    onSuccess: ({ blob, fileName }) => saveBlob(blob, fileName),
    onError: (e) => toast.error(errorMessage(e)),
  })
  const reading = listed.isFetching || again.isPending
  const inArena = listed.data?.mineInArena ?? false
  const skills = listed.data?.skills ?? []
  const mine = skills.filter((s) => s.source === 'project' || s.source === 'person')
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
                {scope === 'project' ? 'For this project (.arena/skills)…' : inArena ? 'For you, in Arena (every project and the web chat)…' : 'For you, on this computer…'}
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
          <button type="button" aria-label="Refresh skills" onClick={() => again.mutate()} disabled={reading} className={action}>
            <RotateCw className={cn(reading && 'animate-spin')} />
          </button>
        </Tooltip>
        <DropdownMenu>
          <Tooltip content="More Actions…">
            <DropdownMenuTrigger asChild>
              <button type="button" aria-label="More skill actions" className={action}>
                <MoreHorizontal />
              </button>
            </DropdownMenuTrigger>
          </Tooltip>
          <DropdownMenuContent align="end">
            <DropdownMenuItem onSelect={() => setImporting(true)}>Import Skills…</DropdownMenuItem>
            <DropdownMenuSeparator />
            <DropdownMenuItem disabled={mine.length === 0} onSelect={() => download.mutate({ names: mine.map((s) => s.name) })}>
              Export the project’s and yours (.zip)
            </DropdownMenuItem>
            <DropdownMenuItem disabled={skills.length === 0} onSelect={() => download.mutate({ names: skills.map((s) => s.name) })}>
              Export all (.zip)
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
        <PartHelp part="skills" />
      </div>
      <Dialog open={importing} onOpenChange={setImporting}>
        <DialogContent className="grid-cols-[minmax(0,1fr)] sm:max-w-2xl">
          {importing && (
            <SkillImport
              key={scope}
              onClose={() => setImporting(false)}
              look={(files) => previewSkillImport(files, scope)}
              bring={(files, choices) => importSkills(files, scope, choices)}
              done={() => queryClient.invalidateQueries({ queryKey: skillsQuery.queryKey })}
              about="Every file is looked at for secrets first (by Arena's safeguards too, when signed in)."
              intro={
                <div role="radiogroup" aria-label="Where they go" className="grid gap-1.5 text-sm">
                  {(['project', 'person'] as const).map((s) => (
                    <label key={s} className="flex items-start gap-2">
                      <input type="radio" name="skill-scope" className="mt-1 accent-primary" checked={scope === s} onChange={() => setScope(s)} />
                      <span>
                        {s === 'project' ? 'For this project' : inArena ? 'For you, in Arena' : 'For you, on this computer'}
                        <span className="block text-xs text-muted-foreground">
                          {s === 'project'
                            ? 'In .arena/skills of the repository: commit them to share them.'
                            : inArena
                              ? 'Yours in Arena: the web chat and Code Arena everywhere use them.'
                              : "In code-arena's config folder: yours, in every project on this computer."}
                        </span>
                      </span>
                    </label>
                  ))}
                </div>
              }
            />
          )}
        </DialogContent>
      </Dialog>
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
            {making === 'project'
              ? 'In .arena/skills of the repository: commit it to share it.'
              : inArena
                ? 'One of your skills in Arena: the web chat and Code Arena everywhere use it.'
                : "In code-arena's config folder: yours, in every project on this computer."}{' '}
            Enter makes it and opens its SKILL.md; Esc does not.
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
                      title={`/${s.name}: ${s.description}${s.path ? ` (${s.path.replace(/^user:/, '')})` : s.origin && !['arena', 'builtin', 'config'].includes(s.origin) ? ` (${s.origin})` : ''}`}
                      className="flex h-full min-w-0 flex-1 items-center gap-1.5 pl-3 text-left outline-none focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-inset"
                    >
                      <span className="shrink-0 font-mono">/{s.name}</span>
                      <span className="min-w-0 truncate text-xs text-muted-foreground">{s.description}</span>
                      {s.origin && !['arena', 'builtin', 'config', '.arena/skills'].includes(s.origin) && <span className="ms-auto shrink-0 pe-1 text-[0.6875rem] text-muted-foreground/80">{s.origin}</span>}
                    </button>
                    <span className="hidden shrink-0 items-center group-focus-within:flex group-hover:flex">
                      <Tooltip content="Export (.zip of its folder)">
                        <button type="button" aria-label={`Export /${s.name}`} onClick={() => download.mutate({ names: [s.name] })} className={action}>
                          <Download />
                        </button>
                      </Tooltip>
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
