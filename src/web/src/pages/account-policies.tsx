import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Check } from 'lucide-react'
import { useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Skeleton } from '@/components/ui/skeleton'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { Markdown } from './chat/markdown'

interface ToRead {
  versionId: string
  key: string
  title: string
  version: number
  approvedAt: string
  acknowledgedAt: string | null
}

const toReadQuery = {
  queryKey: ['account', 'policies'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<ToRead[]>('/api/account/policies', { signal }),
}

/** A policy's text, and saying it was read. */
function ReadPolicy({ policy, onClose }: { policy: ToRead; onClose: () => void }) {
  const queryClient = useQueryClient()
  const text = useQuery({
    queryKey: ['account', 'policies', policy.versionId],
    queryFn: ({ signal }) => api<{ text: string }>(`/api/account/policies/${policy.versionId}`, { signal }),
  })
  const read = useMutation({
    mutationFn: () => api(`/api/account/policies/${policy.versionId}/acknowledge`, { method: 'POST', body: {} }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: toReadQuery.queryKey })
      toast.success('Thank you: noted.')
      onClose()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>{policy.title}</DialogTitle>
          <DialogDescription>
            Version {policy.version}, in force since {new Date(policy.approvedAt).toLocaleDateString()}.
          </DialogDescription>
        </DialogHeader>
        <div className="max-h-[60vh] overflow-y-auto rounded-lg border p-4 text-sm">{text.data ? <Markdown text={text.data.text} /> : <Skeleton className="h-64" />}</div>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Close
          </Button>
          {!policy.acknowledgedAt && (
            <Button loading={read.isPending} disabled={!text.data} onClick={() => read.mutate()}>
              I have read it
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

/** Your account: the company's policies you must read, and saying you did. Shown only when there are some. */
export function PoliciesToRead() {
  const data = useQuery(toReadQuery)
  const [open, setOpen] = useState<ToRead | null>(null)
  const list = data.data ?? []
  if (list.length === 0) return null
  const unread = list.filter((p) => !p.acknowledgedAt).length
  return (
    <Card id="policies" className="scroll-mt-20">
      <CardHeader>
        <CardTitle>Policies to read</CardTitle>
        <CardDescription>{unread > 0 ? `${unread} of the company's policies for you to read and say so.` : 'You have read each policy in force.'}</CardDescription>
      </CardHeader>
      <CardContent>
        <ul className="divide-y">
          {list.map((p) => (
            <li key={p.versionId} className="flex flex-wrap items-center gap-2 py-2 text-sm">
              <span className="min-w-40 flex-1">
                {p.title} <span className="text-muted-foreground">v{p.version}</span>
              </span>
              {p.acknowledgedAt ? (
                <span className="inline-flex items-center gap-1 text-xs text-success-ink">
                  <Check className="size-3.5" aria-hidden="true" /> read {new Date(p.acknowledgedAt).toLocaleDateString()}
                </span>
              ) : (
                <Badge variant="warning">to read</Badge>
              )}
              <Button size="sm" variant={p.acknowledgedAt ? 'ghost' : 'outline'} onClick={() => setOpen(p)}>
                {p.acknowledgedAt ? 'Open' : 'Read'}
              </Button>
            </li>
          ))}
        </ul>
      </CardContent>
      {open && <ReadPolicy policy={open} onClose={() => setOpen(null)} />}
    </Card>
  )
}
