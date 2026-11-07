import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ExternalLink, FolderInput, FolderOutput, SquareTerminal } from 'lucide-react'
import { useEffect, useRef } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { useLocalTimeWindow } from '@/lib/use-time-window'
import { mayAdd, type Project } from '@/pages/projects/api'
import { ProjectView, type ProjectHost } from '@/pages/projects/project'
import { chooseProject, openWebChat, stateQuery } from './api'
import { runCommand } from './commands'
import { ownPage } from './http'

/** Arena's address for a link out of the IDE: the one signed in to (code-arena's page), or this page's own (Code Studio). */
function useArena(): string | null {
  const manual = useQuery(stateQuery).data?.manual
  if (!ownPage()) return ''
  return manual ? new URL(manual).origin : null
}

/**
 * A project of Arena's in an editor tab: the same page as Arena's (its numbers over a time window and their export, its
 * goals and tasks, what is in it, its people, and its settings for owners), with what is in it opened here (a chat or a
 * session of the person's continues in the chat), and the session open now put in it or taken out.
 */
export function ProjectTab({ id }: { id: string }) {
  const queryClient = useQueryClient()
  const time = useLocalTimeWindow()
  const state = useQuery(stateQuery).data
  const arena = useArena()
  const open = useMutation({
    mutationFn: openWebChat,
    onSuccess: async () => {
      runCommand('workbench.action.chat.open')
      await queryClient.invalidateQueries({ queryKey: ['code'] })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  // A turn of the session ended: what it used is in its project now (the usage log takes each request as it ends).
  const busy = state?.busy
  const was = useRef(busy)
  useEffect(() => {
    if (was.current && !busy && state?.workProject?.id === id) void queryClient.invalidateQueries({ queryKey: ['projects', id] })
    was.current = busy
  }, [busy, id, queryClient, state?.workProject?.id])
  const host: ProjectHost = {
    chatLink: (c, inner) =>
      c.kind !== 'room' ? (
        <button
          type="button"
          onClick={() => (c.id === state?.chat ? runCommand('workbench.action.chat.open') : open.mutate(c.id))}
          disabled={open.isPending || state?.busy}
          title={c.id === state?.chat ? 'The session open now' : `Continue ${c.title} in the chat`}
          className="flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-sm outline-none hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-60"
        >
          {inner}
          {c.id === state?.chat && <span className="text-xs text-primary-ink">open now</span>}
        </button>
      ) : arena !== null ? (
        // A room is Arena's: it opens there.
        <a href={`${arena}/chat/${c.id}`} target="_blank" rel="noreferrer" className="flex items-center gap-2 rounded-md px-2 py-1.5 text-sm hover:bg-accent">
          {inner}
          <ExternalLink className="size-3 shrink-0 text-muted-foreground" aria-label="Opens in Arena" />
        </a>
      ) : (
        <span className="flex items-center gap-2 px-2 py-1.5 text-sm">{inner}</span>
      ),
    onDeleted: () => void queryClient.invalidateQueries({ queryKey: stateQuery.queryKey }),
    inIt: (p) => <ThisSession project={p} session={state?.workProject ?? null} />,
    itemsAt: arena !== null ? (p) => `${arena}/projects/${p.id}` : undefined,
  }
  return (
    <div className="absolute inset-0 overflow-auto bg-background">
      <div className="mx-auto max-w-6xl p-4 sm:p-6" data-testid="project-tab">
        <ProjectView id={id} time={time} host={host} />
      </div>
    </div>
  )
}

/** The session open now: in this project (what it uses counts here), or put in it; the folder's next sessions follow. */
function ThisSession({ project: p, session }: { project: Project; session: { id: string; name: string } | null }) {
  const queryClient = useQueryClient()
  const inIt = session?.id === p.id
  const may = mayAdd(p.role) && p.status === 'active'
  const choose = useMutation({
    mutationFn: chooseProject,
    onSuccess: async ({ said }) => {
      await queryClient.invalidateQueries({ queryKey: stateQuery.queryKey })
      await queryClient.invalidateQueries({ queryKey: ['projects'] })
      toast.success(said)
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Card className="xl:col-span-2">
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <SquareTerminal className="size-4" aria-hidden="true" /> This session
        </CardTitle>
        <CardDescription>
          {inIt
            ? 'It counts to this project: what it uses shows here within seconds, and its chat in Arena is in it. The next sessions in this folder start in it.'
            : session
              ? `It counts to ${session.name || 'another project'}.`
              : 'It is in no project.'}
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-wrap gap-2">
        {inIt ? (
          <Button variant="outline" size="sm" disabled={choose.isPending} onClick={() => choose.mutate('none')}>
            <FolderOutput /> Take this session out
          </Button>
        ) : may ? (
          <Button size="sm" disabled={choose.isPending} onClick={() => choose.mutate(p.id)}>
            <FolderInput /> Put this session in it
          </Button>
        ) : (
          <p className="text-sm text-muted-foreground">{p.status === 'archived' ? 'It is archived: nothing goes in it.' : 'Viewers see it; its contributors and owners put sessions in it.'}</p>
        )}
      </CardContent>
    </Card>
  )
}
