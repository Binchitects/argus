import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { CalendarClock, Plus, Workflow as WorkflowIcon } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { triggerIcons, when } from './flow-words'
import { createWorkflow, fromTask, templatesQuery, triggerLabels, workflowsQuery, type Flow, type Template } from './studio-api'

const blank: Flow = { trigger: { kind: 'manual' }, steps: [{ id: 's1', kind: 'ask', name: 'Ask', prompt: 'Answer briefly: {{input.text}}' }] }

/** Workspace → Agent Studio: one's workflows, those shared to fill in, and templates to start from. */
export function StudioPage() {
  const list = useQuery(workflowsQuery)
  const templates = useQuery(templatesQuery)
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [tasks, setTasks] = useState(false)
  const make = useMutation({
    mutationFn: (t: { name: string; description?: string; flow: Flow }) => createWorkflow({ ...t, enabled: false }),
    onSuccess: (made) => {
      void queryClient.invalidateQueries({ queryKey: ['studio'] })
      navigate(`/studio/${made.workflow.id}`)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (list.isPending) return <PageSkeleton />
  if (list.error) return <QueryError error={list.error} retry={() => list.refetch()} />
  const mine = list.data.filter((w) => w.mine)
  const shared = list.data.filter((w) => !w.mine)
  return (
    <>
      <PageHeader
        title="Agent Studio"
        description="Workflows that do the work as you: started by hand, on a schedule, by a webhook, GitLab, a room's message or a form; steps that ask a model, use tools, decide, loop and wait for your yes; what they give out goes to a room, an email, GitLab or a file. Every run is kept, to read and replay."
        actions={
          <>
            <Button variant="outline" onClick={() => setTasks(true)}>
              <CalendarClock /> From a scheduled task
            </Button>
            <Button onClick={() => make.mutate({ name: 'A new workflow', flow: blank })} loading={make.isPending}>
              <Plus /> New workflow
            </Button>
          </>
        }
      />
      {mine.length === 0 ? (
        <EmptyState icon={WorkflowIcon} title="No workflows yet">
          Start from a template below, or a blank one, and change it on the canvas.
        </EmptyState>
      ) : (
        <ul className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3" aria-label="Your workflows">
          {mine.map((w) => {
            const Icon = triggerIcons[w.trigger]
            return (
              <li key={w.id}>
                <Link to={`/studio/${w.id}`} className="block h-full rounded-xl outline-none focus-visible:ring-[3px] focus-visible:ring-ring">
                  <Card className="h-full transition-colors hover:bg-accent/40">
                    <CardHeader>
                      <CardTitle className="flex items-center gap-2 text-base">
                        <Icon className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                        <span className="truncate">{w.name}</span>
                      </CardTitle>
                      {w.description && <CardDescription className="line-clamp-2">{w.description}</CardDescription>}
                    </CardHeader>
                    <CardContent className="flex flex-wrap items-center gap-1.5 text-xs text-muted-foreground">
                      <Badge variant={w.enabled ? 'secondary' : 'outline'}>{w.enabled ? 'On' : 'Off'}</Badge>
                      <span>{triggerLabels[w.trigger]}</span>
                      {w.lastRunAt && <span>· last ran {new Date(w.lastRunAt).toLocaleString()}</span>}
                    </CardContent>
                  </Card>
                </Link>
              </li>
            )
          })}
        </ul>
      )}
      {shared.length > 0 && (
        <section className="mt-8" aria-labelledby="shared-heading">
          <h2 id="shared-heading" className="mb-3 text-sm font-semibold">Shared with you</h2>
          <ul className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3" aria-label="Shared with you">
            {shared.map((w) => (
              <li key={w.id}>
                <Link to={`/studio/${w.id}`} className="block rounded-xl border bg-card p-4 text-sm hover:bg-accent/40">
                  <span className="font-medium">{w.name}</span>
                  <span className="block text-xs text-muted-foreground">
                    By {w.owner ?? 'someone'} · {triggerLabels[w.trigger]}
                  </span>
                </Link>
              </li>
            ))}
          </ul>
        </section>
      )}
      <section className="mt-8" aria-labelledby="templates-heading">
        <h2 id="templates-heading" className="mb-3 text-sm font-semibold">Start from a template</h2>
        <ul className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3" aria-label="Templates">
          {(templates.data ?? []).map((t: Template) => {
            const Icon = triggerIcons[t.flow.trigger.kind]
            return (
              <li key={t.key}>
                <button
                  type="button"
                  disabled={make.isPending}
                  onClick={() => make.mutate({ name: t.name, description: t.description, flow: t.flow })}
                  className="flex h-full w-full flex-col gap-1 rounded-xl border bg-card p-4 text-left text-sm transition-colors hover:bg-accent/40 focus-visible:ring-[3px] focus-visible:ring-ring focus-visible:outline-none"
                >
                  <span className="flex items-center gap-2 font-medium">
                    <Icon className="size-4 text-muted-foreground" aria-hidden="true" /> {t.name}
                  </span>
                  <span className="text-xs text-muted-foreground">{t.description}</span>
                  <span className="mt-1 text-[0.7rem] text-muted-foreground">
                    {triggerLabels[t.flow.trigger.kind]} · {when(t.flow.trigger)} · {t.flow.steps.length} steps
                  </span>
                </button>
              </li>
            )
          })}
        </ul>
      </section>
      <Dialog open={tasks} onOpenChange={setTasks}>
        <DialogContent className="sm:max-w-md">{tasks && <TaskPicker onDone={(id) => navigate(`/studio/${id}`)} />}</DialogContent>
      </Dialog>
    </>
  )
}

/** One of one's scheduled tasks, made a workflow (it starts off: the task keeps running until it is switched off). */
function TaskPicker({ onDone }: { onDone: (id: string) => void }) {
  const tasks = useQuery({ queryKey: ['tasks'], queryFn: ({ signal }) => api<{ tasks: { id: string; name: string; prompt: string }[] }>('/api/tasks', { signal }) })
  const make = useMutation({ mutationFn: fromTask, onSuccess: (made) => onDone(made.workflow.id), onError: (e) => toast.error(errorMessage(e)) })
  return (
    <>
      <DialogHeader>
        <DialogTitle>From a scheduled task</DialogTitle>
        <DialogDescription>Its trigger, its question and tools as an agent step, its email and GitLab answer as steps. The workflow starts off: switch the task off when it is on.</DialogDescription>
      </DialogHeader>
      <ul className="grid gap-1.5" aria-label="Your scheduled tasks">
        {(tasks.data?.tasks ?? []).map((t) => (
          <li key={t.id}>
            <Button variant="outline" className="h-auto w-full justify-start py-2 text-left" onClick={() => make.mutate(t.id)} loading={make.isPending && make.variables === t.id}>
              <span className="min-w-0">
                <span className="block truncate font-medium">{t.name}</span>
                <span className="block truncate text-xs font-normal text-muted-foreground">{t.prompt}</span>
              </span>
            </Button>
          </li>
        ))}
        {tasks.data?.tasks.length === 0 && <li className="text-sm text-muted-foreground">You have no scheduled tasks.</li>}
      </ul>
    </>
  )
}
