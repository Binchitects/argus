import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { FolderInput, FolderKanban, Globe2, Plus, RotateCw, Search } from 'lucide-react'
import { useState } from 'react'
import { Segmented } from '@/components/app/segmented'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { ApiError, errorMessage } from '@/lib/api'
import { money } from '@/lib/format'
import { cn } from '@/lib/utils'
import { NewProjectDialog } from '@/pages/projects/new-project'
import { mayAdd, progressOf, projectsMayQuery, projectsQuery, roleLabels, shownProjects, type ProjectsShown, type ProjectSummary } from '@/pages/projects/api'
import { chooseProject, projectsQuery as choicesQuery, stateQuery } from './api'
import { runCommand } from './commands'
import { PartHelp } from './help'

const action =
  'grid size-6 place-items-center rounded-sm text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50 [&_svg]:size-3.5'

/** Opens a project in an editor tab of its own (the workbench's command: one tab each). */
const openProject = (p: { id: string; name: string }) => runCommand('codeArena.project.open', p)

/**
 * The side bar's Projects: Arena's projects as its Projects page has them (those the person is in, the global ones, every
 * one they see), found by their words; the session's and the repository's marked; a new one made as Arena lets the person.
 * One opens in an editor tab. Arena answers every call (with the person's key, through code-arena), so what may be done
 * here is what Arena's page lets them do.
 */
