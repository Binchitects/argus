import { ChevronLeft, ChevronRight, Download, ExternalLink, X } from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogClose, DialogContent, DialogDescription, DialogTitle } from '@/components/ui/dialog'
import { Tooltip } from '@/components/ui/tooltip'
import { formatValue } from '@/lib/format'
import { cn } from '@/lib/utils'
import { attachmentUrl } from './api'
import type { Attachment } from './types'

/**
 * Images at full size: fitted to the screen, or at their own size on a click;
 * arrows (and the arrow keys) move between the images sent together.
 */
export function ImageViewer({ images, index, onIndex }: { images: Attachment[]; index: number | null; onIndex: (i: number | null) => void }) {
  const [actual, setActual] = useState(false)
  const image = index === null ? null : images[index]
  const many = images.length > 1
  const go = (step: number) => {
    if (index === null || !many) return
    setActual(false)
    onIndex((index + step + images.length) % images.length)
  }
  return (
    <Dialog
      open={image !== undefined && image !== null}
      onOpenChange={(open) => {
        if (!open) onIndex(null)
        setActual(false)
      }}
    >
      <DialogContent
        hideClose
        className="flex h-[calc(100dvh-2rem)] w-[calc(100vw-2rem)] max-w-6xl flex-col gap-0 overflow-hidden p-0"
        onKeyDown={(e) => {
          if (e.key === 'ArrowLeft') go(-1)
          if (e.key === 'ArrowRight') go(1)
        }}
      >
        {image && (
          <>
            <header className="flex h-12 shrink-0 items-center gap-2 border-b pr-2 pl-4">
              <div className="grid min-w-0">
                <DialogTitle className="truncate text-sm leading-tight">{image.fileName}</DialogTitle>
                <DialogDescription className="text-xs">
                  {formatValue(image.size, 'bytes')}
                  {many && ` · ${index! + 1} of ${images.length}`}
                </DialogDescription>
              </div>
              <span className="ml-auto flex items-center">
                <Tooltip content="Open in a new tab">
                  <Button variant="ghost" size="icon-sm" asChild>
                    <a href={attachmentUrl(image.id)} target="_blank" rel="noopener" aria-label="Open in a new tab">
                      <ExternalLink />
                    </a>
                  </Button>
                </Tooltip>
                <Tooltip content={`Download ${image.fileName}`}>
                  <Button variant="ghost" size="icon-sm" asChild>
                    <a href={attachmentUrl(image.id)} download={image.fileName} aria-label={`Download ${image.fileName}`}>
                      <Download />
                    </a>
                  </Button>
                </Tooltip>
                <DialogClose asChild>
                  <Button variant="ghost" size="icon-sm" aria-label="Close">
                    <X />
                  </Button>
                </DialogClose>
              </span>
            </header>
            <div className={cn('relative min-h-0 flex-1 bg-muted/40', actual ? 'overflow-auto' : 'flex items-center justify-center overflow-hidden p-4')}>
              <button
                type="button"
                onClick={() => setActual(!actual)}
                className={cn('block outline-none focus-visible:ring-[3px] focus-visible:ring-ring', actual ? 'cursor-zoom-out' : 'h-full w-full cursor-zoom-in')}
                aria-label={actual ? 'Fit to the screen' : 'Show at actual size'}
              >
                <img src={attachmentUrl(image.id)} alt={image.fileName} className={cn(actual ? 'max-w-none' : 'mx-auto h-full w-full object-contain')} />
              </button>
              {many && (
                <>
                  <Button variant="secondary" size="icon" className="absolute top-1/2 left-3 -translate-y-1/2 rounded-full shadow" onClick={() => go(-1)} aria-label="Previous image">
                    <ChevronLeft />
                  </Button>
                  <Button variant="secondary" size="icon" className="absolute top-1/2 right-3 -translate-y-1/2 rounded-full shadow" onClick={() => go(1)} aria-label="Next image">
                    <ChevronRight />
                  </Button>
                </>
              )}
            </div>
          </>
        )}
      </DialogContent>
    </Dialog>
  )
}
