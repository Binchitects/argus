import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, LayoutDashboard, MessagesSquare, MoreHorizontal, Pencil, Plus, RefreshCw, Trash2 } from 'lucide-react'
import { useId, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { DataChart } from '@/components/charts/data-chart'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Segmented } from '@/components/app/segmented'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { EmptyState } from '@/components/ui/empty-state'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { listQuery, queueMessage } from '../chat/api'
import { pinMessage } from './post'
import { addBoard, boardQuery, boardsQuery, changeBoard, changePin, reachLabels, refreshLabels, refreshPin, removeBoard, unpin, type Board, type BoardReach, type Pin, type Refresh } from './data-api'

/** Data → Boards: the boards one may see (one's own, one's groups', everyone's), the latest changed first. */
export function BoardsPage() {
  const boards = useQuery(boardsQuery)
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [naming, setNaming] = useState(false)
  const [name, setName] = useState('')
  const add = useMutation({
    mutationFn: () => addBoard({ name: name.trim() }),
    onSuccess: (b) => {
      void queryClient.invalidateQueries({ queryKey: ['data', 'boards'] })
      navigate(`/data/boards/${b.id}`)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (boards.isPending) return <PageSkeleton />
  if (boards.error) return <QueryError error={boards.error} retry={() => boards.refetch()} />
  return (
    <>
      <PageHeader
        title="Boards"
        description="Charts pinned from answers, each run again on its schedule as the board's owner. Share a board with groups or everyone; post a chart to a room."
        actions={
          <>
            <Button variant="outline" asChild>
              <Link to="/data">
                <ArrowLeft /> Ask
              </Link>
            </Button>
            <Button onClick={() => setNaming(true)}>
              <Plus /> New board
            </Button>
          </>
        }
      />
      {boards.data.length === 0 ? (
        <EmptyState icon={LayoutDashboard} title="No boards yet">
          Ask a question under Data, then press Pin to a board on its answer.
        </EmptyState>
      ) : (
        <ul className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3" aria-label="Boards">
          {boards.data.map((b) => (
            <li key={b.id}>
              <Link to={`/data/boards/${b.id}`} className="block h-full rounded-xl outline-none focus-visible:ring-[3px] focus-visible:ring-ring">
                <Card className="h-full transition-colors hover:bg-accent/40">
                  <CardHeader>
                    <CardTitle className="flex items-center gap-2 text-base">
                      <LayoutDashboard className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                      <span className="truncate">{b.name}</span>
                    </CardTitle>
                    {b.description && <CardDescription className="line-clamp-2">{b.description}</CardDescription>}
                  </CardHeader>
                  <CardContent className="flex flex-wrap items-center gap-1.5 text-xs text-muted-foreground">
                    <span>{b.mine ? 'Yours' : `By ${b.owner ?? 'someone'}`}</span>
                    <span>
                      · {b.pins} {b.pins === 1 ? 'chart' : 'charts'}
                    </span>
                    {b.mine && <Badge variant="outline">{reachLabels[b.reach]}</Badge>}
                  </CardContent>
                </Card>
              </Link>
            </li>
          ))}
        </ul>
      )}
      <Dialog open={naming} onOpenChange={setNaming}>
        <DialogContent className="sm:max-w-md">
          <form
            className="grid gap-4"
            onSubmit={(e) => {
              e.preventDefault()
              add.mutate()
            }}
          >
            <DialogHeader>
              <DialogTitle>New board</DialogTitle>
              <DialogDescription>Charts come onto it from answers: Pin to a board.</DialogDescription>
            </DialogHeader>
            <Field label="Name">
              <Input value={name} maxLength={100} onChange={(e) => setName(e.target.value)} placeholder="Sales this week" />
            </Field>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setNaming(false)}>
                Cancel
              </Button>
              <Button type="submit" loading={add.isPending} disabled={!name.trim()}>
                Make it
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
    </>
  )
}

/** Its name, words and who may see it, for its owner. */
function BoardForm({ board, onClose }: { board: Board; onClose: () => void }) {
  const queryClient = useQueryClient()
  const id = useId()
  const [name, setName] = useState(board.name)
  const [description, setDescription] = useState(board.description)
  const [reach, setReach] = useState<BoardReach>(board.reach)
  const [groupIds, setGroupIds] = useState<string[]>(board.groupIds)
  const [error, setError] = useState<string | null>(null)
  const groups = useQuery({ queryKey: ['sharing', 'groups'], queryFn: ({ signal }) => api<{ id: string; name: string }[]>('/api/sharing/groups', { signal }), enabled: reach === 'groups' })
  const save = useMutation({
    mutationFn: () => changeBoard(board.id, { name: name.trim(), description: description.trim(), reach, groupIds: reach === 'groups' ? groupIds : [] }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['data', 'boards'] })
      onClose()
    },
    onError: (e) => setError(errorMessage(e)),
  })
  return (
    <form
      className="grid gap-4"
      onSubmit={(e) => {
        e.preventDefault()
        setError(null)
        save.mutate()
      }}
    >
      <DialogHeader>
        <DialogTitle>{board.name}</DialogTitle>
        <DialogDescription>Who you share it with sees its charts as they last ran, as you: what you may ask, they see.</DialogDescription>
      </DialogHeader>
      {error && <Alert variant="destructive">{error}</Alert>}
      <Field label="Name">
        <Input value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />
      </Field>
      <Field label="What it is for (optional)">
        <Textarea rows={2} maxLength={500} value={description} onChange={(e) => setDescription(e.target.value)} />
      </Field>
      <Segmented label="Who may see it" value={reach} onChange={setReach} options={(['private', 'groups', 'company'] as const).map((r) => ({ value: r, label: reachLabels[r] }))} />
      {reach === 'groups' && (
        <fieldset className="grid gap-1.5">
          <legend className="mb-1 text-sm font-medium">Groups</legend>
          {(groups.data ?? []).map((g) => (
            <div key={g.id} className="flex items-center gap-2 text-sm">
              <Checkbox id={`${id}-${g.id}`} checked={groupIds.includes(g.id)} onCheckedChange={(on) => setGroupIds(on ? [...groupIds, g.id] : groupIds.filter((x) => x !== g.id))} />
              <label htmlFor={`${id}-${g.id}`}>{g.name}</label>
            </div>
          ))}
          {groups.data?.length === 0 && <p className="text-xs text-muted-foreground">You are in no group.</p>}
        </fieldset>
      )}
      <DialogFooter>
        <Button type="button" variant="outline" onClick={onClose}>
          Cancel
        </Button>
        <Button type="submit" loading={save.isPending} disabled={!name.trim() || (reach === 'groups' && groupIds.length === 0)}>
          Save
        </Button>
      </DialogFooter>
    </form>
  )
}

