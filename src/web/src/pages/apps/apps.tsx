import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AppWindow, ArrowLeft, Copy, GitFork, History, Pencil, Trash2 } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { LivePreview } from '../chat/live-preview'
import { appArena } from './arena-host'
import { appQuery, appsQuery, changeApp, reachLabels, remixApp, removeApp, restoreVersion, versionsQuery, type AppBrief, type AppReach } from './apps-api'

/** Workspace → Apps: the apps you may open (yours, your groups', everyone's), the most used first. */
export function AppsPage() {
  const apps = useQuery(appsQuery)
  const [shown, setShown] = useState<'all' | 'mine'>('all')
  if (apps.isPending) return <PageSkeleton />
  if (apps.error) return <QueryError error={apps.error} retry={() => apps.refetch()} />
  const list = shown === 'mine' ? apps.data.filter((a) => a.mine) : apps.data
  return (
    <>
      <PageHeader
        title="Apps"
        description="What the chat built, published to use: a page or a component that asks the model and keeps its data, as the person using it. Publish one from a preview in the chat (Publish as app)."
      />
      <Segmented label="Apps shown" value={shown} onChange={setShown} options={[{ value: 'all', label: 'All I can open' }, { value: 'mine', label: 'Mine' }]} />
      {list.length === 0 ? (
        <EmptyState icon={AppWindow} title="No apps yet" className="mt-4">
          Ask the chat to build one (a page or a React component), open its preview, and press Publish as app.
        </EmptyState>
      ) : (
        <ul className="mt-4 grid gap-3 sm:grid-cols-2 xl:grid-cols-3" aria-label="Apps">
          {list.map((a) => (
            <li key={a.id}>
              <Link to={`/apps/${a.id}`} className="block h-full rounded-xl outline-none focus-visible:ring-[3px] focus-visible:ring-ring">
                <Card className="h-full transition-colors hover:bg-accent/40">
                  <CardHeader>
                    <CardTitle className="flex items-center gap-2 text-base">
                      <AppWindow className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                      <span className="truncate">{a.name}</span>
                    </CardTitle>
                    <CardDescription className="line-clamp-2">{a.description || (a.kind === 'react' ? 'A component.' : 'A page.')}</CardDescription>
                  </CardHeader>
                  <CardContent className="flex flex-wrap items-center gap-1.5 text-xs text-muted-foreground">
                    <span>{a.mine ? 'Yours' : `By ${a.owner ?? 'someone'}`}</span>
                    <span>· version {a.version}</span>
                    {a.opens > 0 && <span>· opened {a.opens} {a.opens === 1 ? 'time' : 'times'}</span>}
                    {a.mine && <Badge variant="outline">{reachLabels[a.reach]}</Badge>}
                  </CardContent>
                </Card>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </>
  )
}

/** Its name, words and who may open it, for its owner. */
function EditDialog({ app, onClose }: { app: AppBrief; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [name, setName] = useState(app.name)
  const [description, setDescription] = useState(app.description)
  const [reach, setReach] = useState<AppReach>(app.reach === 'groups' ? 'company' : app.reach)
  const [error, setError] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: () => changeApp(app.id, { name: name.trim(), description: description.trim(), ...(app.reach === 'groups' && reach === 'company' ? {} : { reach }) }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['apps'] })
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  return (
    <form
      className="grid gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        save.mutate()
      }}
    >
      <DialogHeader>
        <DialogTitle>{app.name}</DialogTitle>
        <DialogDescription>Its code changes from the chat: publish a new version there, or put an old one back under Versions.</DialogDescription>
      </DialogHeader>
      {error && <Alert variant="destructive">{error}</Alert>}
      <Field label="Name">
        <Input value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />
      </Field>
      <Field label="What it is for">
        <Textarea rows={2} maxLength={500} value={description} onChange={(e) => setDescription(e.target.value)} />
      </Field>
      {app.reach !== 'groups' && (
        <Segmented label="Who may open it" value={reach} onChange={setReach} options={(['private', 'company'] as const).map((r) => ({ value: r, label: reachLabels[r] }))} />
      )}
      <DialogFooter>
        <Button type="button" variant="outline" onClick={onClose}>
          Cancel
        </Button>
        <Button type="submit" loading={save.isPending} disabled={!name.trim()}>
          Save
        </Button>
      </DialogFooter>
    </form>
  )
}

/** Every version of its code, newest first; an old one put back as a new version. */
function VersionsDialog({ app, onClose }: { app: AppBrief; onClose: () => void }) {
  const queryClient = useQueryClient()
  const versions = useQuery(versionsQuery(app.id))
  const restore = useMutation({
    mutationFn: (v: number) => restoreVersion(app.id, v),
    onSuccess: (a) => {
      toast.success(`Version ${a.version} is the one before, put back`)
      void queryClient.invalidateQueries({ queryKey: ['apps'] })
      onClose()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <>
      <DialogHeader>
        <DialogTitle>Versions of {app.name}</DialogTitle>
        <DialogDescription>Each change of its code is a version. Putting one back makes it the newest.</DialogDescription>
      </DialogHeader>
      <ul className="grid gap-1.5 text-sm" aria-label="Versions">
        {(versions.data ?? []).map((v) => (
          <li key={v.version} className="flex items-center gap-2 rounded-md border px-3 py-2">
            <span className="flex-1">
              <span className="font-medium">Version {v.version}</span>
              <span className="block text-xs text-muted-foreground">
                {new Date(v.createdAt).toLocaleString()}
                {v.note ? ` · ${v.note}` : ''}
              </span>
            </span>
            {v.version !== app.version ? (
              <Button size="sm" variant="outline" onClick={() => restore.mutate(v.version)} loading={restore.isPending && restore.variables === v.version}>
                Put back
              </Button>
            ) : (
              <Badge variant="secondary">Now</Badge>
            )}
          </li>
        ))}
      </ul>
    </>
  )
}

/** /apps/:id: the app, running, with what its person can do with it (and its owner: change, versions, delete). */
export function AppPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const loaded = useQuery(appQuery(id))
  const arena = useMemo(() => appArena(id), [id])
  const [dialog, setDialog] = useState<'edit' | 'versions' | null>(null)
  const remix = useMutation({
    mutationFn: () => remixApp(id),
    onSuccess: (copy) => {
      toast.success('Remixed: it is yours to change')
      void queryClient.invalidateQueries({ queryKey: ['apps'] })
      navigate(`/apps/${copy.id}`)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (loaded.isPending) return <PageSkeleton />
  if (loaded.error) return <QueryError error={loaded.error} retry={() => loaded.refetch()} />
  const { app, code } = loaded.data
  return (
    <div className="flex h-[calc(100dvh-7rem)] min-h-[32rem] flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <Button variant="ghost" size="icon-sm" asChild>
          <Link to="/apps" aria-label="All apps">
            <ArrowLeft />
          </Link>
        </Button>
        <div className="min-w-0 flex-1">
          <h1 className="truncate text-lg font-semibold">{app.name}</h1>
          <p className="truncate text-xs text-muted-foreground">
            {app.mine ? `Yours · ${reachLabels[app.reach]}` : `By ${app.owner ?? 'someone'}`} · version {app.version}
            {app.description ? ` · ${app.description}` : ''}
          </p>
        </div>
        <Button variant="outline" size="sm" onClick={() => void navigator.clipboard.writeText(window.location.href).then(() => toast.success('Link copied'))}>
          <Copy /> Copy link
        </Button>
        <Button variant="outline" size="sm" onClick={() => remix.mutate()} loading={remix.isPending}>
          <GitFork /> Remix
        </Button>
        {app.mine && (
          <>
            <Button variant="outline" size="sm" onClick={() => setDialog('edit')}>
              <Pencil /> Change
            </Button>
            <Button variant="outline" size="sm" onClick={() => setDialog('versions')}>
              <History /> Versions
            </Button>
            <Button
              variant="ghost"
              size="icon-sm"
              aria-label={`Delete ${app.name}`}
              onClick={async () => {
                if (await confirm({ title: `Delete ${app.name}?`, description: 'Its link stops working and its data goes with it.', confirm: 'Delete', destructive: true })) {
                  await removeApp(app.id).catch((e: unknown) => toast.error(errorMessage(e)))
                  void queryClient.invalidateQueries({ queryKey: ['apps'] })
                  navigate('/apps')
                }
              }}
            >
              <Trash2 />
            </Button>
          </>
        )}
      </div>
      <LivePreview kind={app.kind} code={code} name={app.name} arena={arena} className="flex min-h-0 flex-1 flex-col gap-2" />
      <Dialog open={dialog !== null} onOpenChange={(o) => !o && setDialog(null)}>
        <DialogContent className="sm:max-w-lg">
          {dialog === 'edit' && <EditDialog app={app} onClose={() => setDialog(null)} />}
          {dialog === 'versions' && <VersionsDialog app={app} onClose={() => setDialog(null)} />}
        </DialogContent>
      </Dialog>
    </div>
  )
}
