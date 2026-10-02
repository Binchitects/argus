import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Download, ExternalLink, Heart, Lock, Pause, Play, Plus, Search, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { formatValue } from '@/lib/format'
import { cn } from '@/lib/utils'
import { bytes } from './model-profile'

interface HfModel {
  id: string
  downloads: number
  likes: number
  lastModified: string | null
  gated: boolean
  pipelineTag: string | null
  tags: string[]
}

interface HfRepo {
  id: string
  sha: string
  gated: boolean
  license: string | null
  architecture: string | null
  parameters: number | null
  context: number | null
  downloads: number
  likes: number
  gpuForModels: number | null
  ramForModels: number | null
  models: { name: string; quant: string | null; size: number; projector: boolean; parts: number; inLibrary: boolean; path: string }[]
}

export interface HfDownload {
  id: string
  repo: string
  state: 'queued' | 'running' | 'paused' | 'done' | 'failed'
  bytes: number
  total: number
  speed: number | null
  error: string | null
  createdAt: string
  files: { path: string; size: number; library: string }[]
}

/** Whether weights of this size fit: on the GPUs (with room for the cache), on GPUs and RAM, or not at all. */
function fit(size: number, gpu: number | null, ram: number | null): { label: string; tone: 'success' | 'warning' | 'destructive' } | null {
  if (gpu === null) return null
  // The context cache and compute buffers need room besides the weights.
  if (size * 1.15 <= gpu) return { label: 'Fits the GPUs', tone: 'success' }
  if (size * 1.1 <= gpu + (ram ?? 0)) return { label: 'GPUs and RAM', tone: 'warning' }
  return { label: 'Too big', tone: 'destructive' }
}

/**
 * Hugging Face, in a dialog: search GGUF models, open one to see its files (by
 * quantisation, with sizes and whether each fits this machine), and download into the library.
 */
