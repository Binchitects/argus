import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowRightLeft, Cpu, Gauge, Settings } from 'lucide-react'
import { Link } from 'react-router'
import { CodeBlock } from '@/components/app/code-block'
import { KeyValues } from '@/components/app/key-values'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Stat, StatGrid } from '@/components/app/stat'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { blockChanges } from './model-switch'

interface Sample {
  file: string
  title: string
  hardware: string | null
  download: string | null
  measured: string | null
  status: string | null
  model: string
  block: string
}

interface ModelInfo {
  running: { name: string | null; file: string | null; context: string | null; maxOutput: string | null; mtpDraftMax: string | null; gpuPowerLimitW: string | null; cpuPowerLimitW: string | null; thinkingPresets: string | null }
  prices: { input: string | null; cachedInput: string | null; output: string | null }
  samples: Sample[]
}

export function ModelPage() {
  const m = useQuery({ queryKey: ['admin', 'model'], queryFn: ({ signal }) => api<ModelInfo>('/api/admin/model', { signal }) })
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const switchTo = useMutation({
    mutationFn: (s: Sample) => api('/api/admin/config', { method: 'PUT', body: { changes: blockChanges(s.block) } }),
    onSuccess: (_, s) => {
      void queryClient.invalidateQueries({ queryKey: ['admin', 'config'] })
      toast.success(`${s.model} is ready to switch to`, { description: 'Run ./scripts/apply-settings.sh on the host to apply it.', duration: 10_000 })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  if (m.isPending) return <PageSkeleton />
  if (m.error) return <QueryError error={m.error} retry={() => m.refetch()} />
  const r = m.data.running
  const mtp = r.mtpDraftMax && r.mtpDraftMax !== '0' ? `on (${r.mtpDraftMax} draft tokens)` : 'off'
  const price = (v: string | null) => (v ? `$${v}` : '?')
  return (
    <>
      <PageHeader
        title="Deployment"
        description="The .env model the engine starts with, and the shipped deployments to switch to."
        actions={
          <Button variant="outline" asChild>
            <Link to="/admin/settings#model">
              <Settings /> Model settings
            </Link>
          </Button>
        }
      />
      <div className="grid gap-6">
        <StatGrid className="xl:grid-cols-4">
          <Stat icon={Cpu} label="Model" value={r.name ?? 'not set'} hint={r.file ?? undefined} text />
          <Stat label="Context" value={r.context ? Number(r.context).toLocaleString('en-US') : '?'} hint="tokens" />
          <Stat label="Longest reply" value={r.maxOutput ? Number(r.maxOutput).toLocaleString('en-US') : '?'} hint="tokens" />
          <Stat icon={Gauge} label="Prices per 1M tokens" value={price(m.data.prices.output)} hint={`output · input ${price(m.data.prices.input)} · cached ${price(m.data.prices.cachedInput)}`} />
        </StatGrid>
        <Card>
          <CardHeader>
            <CardTitle>Running</CardTitle>
          </CardHeader>
          <CardContent>
            <KeyValues
              items={[
                ['Multi-token prediction', mtp],
                ['Thinking levels', r.thinkingPresets || 'none'],
                ['GPU power cap', r.gpuPowerLimitW ? `${r.gpuPowerLimitW} W` : 'the card’s default'],
                ['CPU power cap', r.cpuPowerLimitW ? `${r.cpuPowerLimitW} W` : 'the firmware’s'],
              ]}
            />
          </CardContent>
        </Card>
        <div className="grid gap-3">
          <h2 className="text-base font-semibold">Switch the deployment to</h2>
          {m.data.samples.length === 0 && <Alert>No deployment samples are mounted.</Alert>}
          {m.data.samples.map((s) => {
            const running = s.model === r.name
            return (
              <Card key={s.file}>
                <CardHeader>
                  <CardTitle className="flex flex-wrap items-center gap-2">
                    {s.title} {running && <Badge variant="success">Running</Badge>}
                  </CardTitle>
                  {s.hardware && <CardDescription>{s.hardware}</CardDescription>}
                  {!running && (
                    <CardAction>
                      <Button
                        loading={switchTo.isPending && switchTo.variables?.file === s.file}
                        onClick={async () => {
                          if (
                            await confirm({
                              title: `Switch to ${s.model}?`,
                              description: `The model settings are saved as pending .env changes (your model directory stays). Nothing changes until you run ./scripts/apply-settings.sh on the host; then the engine reloads${s.download ? ', downloading first if needed' : ''}. Chat and the API pause for a few minutes.`,
                              confirm: 'Prepare the switch',
                            })
                          )
                            switchTo.mutate(s)
                        }}
                      >
                        <ArrowRightLeft /> Switch to this model
                      </Button>
                    </CardAction>
                  )}
                </CardHeader>
                <CardContent className="grid gap-3 text-sm">
                  {(s.download || s.measured || s.status) && (
                    <div className="grid gap-1 text-muted-foreground">
                      {s.download && <p>{s.download}</p>}
                      {s.measured && <p>{s.measured}</p>}
                      {s.status && <p>{s.status}</p>}
                    </div>
                  )}
                  <details className="group">
                    <summary className="cursor-pointer text-sm font-medium text-primary-ink select-none">The .env block, to apply by hand</summary>
                    <div className="mt-3 grid gap-2">
                      <p className="text-muted-foreground">
                        In <code className="font-mono">stack/.env</code>, replace everything from <code className="font-mono"># &gt;&gt;&gt; MODEL</code> to <code className="font-mono"># &lt;&lt;&lt; MODEL</code> with this, keeping your own <code className="font-mono">LLAMACPP_MODEL_DIR</code>. Then run{' '}
                        <code className="font-mono">docker compose up -d</code>. Whole file: <code className="font-mono">env-samples/{s.file}</code>.
                      </p>
                      <CodeBlock code={s.block} label={`${s.model} .env block`} />
                    </div>
                  </details>
                </CardContent>
              </Card>
            )
          })}
        </div>
      </div>
    </>
  )
}
