import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { FolderKanban, Globe2, Plus, Search, Users } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate, useOutletContext } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { api, errorMessage, type Me } from '@/lib/api'
import { money } from '@/lib/format'
import { progressOf, projectsQuery, roleLabels, type ProjectSummary } from './api'

type Show = 'mine' | 'global' | 'all'

/** Projects: those the person is in, the global ones everyone adds to, and a new one. */
export function ProjectsPage() {
  const me = useOutletContext<Me>()
  const projects = useQuery(projectsQuery)
  const [show, setShow] = useState<Show>('mine')
  const [q, setQ] = useState('')
  const [making, setMaking] = useState(false)
  const may = !me.denied?.includes('feature.projects')
  const needle = q.trim().toLowerCase()
  const all = projects.data ?? []
  const shown = all
    .filter((p) => (show === 'mine' ? p.mine : show === 'global' ? p.global : true))
    .filter((p) => !needle || p.name.toLowerCase().includes(needle) || p.description.toLowerCase().includes(needle))
    .sort((a, b) => Number(a.status === 'archived') - Number(b.status === 'archived') || b.updatedAt.localeCompare(a.updatedAt))
  return (
    <>
      <PageHeader
        title="Projects"
        description="Work several people do together, in the chat and in Code Arena: its people, repositories, chats and sessions, its goals, and what it used. Global projects are everyone's to add to."
        actions={
          may && (
            <Button onClick={() => setMaking(true)}>
              <Plus /> New project
            </Button>
          )
        }
      />
      <div className="mb-4 flex flex-wrap items-center gap-2">
        <Segmented
          label="Show"
          value={show}
          onChange={setShow}
          options={[
            { value: 'mine', label: 'Mine' },
            { value: 'global', label: 'Global' },
            { value: 'all', label: 'All I see' },
          ]}
        />
        <div className="relative min-w-0 flex-1 sm:max-w-xs">
          <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
          <Input type="search" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Find a project" aria-label="Find a project" className="h-9 pl-8" />
        </div>
      </div>
      {projects.error && <QueryError error={projects.error} retry={() => projects.refetch()} />}
      {projects.isPending && (
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
          {[0, 1, 2].map((i) => (
            <Skeleton key={i} className="h-36" />
          ))}
        </div>
      )}
      {projects.isSuccess && shown.length === 0 && (
        <EmptyState
          icon={FolderKanban}
          title={needle ? 'No project by that name' : show === 'global' ? 'No global projects yet' : 'You are in no project yet'}
          action={
            may && !needle ? (
              <Button variant="outline" onClick={() => setMaking(true)}>
                <Plus /> New project
              </Button>
            ) : undefined
          }
        >
          {show === 'mine' && all.some((p) => p.global) ? 'The global projects are everyone’s: show them above, and add your chats to one.' : 'A project gathers its people, chats and repositories, and counts what they used.'}
        </EmptyState>
      )}
      <ul className="stagger grid gap-3 sm:grid-cols-2 xl:grid-cols-3" aria-label="Projects">
        {shown.map((p) => (
          <li key={p.id}>
            <ProjectCard project={p} />
          </li>
        ))}
      </ul>
      <NewProjectDialog open={making} onOpenChange={setMaking} mayGlobal={!me.denied?.includes('feature.projects.global')} />
    </>
  )
}

