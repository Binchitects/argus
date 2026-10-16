import { EyeOff, FileText, X } from 'lucide-react'
import { formatValue } from '@/lib/format'
import { cn } from '@/lib/utils'
import type { ChatModel } from './types'
import type { Uploads } from './uploads'

/**
 * The files going with the next message, above the box: a picture as itself, another file by its name and size, each
 * with how far its upload is and a button to take it away. The web chat's and Code Arena's boxes show them so.
 */
export function UploadChips({ uploads, model }: { uploads: Uploads; model?: ChatModel }) {
  const blindImages = model && !model.vision && uploads.uploads.some((u) => u.isImage && !u.error)
  return (
    <>
      {uploads.uploads.length > 0 && (
        <ul className="flex flex-wrap gap-2 px-3 pt-3" aria-label="Files to send">
          {uploads.uploads.map((u) => (
            <li key={u.key} className={cn('relative flex items-center gap-2 overflow-hidden rounded-lg border bg-muted/40 text-sm', u.error && 'border-destructive/50', u.isImage && u.preview ? 'p-0' : 'py-1.5 pr-8 pl-2.5')}>
              {u.isImage && u.preview ? (
                <img src={u.preview} alt={u.name} className="size-16 object-cover" />
              ) : (
                <>
                  <FileText className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                  <span className="grid">
                    <span className="max-w-44 truncate">{u.name}</span>
                    <span className={cn('text-xs', u.error ? 'text-destructive-ink' : 'text-muted-foreground')}>
                      {u.error ?? (u.attachment ? `${formatValue(u.size, 'bytes')}${u.attachment.truncated ? ', cut to fit' : ''}` : `Uploading ${Math.round(u.progress * 100)}%`)}
                    </span>
                  </span>
                </>
              )}
              {!u.attachment && !u.error && (
                <span className="absolute inset-x-0 bottom-0 h-0.5 bg-muted" aria-hidden="true">
                  <span className="block h-full bg-primary transition-[width]" style={{ width: `${u.progress * 100}%` }} />
                </span>
              )}
              {u.isImage && u.error && <span className="px-2 text-xs text-destructive-ink">{u.error}</span>}
              <button
                type="button"
                onClick={() => uploads.remove(u.key)}
                className="absolute top-1 right-1 flex size-5 items-center justify-center rounded-full bg-background/90 text-muted-foreground shadow-sm outline-none hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring"
                aria-label={`Remove ${u.name}`}
              >
                <X className="size-3" />
              </button>
            </li>
          ))}
        </ul>
      )}
      {blindImages && (
        <p className="flex items-center gap-1.5 px-4 pt-2 text-xs text-muted-foreground">
          <EyeOff className="size-3.5" aria-hidden="true" /> {model.name} cannot see images: it gets their names only.
        </p>
      )}
    </>
  )
}
