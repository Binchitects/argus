import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Download } from 'lucide-react'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'

/** A default model's file, or the speech server's model: what serves it, its place, where it comes from, and its state. */
interface DefaultModel {
  server: string
  name: string
  source: string
  state: 'present' | 'downloading' | 'missing' | 'unknown'
}

const serverLabel: Record<string, string> = { chat: 'Chat model (MODEL)', imagegen: 'Pictures', videogen: 'Video', embed: 'Search by meaning', audio: 'Speech', laya: 'Laya' }

const defaultModelsQuery = {
  queryKey: ['admin', 'models', 'defaults'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<DefaultModel[]>('/api/admin/models/defaults', { signal }),
}

/**
 * The default models the stack starts with that are not here yet. The app fetches nothing on its own: the installer's
 * option downloads them, or an admin here (or copies them into the model library). Shown only while one is missing.
 */
export function DefaultModelsCard() {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const list = useQuery({ ...defaultModelsQuery, refetchInterval: (q) => (q.state.data?.some((m) => m.state === 'downloading') ? 5000 : false) })
  const start = useMutation({
    mutationFn: () => api<{ started: string[]; models: DefaultModel[] }>('/api/admin/models/defaults', { body: {} }),
    onSuccess: (r) => {
      queryClient.setQueryData(defaultModelsQuery.queryKey, r.models)
      void queryClient.invalidateQueries({ queryKey: ['admin', 'models'] })
      if (r.started.length) toast.success('Downloading the default models', { description: r.started.join(' · ') })
      else toast.success('Nothing to download', { description: 'Every default model is here, or on its way.' })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const missing = (list.data ?? []).filter((m) => m.state !== 'present')
  if (missing.length === 0) return null
  return (
    <Card className="mb-4">
      <CardHeader>
        <CardTitle>Default models</CardTitle>
        <CardDescription>
          What the stack starts with that is not in the model library yet. The app fetches nothing on its own: download them here (from Hugging Face), or copy their files into the
          library (MODELS_DIR) as the installer&apos;s offline option does.
        </CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        <ul className="grid gap-1.5 text-sm">
          {missing.map((m) => (
            <li key={`${m.server}:${m.name}`} className="flex flex-wrap items-center gap-2">
              <span className="font-medium">{serverLabel[m.server] ?? m.server}</span>
              <code className="text-xs [overflow-wrap:anywhere]">{m.name}</code>
              <span className="text-xs text-muted-foreground">from {m.source}</span>
              <Badge variant={m.state === 'downloading' ? 'secondary' : 'outline'}>{m.state === 'downloading' ? 'Downloading…' : m.state === 'unknown' ? 'Its server does not answer' : 'Missing'}</Badge>
            </li>
          ))}
        </ul>
        {missing.some((m) => m.state === 'missing') && (
          <div>
            <Button
              loading={start.isPending}
              onClick={async () => {
                if (
                  await confirm({
                    title: 'Download the default models?',
                    description: 'From Hugging Face, into the model library: tens of gigabytes for the chat model. They come in under Downloads, each checked against its SHA-256.',
                    confirm: 'Download',
                  })
                )
                  start.mutate()
              }}
            >
              <Download /> Download the default models
            </Button>
          </div>
        )}
        {list.error && <Alert variant="destructive">{errorMessage(list.error)}</Alert>}
      </CardContent>
    </Card>
  )
}
