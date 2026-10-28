import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Download } from 'lucide-react'
import { useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Field } from '@/components/ui/field'
import { Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { formatValue, when } from '@/lib/format'
import { cn } from '@/lib/utils'
import { DATA_REQUEST_KINDS, dataRequestLabel, type DataRequest, type DataRequestKind } from './admin/compliance-api'
import { RequestStatus } from './admin/data-requests'

interface AccountData {
  retentionDays: number | null
  /** What their files take, and their room (null: no limit). */
  files?: { bytes: number; limitBytes: number | null }
  /** Days the prompt log keeps a question asked with an API key (0: forever); null: not kept. */
  promptLogDays?: number | null
}

/** A person's own copy of their data, how long their chats are kept, and what their files take of their room. */
export function YourData() {
  const data = useQuery({ queryKey: ['account', 'data'], queryFn: () => api<AccountData>('/api/account/data') })
  const days = data.data?.retentionDays
  const files = data.data?.files
  const share = files?.limitBytes ? Math.min(100, (100 * files.bytes) / files.limitBytes) : null
  return (
    <Card>
      <CardHeader>
        <CardTitle>Your data</CardTitle>
        <CardDescription>
          A zip of your chats (each as JSON and as Markdown), your files, assistants, scheduled tasks and settings.
          {data.data && (days ? ` Chats are kept for ${days} days after their last message, then deleted.` : ' Chats are kept until you delete them.')}
        </CardDescription>
        {data.data && (
          <p className="text-sm text-muted-foreground">
            Your questions and the answers you got, in chats and with your API keys, can be read by the people who run this with the right to the prompt log; each time they
            look is kept in the audit log.
            {data.data.promptLogDays === 0 && ' Questions asked with API keys are kept in the prompt log for good.'}
            {!!data.data.promptLogDays && ` Questions asked with API keys are kept in the prompt log for ${data.data.promptLogDays} days.`}
          </p>
        )}
      </CardHeader>
      <CardContent className="grid gap-4">
        {files && (
          <div className="grid max-w-md gap-1.5 text-sm">
            <p>
              Your files take <span className="font-medium">{formatValue(files.bytes, 'bytes')}</span>
              {files.limitBytes ? ` of your ${formatValue(files.limitBytes, 'bytes')}.` : '.'}
              {share !== null && share >= 100 && ' It is full: new files and pictures are refused until you delete chats you no longer need.'}
            </p>
            {share !== null && (
              <div className="h-1.5 w-full overflow-hidden rounded-full bg-muted" aria-hidden="true">
                <div className={cn('h-full rounded-full', share >= 100 ? 'bg-destructive' : share >= 80 ? 'bg-warning' : 'bg-primary')} style={{ width: `${share}%` }} />
              </div>
            )}
          </div>
        )}
        <div>
          <Button variant="outline" asChild>
            <a href="/api/account/export" download>
              <Download /> Download your data
            </a>
          </Button>
        </div>
      </CardContent>
    </Card>
  )
}

/**
 * The person's rights over their data (GDPR 15–22): what they asked, and asking for a copy, a correction, restriction,
 * objection or erasure. Each is answered by people within a month; they are told in their bell.
 */
export function DataRights() {
  const queryClient = useQueryClient()
  const mine = useQuery({
    queryKey: ['account', 'data-requests'],
    queryFn: ({ signal }) => api<{ restrictedAt: string | null; requests: DataRequest[] }>('/api/account/data-requests', { signal }),
  })
  const [kind, setKind] = useState<DataRequestKind>('access')
  const [details, setDetails] = useState('')
  const ask = useMutation({
    mutationFn: () => api<DataRequest>('/api/account/data-requests', { method: 'POST', body: { kind, details } }),
    onSuccess: (r) => {
      toast.success('Sent', { description: `It is answered by ${new Date(r.dueAt).toLocaleDateString()} at the latest; you are told here and in your bell.` })
      setDetails('')
      void queryClient.invalidateQueries({ queryKey: ['account', 'data-requests'] })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const about = DATA_REQUEST_KINDS.find((k) => k.kind === kind)?.about
  return (
    <Card id="your-data-rights">
      <CardHeader>
        <CardTitle>Your data rights</CardTitle>
        <CardDescription>Ask for a copy of your data, a correction, its use restricted or objected to, or its erasure. The people who run this answer within a month.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        {mine.data?.restrictedAt && (
          <Alert variant="info">Since {new Date(mine.data.restrictedAt).toLocaleDateString()}, nothing learns from your data: no memories, digests or skills from your work.</Alert>
        )}
        {(mine.data?.requests.length ?? 0) > 0 && (
          <ul className="divide-y rounded-lg border text-sm">
            {mine.data!.requests.map((r) => (
              <li key={r.id} className="grid gap-1 p-3">
                <span className="flex flex-wrap items-center gap-2">
                  <span className="font-medium">{dataRequestLabel(r.kind)}</span>
                  <RequestStatus r={r} />
                  <time dateTime={r.receivedAt} title={when(r.receivedAt)} className="text-xs text-muted-foreground">
                    asked {new Date(r.receivedAt).toLocaleDateString()}
                  </time>
                </span>
                {r.outcome && <span className="text-muted-foreground">{r.outcome}</span>}
              </li>
            ))}
          </ul>
        )}
        <form
          className="grid gap-3"
          onSubmit={(e) => {
            e.preventDefault()
            ask.mutate()
          }}
        >
          <Field label="What you ask for" hint={about}>
            <Select value={kind} onValueChange={(v) => setKind(v as DataRequestKind)}>
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {DATA_REQUEST_KINDS.map((k) => (
                  <SelectItem key={k.kind} value={k.kind}>
                    {k.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
          <Field label="Anything to add (optional)">
            <Textarea value={details} onChange={(e) => setDetails(e.target.value)} rows={3} maxLength={4000} />
          </Field>
          <div>
            <Button type="submit" variant="outline" loading={ask.isPending}>
              Send the request
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  )
}