export function ProjectsView({ session }: { session: { id: string; name: string } | null | undefined }) {
  const queryClient = useQueryClient()
  // Not signed in to Arena here (code-arena has no key): said at once, not asked again.
  const projects = useQuery({ ...projectsQuery, retry: (n, e) => !(e instanceof ApiError && (e.http < 500 || e.status === 'no_arena')) && n < 2 })
  const may = useQuery(projectsMayQuery)
  // The repository's project (Arena knows it by the folder's origin), as the chat's picker marks it.
  const choices = useQuery(choicesQuery)
  const [show, setShow] = useState<ProjectsShown>('mine')
  const [find, setFind] = useState('')
  const [making, setMaking] = useState(false)
  const put = useMutation({
    mutationFn: chooseProject,
    onSuccess: async ({ said }) => {
      await queryClient.invalidateQueries({ queryKey: stateQuery.queryKey })
      await queryClient.invalidateQueries({ queryKey: ['projects'] })
      toast.success(said)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const all = projects.data ?? []
  const shown = shownProjects(all, show, find)
  const suggested = choices.data?.suggested ?? null
  const signedOut = projects.error instanceof ApiError && (projects.error.status === 'no_arena' || projects.error.http === 401)
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex h-9 shrink-0 items-center gap-1 pr-1.5 pl-3">
        <h2 className="min-w-0 flex-1 truncate text-[0.6875rem] font-semibold tracking-wider text-muted-foreground uppercase">Projects</h2>
        {may.data?.create && (
          <Tooltip content="New project">
            <button type="button" aria-label="New project" onClick={() => setMaking(true)} className={action}>
              <Plus />
            </button>
          </Tooltip>
        )}
        <Tooltip content="Read again from Arena">
          <button type="button" aria-label="Read the projects again" onClick={() => void projects.refetch()} disabled={projects.isFetching} className={action}>
            <RotateCw className={cn(projects.isFetching && 'animate-spin')} />
          </button>
        </Tooltip>
        <PartHelp part="projects" />
      </div>
      <div className="grid gap-2 px-3 pb-2">
        <Segmented
          label="Show"
          value={show}
          onChange={setShow}
          className="h-8 w-full [&>*]:flex-1 [&>*]:justify-center [&>*]:px-1.5 [&>*]:text-xs"
          options={[
            { value: 'mine', label: 'Mine' },
            { value: 'global', label: 'Global' },
            { value: 'all', label: 'All I see' },
          ]}
        />
        <label className="flex items-center gap-1.5 rounded border bg-background px-2">
          <Search className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
          <input type="search" value={find} onChange={(e) => setFind(e.target.value)} placeholder="Find a project" aria-label="Find a project" className="h-7 min-w-0 flex-1 bg-transparent text-xs outline-none" />
        </label>
      </div>
      <div className="min-h-0 flex-1 overflow-auto px-1.5 pb-3 text-xs">
        {projects.isPending ? (
          <p className="px-1.5 text-muted-foreground">Asking Arena…</p>
        ) : projects.error ? (
          <p className="px-1.5 text-muted-foreground" role="alert">
            {signedOut ? 'Projects are in Arena: sign in to Arena (the Accounts menu, or code-arena login) to see them.' : errorMessage(projects.error)}
          </p>
        ) : shown.length === 0 ? (
          <div className="grid gap-2 px-1.5 text-muted-foreground">
            <p className="flex items-start gap-1.5">
              <FolderKanban className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
              {find.trim() ? 'No project by that name.' : show === 'global' ? 'No global projects yet.' : 'You are in no project yet.'}
            </p>
            {may.data?.create && !find.trim() && (
              <button type="button" onClick={() => setMaking(true)} className="flex h-7 w-fit items-center gap-1 rounded bg-primary px-2 text-primary-foreground">
                <Plus className="size-3.5" aria-hidden="true" /> New project
              </button>
            )}
          </div>
        ) : (
          <ul className="grid gap-0.5" aria-label="Projects">
            {shown.map((p) => (
              <ProjectRow
                key={p.id}
                project={p}
                session={p.id === session?.id}
                repository={p.id === suggested}
                onPut={mayAdd(p.role) && p.status === 'active' && p.id !== session?.id ? () => put.mutate(p.id) : undefined}
                putting={put.isPending}
              />
            ))}
          </ul>
        )}
      </div>
      <NewProjectDialog open={making} onOpenChange={setMaking} mayGlobal={!!may.data?.global} onMade={(id, name) => openProject({ id, name })} />
    </div>
  )
}

function ProjectRow({ project: p, session, repository, onPut, putting }: { project: ProjectSummary; session: boolean; repository: boolean; onPut?: () => void; putting: boolean }) {
  const progress = progressOf(p)
  const marks = [session && 'this session', repository && "this folder's repository"].filter(Boolean).join(', ')
  return (
    <li className="group flex items-start gap-1 rounded px-1.5 py-1 hover:bg-accent/50" data-current={session || undefined}>
      <button
        type="button"
        onClick={() => openProject(p)}
        aria-label={`${p.name}${marks ? ` (${marks})` : ''}`}
        className="grid min-w-0 flex-1 gap-0.5 rounded-sm text-left outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        <span className="flex min-w-0 items-center gap-1.5">
          <FolderKanban className={cn('size-3.5 shrink-0', session ? 'text-primary-ink' : 'text-muted-foreground')} aria-hidden="true" />
          <bdi className={cn('min-w-0 truncate', session && 'font-semibold')}>{p.name}</bdi>
          {p.global && <Globe2 className="size-3 shrink-0 text-muted-foreground" aria-label="Global" />}
          {p.status === 'archived' && <span className="shrink-0 rounded border px-1 text-[0.625rem] text-muted-foreground">Archived</span>}
        </span>
        <span className="flex flex-wrap gap-x-2 ps-5 text-muted-foreground">
          <span>{roleLabels[p.role]}</span>
          {progress !== null && <span>{progress}% of its goals</span>}
          <span>
            {money(p.spentThisMonth)} this month{p.monthlyBudget ? ` of ${money(p.monthlyBudget)}` : ''}
          </span>
        </span>
        {marks && <span className="ps-5 text-primary-ink">{session ? 'This session counts to it' : "This folder's repository is in it"}</span>}
      </button>
      {onPut && (
        <Tooltip content="Put this session in it">
          <button type="button" aria-label={`Put this session in ${p.name}`} onClick={onPut} disabled={putting} className={cn(action, 'opacity-0 group-hover:opacity-100 focus-visible:opacity-100')}>
            <FolderInput />
          </button>
        </Tooltip>
      )}
    </li>
  )
}
