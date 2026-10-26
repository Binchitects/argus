import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { FileText, LogOut } from 'lucide-react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { api, errorMessage, meQuery, type Me } from '@/lib/api'
import { Markdown } from '@/pages/chat/markdown'
import { useSignOut } from './use-sign-out'

interface NoticeView {
  on: boolean
  title: string
  text: string | null
  version: string
  acceptedVersion: string | null
}

export const noticeQuery = {
  queryKey: ['account', 'notice'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<NoticeView>('/api/account/notice', { signal }),
}

/** The sign-in notice (acceptable use, the privacy notice) in its current version, accepted before anything else. */
export function AcceptNotice({ me }: { me: Me }) {
  const queryClient = useQueryClient()
  const signOut = useSignOut()
  const notice = useQuery(noticeQuery)
  const accept = useMutation({
    mutationFn: (version: string) => api('/api/account/notice/accept', { method: 'POST', body: { version } }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: meQuery.queryKey }),
    onError: () => void notice.refetch(),
  })
  const n = notice.data
  return (
    <main className="grid min-h-dvh place-items-center bg-background px-4 py-10">
      <div className="grid w-full max-w-2xl gap-5">
        <div className="grid gap-2">
          <FileText className="size-8 text-primary" aria-hidden="true" />
          <h1 className="text-2xl font-semibold tracking-tight">{n?.title ?? 'Before you use this service'}</h1>
          <p className="text-muted-foreground">
            Hello {me.displayName}. {n?.acceptedVersion ? 'The notice changed since you last accepted it.' : 'Please read this first.'}
          </p>
        </div>
        {accept.error && <Alert variant="warning">{errorMessage(accept.error)}</Alert>}
        <div className="max-h-[60vh] overflow-y-auto rounded-xl border bg-card p-5 text-sm">{n?.text ? <Markdown text={n.text} /> : <Skeleton className="h-40" />}</div>
        <div className="flex flex-wrap items-center gap-3">
          <Button onClick={() => n && accept.mutate(n.version)} loading={accept.isPending} disabled={!n}>
            I have read it and accept it
          </Button>
          <Button variant="ghost" onClick={signOut}>
            <LogOut /> Sign out
          </Button>
          {n && <span className="text-xs text-muted-foreground">Version {n.version}</span>}
        </div>
      </div>
    </main>
  )
}
