import { Cloud, CloudAlert, CloudDownload, CloudUpload } from 'lucide-react'
import { Tooltip } from '@/components/ui/tooltip'
import type { SyncState } from './api'
import { syncWords } from './sync'

/** A small mark in the chat's header: in step, sending, the web's news waiting, or trouble (its words on hover). */
export function SyncStatus({ sync }: { sync?: SyncState | null }) {
  if (!sync) return null
  const [Icon, short, tone] = sync.trouble
    ? [CloudAlert, 'Not in step', 'text-destructive-ink']
    : sync.waiting > 0
      ? [CloudUpload, `${sync.waiting} to send`, 'text-muted-foreground']
      : sync.behind
        ? [CloudDownload, 'Web news', 'text-muted-foreground']
        : [Cloud, 'In step', 'text-muted-foreground']
  return (
    <Tooltip content={syncWords(sync)}>
      <output aria-label={`Arena: ${syncWords(sync)}`} className={`hidden items-center gap-1 text-xs @md:inline-flex ${tone}`}>
        <Icon className="size-3.5" aria-hidden="true" />
        {short}
      </output>
    </Tooltip>
  )
}
