import { QueryClient, QueryClientProvider, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Code2, FolderGit2, Loader2, Moon, Plus, Power, Trash2 } from 'lucide-react'
import { lazy, Suspense, useEffect, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { setIdeBase } from '@/code-arena/http'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { createWorkspace, deleteWorkspace, startWorkspace, stopWorkspace, workspacesQuery } from './api'
import { TokenCard } from './fixes'

const when = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

/** Code Studio's workspaces: one's own, each opened in the IDE; a new one from a GitLab project and branch. */
export function Workspaces() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const list = useQuery(workspacesQuery)
  const [project, setProject] = useState('')
  const [branch, setBranch] = useState('')
  const make = useMutation({
    mutationFn: () => createWorkspace({ project: project.trim(), branch: branch.trim() || undefined }),
    onSuccess: (w) => {
      void queryClient.invalidateQueries({ queryKey: workspacesQuery.queryKey })
      navigate(`/code/w/${w.id}`)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: (id: string) => deleteWorkspace(id),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: workspacesQuery.queryKey }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (list.isPending) return <Skeleton className="h-40 rounded-xl" />
  if (!list.data) return null
  const { server, workspaces } = list.data
  return (
    <section className="grid gap-4" aria-label="Workspaces">
      <div className="flex flex-wrap items-end justify-between gap-2">
        <div>
          <h2 className="text-lg font-semibold">Workspaces</h2>
          <p className="text-sm text-muted-foreground">A project and branch in an IDE like VS Code: edit, run, debug, commit and push, with the agent beside you.</p>
        </div>
      </div>
      {!server ? (
        <Alert>Workspaces on the server are not set up here: an admin turns on the workspaces module. Code Arena on your own machine works meanwhile (Connect your tools).</Alert>
      ) : (
        <>
          <TokenCard />
          <Card>
            <CardHeader>
              <CardTitle className="text-base">New workspace</CardTitle>
              <CardDescription>A GitLab project you can read and push to, cloned on the server with your own token.</CardDescription>
            </CardHeader>
            <CardContent>
              <form
                className="flex flex-wrap items-end gap-2"
                aria-label="New workspace"
                onSubmit={(e) => {
                  e.preventDefault()
                  make.mutate()
                }}
              >
                <Field label="Project" className="min-w-56 flex-1">
                  <Input value={project} onChange={(e) => setProject(e.target.value)} placeholder="group/project" />
                </Field>
                <Field label="Branch" className="w-48" hint="Empty: its default branch.">
                  <Input value={branch} onChange={(e) => setBranch(e.target.value)} placeholder="main" />
                </Field>
                <Button type="submit" loading={make.isPending} disabled={!project.trim().includes('/')}>
                  <Plus /> Open it
                </Button>
              </form>
            </CardContent>
          </Card>
        </>
      )}
      {workspaces.length > 0 && (
        <ul className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3" aria-label="Your workspaces">
          {workspaces.map((w) => (
            <li key={w.id} className="flex items-start gap-3 rounded-xl border bg-card p-4">
              <FolderGit2 className="mt-0.5 size-5 shrink-0 text-muted-foreground" aria-hidden="true" />
              <div className="min-w-0 flex-1">
                <Link to={`/code/w/${w.id}`} className="block truncate font-medium hover:underline">
                  {w.name}
                </Link>
                <p className="truncate text-xs text-muted-foreground">
                  {w.project} · {w.branch} · used {when.format(new Date(w.usedAt))}
                </p>
                <div className="mt-2 flex items-center gap-2">
                  <Badge variant={w.state === 'running' ? 'success' : w.state === 'failed' ? 'destructive' : 'secondary'}>
                    {w.state === 'running' ? 'Running' : w.state === 'failed' ? 'Failed' : 'Asleep'}
                  </Badge>
                  <Button size="sm" variant="ghost" className="ml-auto" asChild>
                    <Link to={`/code/w/${w.id}`}>
                      <Code2 /> Open
                    </Link>
                  </Button>
                  <Button
                    size="icon-sm"
                    variant="ghost"
                    aria-label={`Delete ${w.name}`}
                    onClick={async () => {
                      if (await confirm({ title: `Delete ${w.name}?`, description: 'Its files on the server go, with what was not pushed.', confirm: 'Delete', destructive: true })) remove.mutate(w.id)
                    }}
                  >
                    <Trash2 />
                  </Button>
                </div>
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

const Ide = lazy(() => import('@/code-arena/app').then((m) => ({ default: m.App })))

/** A workspace's IDE: the whole window, its requests through Arena to the workspace's engine. */
function StudioIde({ id }: { id: string }) {
  // Its own cache: another workspace's files and state never show here.
  const [client] = useState(() => new QueryClient({ defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: false } } }))
  // Set before the IDE's first request, and back when it closes.
  useState(() => setIdeBase(`/api/studio/w/${id}`))
  useEffect(() => () => setIdeBase(''), [])
  return (
    <QueryClientProvider client={client}>
      <Suspense fallback={<Skeleton className="h-full w-full" />}>
        <Ide />
      </Suspense>
    </QueryClientProvider>
  )
}

/** Code Studio → a workspace: started when it sleeps, then its IDE, under a bar to go back, put it to sleep or see where it is. */
export function WorkspacePage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const list = useQuery(workspacesQuery)
  const w = list.data?.workspaces.find((x) => x.id === id)
  const start = useMutation({
    mutationFn: () => startWorkspace(id),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: workspacesQuery.queryKey }),
  })
  const ready = w?.state === 'running' || start.isSuccess
  const stop = useMutation({
    mutationFn: () => stopWorkspace(id),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: workspacesQuery.queryKey })
      navigate('/code')
    },
  })
  // Asleep or failed: started once (cloned the first time); running: opened at once.
  useEffect(() => {
    if (w && w.state !== 'running' && start.isIdle) start.mutate()
  }, [w, start])

  return (
    <div className="flex h-dvh flex-col bg-background">
      <header className="flex h-9 shrink-0 items-center gap-2 border-b bg-sidebar px-2 text-sm">
        <Button size="sm" variant="ghost" asChild>
          <Link to="/code">
            <ArrowLeft /> Code Studio
          </Link>
        </Button>
        <span className="min-w-0 truncate font-medium">{w?.name ?? 'Workspace'}</span>
        {w && (
          <span className="hidden truncate text-xs text-muted-foreground sm:inline">
            {w.project} · {w.branch}
          </span>
        )}
        <Button size="sm" variant="ghost" className="ml-auto" disabled={!ready} loading={stop.isPending} onClick={() => stop.mutate()}>
          <Moon /> Put to sleep
        </Button>
      </header>
      <main className="min-h-0 flex-1 [&_.h-dvh]:h-full">
        {list.isPending ? (
          <Skeleton className="h-full w-full" />
        ) : !w ? (
          <div className="p-8">
            <Alert variant="destructive">This workspace is not yours, or it was deleted.</Alert>
          </div>
        ) : start.isError ? (
          <div className="grid max-w-xl gap-3 p-8">
            <Alert variant="destructive">{errorMessage(start.error)}</Alert>
            <div>
              <Button onClick={() => start.mutate()}>
                <Power /> Try again
              </Button>
            </div>
          </div>
        ) : !ready ? (
          <div className="flex h-full flex-col items-center justify-center gap-3 text-sm text-muted-foreground">
            <Loader2 className="size-6 animate-spin" aria-hidden="true" />
            <p>Starting {w.name}: the first time, its project is cloned.</p>
          </div>
        ) : (
          <StudioIde key={id} id={id} />
        )}
      </main>
    </div>
  )
}
