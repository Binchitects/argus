import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Eye, FolderOpen, GitFork, Link2Off, Unlink } from 'lucide-react'
import { useEffect, useMemo, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { Sheet, SheetContent, SheetDescription, SheetTitle } from '@/components/ui/sheet'
import { toast } from '@/components/ui/toaster'
import { Tooltip } from '@/components/ui/tooltip'
import { api, ApiError, errorMessage, infoQuery } from '@/lib/api'
import { useMedia } from '@/lib/use-media'
import { cn } from '@/lib/utils'
import { configQuery, sharedQuery } from './api'
import { collectFiles } from './files'
import { FilesPanel } from './files-panel'
import { opened } from './format'
import { ChatTree, toTurns } from './tree'
import { AnswerTurn, CompactedMark, QuestionTurn } from './turns'

/**
 * A chat shared with the person: its messages and files, read-only (no composer, no
 * tool approvals), its branches to switch between, and "Fork into my chats" to go on
 * in a chat of their own. Its owner sees how many opened it and revokes it here too.
 */
export function SharedChatPage() {
  const { shareId = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const shared = useQuery(sharedQuery(shareId))
  const config = useQuery(configQuery)
  const brand = useQuery(infoQuery).data?.name
  const wide = useMedia('(min-width: 1280px)')
  const [leaf, setLeaf] = useState<string | null>(null)
  const [filesOpen, setFilesOpen] = useState(false)
  const [selectedFile, setSelectedFile] = useState<string | null>(null)
  const [fileView, setFileView] = useState<'preview' | 'code'>('preview')
  const data = shared.data
  const tree = useMemo(() => new ChatTree(data?.messages ?? []), [data?.messages])
  const path = useMemo(() => tree.path(leaf ?? data?.currentLeafId ?? null), [tree, leaf, data?.currentLeafId])
  const files = useMemo(() => collectFiles(path), [path])
  const turns = toTurns(path)

  useEffect(() => {
    document.title = data ? `${data.title} · ${brand ?? 'Chat'}` : `Shared chat · ${brand ?? ''}`.trim()
  }, [data, brand])

  const fork = useMutation({
    mutationFn: (messageId?: string) => api<{ id: string; title: string }>(`/api/shared/${shareId}/fork`, { body: messageId ? { messageId } : {} }),
    onSuccess: (made) => {
      void queryClient.invalidateQueries({ queryKey: ['chat', 'list'] })
      navigate(`/chat/${made.id}`)
      toast.success(`Forked into “${made.title}”`, { description: 'It is your own chat now: write in it as in any other.' })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const revoke = useMutation({
    mutationFn: (chatId: string) => api(`/api/chat/conversations/${chatId}/share`, { method: 'DELETE' }),
    onSuccess: (_, chatId) => {
      void queryClient.invalidateQueries({ queryKey: ['chat', 'share', chatId] })
      toast.success('Link revoked', { description: 'It stops working at once.' })
      navigate(`/chat/${chatId}`)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })

  if (shared.error) {
    return (
      <div className="p-6">
        {shared.error instanceof ApiError && shared.error.http === 404 ? (
          <EmptyState icon={Link2Off} title="This link does not open" action={<Button asChild variant="outline"><Link to="/chat">Go to your chats</Link></Button>}>
            It was revoked, its chat was deleted, or it is not shared with you.
          </EmptyState>
        ) : (
          <QueryError error={shared.error} retry={() => shared.refetch()} />
        )}
      </div>
    )
  }
  if (!data || !config.data) {
    return (
      <div className="p-6">
        <PageSkeleton />
      </div>
    )
  }

  const openFile = (name: string) => {
    const f = [...files].reverse().find((x) => x.name === name)
    setSelectedFile(f?.key ?? null)
    setFileView('preview')
    setFilesOpen(true)
  }
  const openPreview = (code: string) => {
    const f = [...files].reverse().find((x) => x.kind === 'code' && x.code === code)
    setSelectedFile(f?.key ?? null)
    setFileView('preview')
    setFilesOpen(true)
  }
  const panel = (
    <FilesPanel
      files={files}
      title={data.title}
      selected={selectedFile}
      onSelect={(key) => {
        setSelectedFile(key)
        setFileView('preview')
      }}
      onClose={() => setFilesOpen(false)}
      view={fileView}
      onView={setFileView}
    />
  )
  return (
    <div className={cn('grid h-[calc(100dvh-3.5rem)] min-h-0 min-w-0', filesOpen && wide && 'grid-cols-[minmax(0,1fr)_26rem] 2xl:grid-cols-[minmax(0,1fr)_34rem]')}>
      <div className="flex min-h-0 min-w-0 flex-col">
        <header className="flex min-h-12 shrink-0 flex-wrap items-center gap-2 border-b px-3 py-1.5">
          <div className="grid min-w-0 flex-1">
            <h1 className="truncate text-sm font-semibold" title={data.title}>
              <bdi>{data.title}</bdi>
            </h1>
            <p className="truncate text-xs text-muted-foreground">
              {data.mine ? 'Shared by you' : `Shared by ${data.owner}`} · {data.branch ? 'one branch' : 'the whole chat'} · {new Date(data.sharedAt).toLocaleDateString()}
            </p>
          </div>
          <Badge variant="secondary">
            <Eye /> Read-only
          </Badge>
          <Tooltip content={filesOpen ? 'Hide files' : 'Files in this chat'}>
            <Button variant={filesOpen ? 'secondary' : 'ghost'} size="sm" className="h-8 gap-1.5" onClick={() => setFilesOpen(!filesOpen)} aria-pressed={filesOpen} aria-label={`Files (${files.length})`}>
              <FolderOpen /> <span className="tabular-nums">{files.length}</span>
            </Button>
          </Tooltip>
          {data.mine && data.conversationId && (
            <Button variant="ghost" size="sm" className="text-destructive-ink" loading={revoke.isPending} onClick={() => revoke.mutate(data.conversationId!)}>
              <Unlink /> Revoke link
            </Button>
          )}
          <Button size="sm" loading={fork.isPending} onClick={() => fork.mutate(undefined)}>
            <GitFork /> Fork into my chats
          </Button>
        </header>
        <div className="min-h-0 flex-1 overflow-y-auto">
          <div className="mx-auto grid w-full max-w-(--thread-max) gap-6 px-4 py-6 sm:px-6">
            {data.mine && data.link && (
              <output className="rounded-lg border bg-muted/40 px-3 py-2 text-sm">
                This is your chat, as the people it is shared with see it. {opened(data.link)}
              </output>
            )}
            {turns.length === 0 && <p className="text-sm text-muted-foreground">This chat has no messages yet.</p>}
            {turns.map((t, i) => {
              const summary = [t.question, ...t.answer].find((m) => m?.summary)?.summary
              return (
                <div key={t.question?.id ?? t.answer[0]?.id ?? i} className="grid gap-4">
                  {t.question && <QuestionTurn m={t.question} siblings={tree.siblings(t.question)} busy={false} onSwitch={(id) => setLeaf(tree.leafBelow(id))} />}
                  {t.answer.length > 0 && (
                    <AnswerTurn
                      answer={t.answer}
                      siblings={t.answer[0] ? tree.siblings(t.answer[0]) : []}
                      live={false}
                      thinkingSince={null}
                      notices={[]}
                      config={config.data}
                      question={t.question}
                      onSwitch={(id) => setLeaf(tree.leafBelow(id))}
                      onOpenFile={openFile}
                      onPreview={openPreview}
                      onFork={(messageId) => fork.mutate(messageId)}
                      busy={fork.isPending}
                    />
                  )}
                  {summary && <CompactedMark summary={summary} onOpenFile={openFile} />}
                </div>
              )
            })}
          </div>
        </div>
        <div className="mx-auto w-full max-w-(--thread-max) px-4 pb-4 sm:px-6">
          <p className="flex flex-wrap items-center justify-center gap-2 rounded-xl border border-dashed px-4 py-3 text-center text-sm text-muted-foreground">
            Read-only. Fork it to go on in a chat of your own.
            <Button variant="outline" size="sm" className="h-7" loading={fork.isPending} onClick={() => fork.mutate(undefined)}>
              <GitFork /> Fork into my chats
            </Button>
          </p>
        </div>
      </div>
      {filesOpen && wide && <div className="min-h-0 border-l">{panel}</div>}
      {!wide && (
        <Sheet open={filesOpen} onOpenChange={setFilesOpen}>
          <SheetContent side="right" hideClose className="gap-0 p-0">
            <SheetTitle className="sr-only">Files</SheetTitle>
            <SheetDescription className="sr-only">Files in this chat</SheetDescription>
            {panel}
          </SheetContent>
        </Sheet>
      )}
    </div>
  )
}
