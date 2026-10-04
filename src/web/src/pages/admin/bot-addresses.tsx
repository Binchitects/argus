import { useQuery } from '@tanstack/react-query'
import { CheckCircle2, CircleDashed } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { api } from '@/lib/api'

interface BotPlatform {
  name: string
  title: string
  ready: boolean
  url: string
  threads: number
}

const where: Record<string, string> = {
  slack: 'Event Subscriptions → Request URL (events app_mention and message.im)',
  mattermost: 'the outgoing webhook’s or slash command’s callback URL',
  teams: 'the Azure Bot’s messaging endpoint',
  email: 'your mail gateway’s inbound webhook (from, to, subject, text)',
}

/** Under Settings → Chat bots: each platform set up or not, and the address to give it. */
export function BotAddresses() {
  const bots = useQuery({ queryKey: ['admin', 'bots'], queryFn: () => api<{ platforms: BotPlatform[] }>('/api/admin/bots') })
  if (!bots.data) return null
  return (
    <section className="grid gap-2 py-4 last:pb-0" aria-label="Where the platforms reach the app">
      <p className="text-sm font-medium">Where the platforms reach the app</p>
      <ul className="grid gap-2">
        {bots.data.platforms.map((p) => (
          <li key={p.name} className="grid gap-1 rounded-lg border p-3" aria-label={p.title}>
            <span className="flex flex-wrap items-center gap-2 text-sm font-medium">
              {p.title}
              {p.ready ? (
                <Badge variant="success">
                  <CheckCircle2 /> Set up
                </Badge>
              ) : (
                <Badge variant="secondary">
                  <CircleDashed /> Not set up
                </Badge>
              )}
              {p.threads > 0 && <span className="text-xs font-normal text-muted-foreground">{p.threads} {p.threads === 1 ? 'chat' : 'chats'}</span>}
            </span>
            <code className="font-mono text-[0.8125rem] break-all">{p.url}</code>
            <span className="text-xs text-muted-foreground">Give it as {where[p.name] ?? 'its address'}.</span>
          </li>
        ))}
      </ul>
    </section>
  )
}
