import { useQuery } from '@tanstack/react-query'
import { Download } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { api } from '@/lib/api'
import { formatValue } from '@/lib/format'
import { cn } from '@/lib/utils'

interface AccountData {
  retentionDays: number | null
  /** What their files take, and their room (null: no limit). */
  files?: { bytes: number; limitBytes: number | null }
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