function PostDialog({ pin, board, onClose }: { pin: Pin; board: string; onClose: () => void }) {
  const rooms = useQuery({ ...listQuery(''), select: (all) => all.filter((c) => c.isRoom) })
  const [room, setRoom] = useState<string | null>(null)
  const chosen = room ?? rooms.data?.[0]?.id ?? ''
  const post = useMutation({
    mutationFn: () => queueMessage(chosen, { content: pinMessage(pin, board), attachments: [] }),
    onSuccess: () => {
      toast.success('Posted to the room')
      onClose()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <>
      <DialogHeader>
        <DialogTitle>Post {pin.title} to a room</DialogTitle>
        <DialogDescription>Its last result goes to the room as a table, from you, for its people (not to the model).</DialogDescription>
      </DialogHeader>
      {rooms.data?.length === 0 ? (
        <p className="text-sm text-muted-foreground">You are in no room yet.</p>
      ) : (
        <Field label="Room">
          <Select value={chosen} onValueChange={setRoom}>
            <SelectTrigger>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {(rooms.data ?? []).map((r) => (
                <SelectItem key={r.id} value={r.id}>
                  {r.title}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>
      )}
      <DialogFooter>
        <Button variant="outline" onClick={onClose}>
          Cancel
        </Button>
        <Button onClick={() => post.mutate()} loading={post.isPending} disabled={!chosen}>
          Post
        </Button>
      </DialogFooter>
    </>
  )
}

/** /data/boards/:id: its charts, each as it last ran; its owner refreshes, changes and removes them. */
export function BoardPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const loaded = useQuery(boardQuery(id))
  const [editing, setEditing] = useState(false)
  const [posting, setPosting] = useState<Pin | null>(null)
  const changed = () => void queryClient.invalidateQueries({ queryKey: ['data', 'boards'] })
  const refresh = useMutation({ mutationFn: refreshPin, onSuccess: changed, onError: (e) => toast.error(errorMessage(e)) })
  const schedule = useMutation({ mutationFn: ({ pin, every }: { pin: string; every: Refresh }) => changePin(pin, { refresh: every }), onSuccess: changed, onError: (e) => toast.error(errorMessage(e)) })
  const remove = useMutation({ mutationFn: unpin, onSuccess: changed, onError: (e) => toast.error(errorMessage(e)) })
  if (loaded.isPending) return <PageSkeleton />
  if (loaded.error) return <QueryError error={loaded.error} retry={() => loaded.refetch()} />
  const { board, pins } = loaded.data
  return (
    <>
      <PageHeader
        title={board.name}
        description={`${board.mine ? `Yours · ${reachLabels[board.reach]}` : `By ${board.owner ?? 'someone'}`}${board.description ? ` · ${board.description}` : ''}`}
        actions={
          <>
            <Button variant="outline" asChild>
              <Link to="/data/boards">
                <ArrowLeft /> Boards
              </Link>
            </Button>
            {board.mine && (
              <>
                <Button variant="outline" onClick={() => setEditing(true)}>
                  <Pencil /> Change
                </Button>
                <Button
                  variant="ghost"
                  size="icon"
                  aria-label={`Delete ${board.name}`}
                  onClick={async () => {
                    if (await confirm({ title: `Delete ${board.name}?`, description: 'Its charts go with it; the answers they came from are not touched.', confirm: 'Delete', destructive: true })) {
                      await removeBoard(board.id).catch((e: unknown) => toast.error(errorMessage(e)))
                      changed()
                      navigate('/data/boards')
                    }
                  }}
                >
                  <Trash2 />
                </Button>
              </>
            )}
          </>
        }
      />
      {pins.length === 0 ? (
        <EmptyState icon={LayoutDashboard} title="No charts on it yet">
          Ask a question under Data, then press Pin to a board on its answer.
        </EmptyState>
      ) : (
        <ul className="grid gap-4 lg:grid-cols-2" aria-label="Charts">
          {pins.map((p) => (
            <li key={p.id}>
              <Card className="h-full">
                <CardHeader>
                  <CardTitle className="min-w-0 truncate text-base">{p.title}</CardTitle>
                  <CardDescription className="min-w-0 truncate text-xs">
                    {p.source ?? 'A database'} · {refreshLabels[p.refresh]}
                    {p.ranAt ? ` · as of ${new Date(p.ranAt).toLocaleString()}` : ''}
                  </CardDescription>
                  <CardAction>
                    <DropdownMenu>
                      <DropdownMenuTrigger asChild>
                        <Button variant="ghost" size="icon-sm" aria-label={`More for ${p.title}`}>
                          <MoreHorizontal />
                        </Button>
                      </DropdownMenuTrigger>
                      <DropdownMenuContent align="end">
                        {p.result && (
                          <DropdownMenuItem onSelect={() => setPosting(p)}>
                            <MessagesSquare /> Post to a room
                          </DropdownMenuItem>
                        )}
                        {board.mine && (
                          <>
                            <DropdownMenuItem onSelect={() => refresh.mutate(p.id)}>
                              <RefreshCw /> Run it now
                            </DropdownMenuItem>
                            {(['hourly', 'daily', 'off'] as const)
                              .filter((r) => r !== p.refresh)
                              .map((r) => (
                                <DropdownMenuItem key={r} onSelect={() => schedule.mutate({ pin: p.id, every: r })}>
                                  Run again: {refreshLabels[r].toLowerCase()}
                                </DropdownMenuItem>
                              ))}
                            <DropdownMenuItem onSelect={() => remove.mutate(p.id)} className="text-destructive-ink">
                              <Trash2 /> Take it off the board
                            </DropdownMenuItem>
                          </>
                        )}
                      </DropdownMenuContent>
                    </DropdownMenu>
                  </CardAction>
                </CardHeader>
                <CardContent className="grid gap-2">
                  {p.error && <Alert variant="destructive">{p.error}</Alert>}
                  {p.result ? <DataChart result={p.result} chart={p.chart} label={p.title} height={240} /> : !p.error && <p className="text-sm text-muted-foreground">Not run yet.</p>}
                </CardContent>
              </Card>
            </li>
          ))}
        </ul>
      )}
      <Dialog open={editing || posting !== null} onOpenChange={(o) => !o && (setEditing(false), setPosting(null))}>
        <DialogContent className="sm:max-w-lg">
          {editing && <BoardForm board={board} onClose={() => setEditing(false)} />}
          {posting && <PostDialog pin={posting} board={board.name} onClose={() => setPosting(null)} />}
        </DialogContent>
      </Dialog>
    </>
  )
}