export function HuggingFaceBrowser({ onClose }: { onClose: () => void }) {
  const [q, setQ] = useState('')
  const [asked, setAsked] = useState('')
  const [repo, setRepo] = useState<string | null>(null)
  const search = useQuery({
    queryKey: ['admin', 'hf', 'search', asked],
    queryFn: ({ signal }) => api<HfModel[]>(`/api/admin/models/hf/search?q=${encodeURIComponent(asked)}`, { signal }),
    enabled: asked.length >= 2,
  })
  if (repo) return <RepoView id={repo} onBack={() => setRepo(null)} onClose={onClose} />
  return (
    <>
      <DialogHeader>
        <DialogTitle>Find a model on Hugging Face</DialogTitle>
        <DialogDescription>GGUF models, most downloaded first. Open one to see its files and whether they fit this machine.</DialogDescription>
      </DialogHeader>
      <form
        className="flex gap-2"
        onSubmit={(e) => {
          e.preventDefault()
          setAsked(q.trim())
        }}
      >
        <Input type="search" value={q} onChange={(e) => setQ(e.target.value)} placeholder="qwen3, llama 3.3, gemma…" aria-label="Search Hugging Face" autoFocus />
        <Button type="submit" disabled={q.trim().length < 2} loading={search.isFetching}>
          <Search /> Search
        </Button>
      </form>
      {search.error && <Alert variant="destructive">{errorMessage(search.error)}</Alert>}
      {search.data && search.data.length === 0 && <p className="text-sm text-muted-foreground">No GGUF model matches.</p>}
      {search.data && search.data.length > 0 && (
        <ul className="grid max-h-[50dvh] gap-1 overflow-y-auto" aria-label="Models found">
          {search.data.map((m) => (
            <li key={m.id}>
              <button
                type="button"
                onClick={() => setRepo(m.id)}
                className="grid w-full gap-0.5 rounded-md border px-3 py-2 text-left text-sm outline-none hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring"
              >
                <span className="flex flex-wrap items-center gap-2 font-medium [overflow-wrap:anywhere]">
                  {m.id}
                  {m.gated && (
                    <Badge variant="outline">
                      <Lock /> Gated
                    </Badge>
                  )}
                </span>
                <span className="flex flex-wrap gap-x-3 text-xs text-muted-foreground">
                  <span>
                    <Download className="inline size-3" aria-hidden="true" /> {formatValue(m.downloads)} downloads
                  </span>
                  <span>
                    <Heart className="inline size-3" aria-hidden="true" /> {formatValue(m.likes)}
                  </span>
                  {m.lastModified && <span>updated {new Date(m.lastModified).toLocaleDateString()}</span>}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </>
  )
}

function RepoView({ id, onBack, onClose }: { id: string; onBack: () => void; onClose: () => void }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const repo = useQuery({ queryKey: ['admin', 'hf', 'repo', id], queryFn: ({ signal }) => api<HfRepo>(`/api/admin/models/hf/repo?id=${encodeURIComponent(id)}`, { signal }) })
  const [picked, setPicked] = useState<string[]>([])
  const r = repo.data
  const chosen = r?.models.filter((m) => picked.includes(m.name)) ?? []
  const total = chosen.reduce((s, m) => s + m.size, 0)
  const start = useMutation({
    mutationFn: () => api('/api/admin/models/hf/downloads', { body: { repo: id, models: picked } }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['admin', 'hf', 'downloads'] })
      toast.success('Download started', { description: 'Follow it under Downloads; once it is in, add it as a model.' })
      onClose()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <>
      <DialogHeader>
        <DialogTitle className="flex items-center gap-2 [overflow-wrap:anywhere]">
          <Button type="button" variant="ghost" size="icon-sm" onClick={onBack} aria-label="Back to the search">
            <ArrowLeft />
          </Button>
          {id}
        </DialogTitle>
        <DialogDescription>
          {r ? (
            <>
              {[r.architecture, r.parameters ? `${formatValue(r.parameters)} parameters` : null, r.context ? `${formatValue(r.context)} tokens of context` : null, r.license ? `licence ${r.license}` : null]
                .filter(Boolean)
                .join(' · ')}{' '}
              <a href={`https://huggingface.co/${id}`} target="_blank" rel="noreferrer" className="text-primary-ink underline underline-offset-2">
                On Hugging Face <ExternalLink className="inline size-3" aria-hidden="true" />
              </a>
            </>
          ) : (
            'Reading the repository…'
          )}
        </DialogDescription>
      </DialogHeader>
      {repo.error && <Alert variant="destructive">{errorMessage(repo.error)}</Alert>}
      {r && r.models.length === 0 && <Alert>This repository has no GGUF files.</Alert>}
      {r && r.models.length > 0 && (
        <fieldset className="grid gap-1.5">
          <legend className="mb-1 text-sm font-medium">Files</legend>
          {r.gpuForModels !== null && (
            <p className="mb-1 text-xs text-muted-foreground">
              This machine has {bytes(r.gpuForModels)} of GPU and {bytes(r.ramForModels ?? 0)} of RAM for models.
            </p>
          )}
          <ul className="grid max-h-[45dvh] gap-1.5 overflow-y-auto" aria-label="Files of the repository">
            {r.models.map((m) => {
              const f = m.projector ? null : fit(m.size, r.gpuForModels, r.ramForModels)
              return (
                <li key={m.name}>
                  <label
                    aria-label={m.name}
                    className={cn(
                      'flex cursor-pointer items-start gap-2.5 rounded-md border px-3 py-2 text-sm has-[:checked]:border-primary has-[:checked]:bg-primary/5 has-[:focus-visible]:ring-[3px] has-[:focus-visible]:ring-ring',
                      m.inLibrary && 'cursor-default',
                    )}
                  >
                    <input
                      type="checkbox"
                      className="mt-0.5 size-3.5 shrink-0 accent-primary outline-none"
                      checked={picked.includes(m.name)}
                      disabled={m.inLibrary}
                      onChange={(e) => setPicked(e.target.checked ? [...picked, m.name] : picked.filter((x) => x !== m.name))}
                    />
                    <span className="grid min-w-0 flex-1 gap-0.5">
                      <span className="flex flex-wrap items-center gap-1.5">
                        <span className="font-mono text-xs [overflow-wrap:anywhere]">{m.name}</span>
                        {m.quant && <Badge variant="secondary">{m.quant}</Badge>}
                        {m.projector && <Badge variant="outline">Vision projector</Badge>}
                        {m.inLibrary && <Badge variant="success">In the library</Badge>}
                      </span>
                      <span className="text-xs text-muted-foreground">
                        {bytes(m.size)}
                        {m.parts > 1 ? ` in ${m.parts} parts` : ''}
                      </span>
                    </span>
                    {f && <Badge variant={f.tone}>{f.label}</Badge>}
                  </label>
                </li>
              )
            })}
          </ul>
        </fieldset>
      )}
      <DialogFooter className="items-center">
        {chosen.length > 0 && <span className="text-sm text-muted-foreground sm:mr-auto">{bytes(total)} to download</span>}
        <Button type="button" variant="outline" onClick={onClose}>
          Cancel
        </Button>
        <Button
          type="button"
          disabled={chosen.length === 0}
          loading={start.isPending}
          onClick={async () => {
            if (await confirm({ title: `Download ${chosen.length === 1 ? chosen[0]!.name : `${chosen.length} files`}?`, description: `${bytes(total)} from huggingface.co/${id} into the model library.`, confirm: 'Download' }))
              start.mutate()
          }}
        >
          <Download /> Download
        </Button>
      </DialogFooter>
    </>
  )
}

/** Downloads from Hugging Face: how far each is, how fast, and when done, adding it as a model. */
export function DownloadsCard({ onAdd }: { onAdd: (file: string) => void }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const list = useQuery({
    queryKey: ['admin', 'hf', 'downloads'],
    queryFn: ({ signal }) => api<HfDownload[]>('/api/admin/models/hf/downloads', { signal }),
    refetchInterval: (q) => (q.state.data?.some((d) => d.state === 'running' || d.state === 'queued') ? 2000 : 30_000),
  })
  const act = useMutation({
    mutationFn: ({ d, action }: { d: HfDownload; action: 'pause' | 'resume' | 'remove' }) =>
      action === 'remove' ? api(`/api/admin/models/hf/downloads/${d.id}`, { method: 'DELETE' }) : api(`/api/admin/models/hf/downloads/${d.id}/${action}`, { method: 'POST' }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['admin', 'hf', 'downloads'] })
      await queryClient.invalidateQueries({ queryKey: ['admin', 'models', 'library'] })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (!list.data?.length) return null
  return (
    <Card className="mb-4" aria-label="Downloads">
      <CardHeader>
        <CardTitle className="text-base">Downloads</CardTitle>
        <CardDescription>From Hugging Face into the model library, one at a time. A paused or cut off one goes on from where it got to; each file is checked against its SHA-256.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        {list.data.map((d) => {
          const pct = d.total ? Math.round((100 * d.bytes) / d.total) : 0
          const eta = d.state === 'running' && d.speed ? (d.total - d.bytes) / d.speed : null
          const model = d.files.find((f) => !/mmproj/i.test(f.path)) ?? d.files[0]
          return (
            <div key={d.id} className="grid gap-1.5 rounded-lg border p-3">
              <div className="flex flex-wrap items-center gap-2 text-sm">
                <span className="min-w-0 font-medium [overflow-wrap:anywhere]">{d.repo}</span>
                <Badge variant={d.state === 'done' ? 'success' : d.state === 'failed' ? 'destructive' : d.state === 'paused' ? 'outline' : 'secondary'}>
                  {{ queued: 'Waiting', running: 'Downloading', paused: 'Paused', done: 'Done', failed: 'Failed' }[d.state]}
                </Badge>
                <span className="ml-auto flex gap-1">
                  {d.state === 'done' && model && (
                    <Button size="sm" variant="outline" onClick={() => onAdd(model.library)}>
                      <Plus /> Add as a model
                    </Button>
                  )}
                  {(d.state === 'running' || d.state === 'queued') && (
                    <Button size="sm" variant="ghost" onClick={() => act.mutate({ d, action: 'pause' })} aria-label={`Pause the download of ${d.repo}`}>
                      <Pause /> Pause
                    </Button>
                  )}
                  {(d.state === 'paused' || d.state === 'failed') && (
                    <Button size="sm" variant="ghost" onClick={() => act.mutate({ d, action: 'resume' })} aria-label={`Resume the download of ${d.repo}`}>
                      <Play /> Resume
                    </Button>
                  )}
                  <Button
                    size="icon-sm"
                    variant="ghost"
                    aria-label={`Remove the download of ${d.repo}`}
                    onClick={async () => {
                      if (d.state === 'done' || (await confirm({ title: `Stop downloading ${d.repo}?`, description: 'What is half downloaded is removed; finished files stay in the library.', confirm: 'Stop', destructive: true })))
                        act.mutate({ d, action: 'remove' })
                    }}
                  >
                    <Trash2 />
                  </Button>
                </span>
              </div>
              <p className="font-mono text-xs text-muted-foreground [overflow-wrap:anywhere]">{d.files.map((f) => f.path).join(', ')}</p>
              {d.state !== 'done' && (
                <>
                  <progress
                    value={pct}
                    max={100}
                    aria-label={`Download of ${d.repo}`}
                    className="h-2 w-full appearance-none overflow-hidden rounded-full bg-muted [&::-moz-progress-bar]:bg-primary [&::-webkit-progress-bar]:bg-muted [&::-webkit-progress-value]:bg-primary"
                  />
                  <p className="flex flex-wrap justify-between gap-2 text-xs text-muted-foreground tabular-nums">
                    <span>
                      {bytes(d.bytes)} of {bytes(d.total)} ({pct}%)
                    </span>
                    {d.state === 'running' && d.speed ? (
                      <span>
                        {bytes(d.speed)}/s{eta !== null ? ` · ${formatEta(eta)} left` : ''}
                      </span>
                    ) : null}
                  </p>
                </>
              )}
              {d.error && <p className="text-xs text-destructive-ink">{d.error}</p>}
            </div>
          )
        })}
      </CardContent>
    </Card>
  )
}

function formatEta(s: number): string {
  if (s < 60) return `${Math.ceil(s)} s`
  if (s < 3600) return `${Math.round(s / 60)} min`
  return `${Math.floor(s / 3600)} h ${Math.round((s % 3600) / 60)} min`
}
