import { Sparkles } from 'lucide-react'
import { Button } from '@/components/ui/button'
import type { AutoRoute, ChatConfig } from './types'

/** What the small model said a question is, in words. */
const kinds: Record<string, string> = {
  chat: 'small talk',
  lookup: 'a quick lookup or rewrite',
  code: 'code',
  reasoning: 'it needs careful reasoning',
  research: 'research',
}

/** Who answered on Auto, and why, in a sentence. */
function routeText(r: AutoRoute, config: ChatConfig): string {
  const kind = kinds[r.kind]
  const why = r.reason ? ` (${r.reason})` : ''
  if (r.small) return `Answered by ${r.model}, the small model: ${kind ?? r.kind}${why}.`
  if (kind) {
    const level = r.thinking ? (config.presets.find((p) => p.level === r.thinking)?.label ?? r.thinking) : null
    return `Handed to ${r.model}${level ? ` (thinking: ${level})` : ''}: ${kind}${why}.`
  }
  return `${r.model} answered. ${r.reason}`
}

/** The note under an answer on Auto: who answered and why; under the small model's, one click asks the big one. */
export function RouteNote({ route, config, busy, onAskBig }: { route: AutoRoute; config: ChatConfig; busy: boolean; onAskBig?: () => void }) {
  return (
    <p className="mt-2 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-muted-foreground" role="note" aria-label="Auto">
      <span className="inline-flex items-center gap-1 font-medium text-foreground">
        <Sparkles className="size-3.5 text-primary-ink" aria-hidden="true" /> Auto
      </span>
      <span>{routeText(route, config)}</span>
      {route.small && onAskBig && (
        <Button variant="link" size="sm" className="h-auto px-0 text-xs" disabled={busy} onClick={onAskBig}>
          Ask the big model
        </Button>
      )}
    </p>
  )
}