function ProjectCard({ project: p }: { project: ProjectSummary }) {
  const progress = progressOf(p)
  return (
    <Card className="relative h-full transition-colors hover:border-ring/50">
      <CardContent className="grid h-full content-start gap-3 pt-5">
        <div className="flex min-w-0 items-start justify-between gap-2">
          <Link to={`/projects/${p.id}`} className="min-w-0 font-semibold leading-snug after:absolute after:inset-0 hover:underline focus-visible:underline">
            <bdi className="line-clamp-2">{p.name}</bdi>
          </Link>
          <div className="flex shrink-0 gap-1">
            {p.global && (
              <Badge variant="secondary" className="gap-1">
                <Globe2 className="size-3" aria-hidden="true" /> Global
              </Badge>
            )}
            {p.status === 'archived' && <Badge variant="outline">Archived</Badge>}
          </div>
        </div>
        {p.description && (
          <p dir="auto" className="line-clamp-2 text-sm text-muted-foreground">
            {p.description}
          </p>
        )}
        {progress !== null && (
          <div className="grid gap-1">
            <div className="flex justify-between text-xs text-muted-foreground">
              <span>
                Goals {p.goalsDone} of {p.goals}
              </span>
              <span>{progress}%</span>
            </div>
            <progress
              value={progress}
              max={100}
              aria-label={`${p.name}: goals done`}
              className="h-1.5 w-full appearance-none overflow-hidden rounded-full bg-muted [&::-moz-progress-bar]:bg-primary [&::-webkit-progress-bar]:bg-muted [&::-webkit-progress-value]:bg-primary"
            />
          </div>
        )}
        <div className="mt-auto flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted-foreground">
          <span className="inline-flex items-center gap-1">
            <Users className="size-3.5" aria-hidden="true" /> {p.global ? 'Everyone' : p.members}
          </span>
          <span>
            {p.chats} chat{p.chats === 1 ? '' : 's'}
          </span>
          <span>{money(p.spentThisMonth)} this month{p.monthlyBudget ? ` of ${money(p.monthlyBudget)}` : ''}</span>
          {p.dueOn && <span>Due {new Date(`${p.dueOn}T00:00:00`).toLocaleDateString(undefined, { dateStyle: 'medium' })}</span>}
          <Badge variant="outline" className="ms-auto">
            {roleLabels[p.role]}
          </Badge>
        </div>
      </CardContent>
    </Card>
  )
}

/** A new project: its name, what it is, its dates, and whether it is everyone's. */
function NewProjectDialog({ open, onOpenChange, mayGlobal }: { open: boolean; onOpenChange: (open: boolean) => void; mayGlobal: boolean }) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [form, setForm] = useState({ name: '', description: '', dueOn: '', global: false })
  const make = useMutation({
    mutationFn: () => api<{ id: string }>('/api/projects', { method: 'POST', body: { name: form.name, description: form.description, dueOn: form.dueOn || undefined, global: form.global } }),
    onSuccess: async ({ id }) => {
      await queryClient.invalidateQueries({ queryKey: ['projects'] })
      onOpenChange(false)
      setForm({ name: '', description: '', dueOn: '', global: false })
      void navigate(`/projects/${id}`)
    },
  })
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-lg">
        <form
          className="grid gap-4"
          onSubmit={(e) => {
            e.preventDefault()
            if (form.name.trim()) make.mutate()
          }}
        >
          <DialogHeader>
            <DialogTitle>New project</DialogTitle>
            <DialogDescription>You are its owner. Add its people, repositories and chats on its page.</DialogDescription>
          </DialogHeader>
          <Field label="Name">
            <Input value={form.name} maxLength={100} onChange={(e) => setForm({ ...form, name: e.target.value })} autoFocus required />
          </Field>
          <Field label="What it is for" hint="Optional. It heads the project's reports.">
            <Textarea value={form.description} maxLength={2000} rows={3} onChange={(e) => setForm({ ...form, description: e.target.value })} />
          </Field>
          <Field label="Due" hint="Optional.">
            <Input type="date" value={form.dueOn} onChange={(e) => setForm({ ...form, dueOn: e.target.value })} className="w-44" />
          </Field>
          {mayGlobal && (
            <div className="flex items-start justify-between gap-4 rounded-lg border p-3">
              <div className="grid gap-0.5">
                <label htmlFor="project-global" className="text-sm font-medium">
                  Global project
                </label>
                <p className="text-xs text-muted-foreground">Everyone sees it and may add their chats and sessions to it.</p>
              </div>
              <Switch id="project-global" checked={form.global} onCheckedChange={(v) => setForm({ ...form, global: v })} />
            </div>
          )}
          {make.error && (
            <p className="text-sm text-destructive" role="alert">
              {errorMessage(make.error)}
            </p>
          )}
          <DialogFooter>
            <Button type="button" variant="ghost" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button type="submit" disabled={!form.name.trim() || make.isPending}>
              Make it
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
