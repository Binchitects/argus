import { ChevronLeft, ChevronRight, Download, ExternalLink, X, ZoomIn, ZoomOut } from 'lucide-react'
import { useRef, useState, type WheelEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogClose, DialogContent, DialogDescription, DialogTitle } from '@/components/ui/dialog'
import { Tooltip } from '@/components/ui/tooltip'
import { cn } from '@/lib/utils'
import type { ViewerImage } from './viewer-images'

const MIN = 0.1
const MAX = 8

/**
 * Pictures at full size: fitted to the screen, or zoomed in and out (the buttons,
 * + and - and 0, Ctrl and the wheel, a click for its own size); arrows (and the
 * arrow keys) move between the pictures shown together.
 */
export function ImageViewer({ images, index, onIndex }: { images: ViewerImage[]; index: number | null; onIndex: (i: number | null) => void }) {
  // null: fitted to the screen.
  const [zoom, setZoom] = useState<number | null>(null)
  // The picture's own size, and the scale that fits it to the screen (both from when it loads).
  const [natural, setNatural] = useState<{ w: number; h: number } | null>(null)
  const [fit, setFit] = useState(1)
  const area = useRef<HTMLDivElement>(null)
  const close = useRef<HTMLButtonElement>(null)
  const image = index === null ? null : images[index]
  const many = images.length > 1
  const go = (step: number) => {
    if (index === null || !many) return
    setZoom(null)
    setNatural(null)
    onIndex((index + step + images.length) % images.length)
  }
  /** The scale it is shown at now. */
  const scale = zoom ?? fit
  const zoomBy = (factor: number) => setZoom(Math.min(MAX, Math.max(MIN, Math.round(scale * factor * 100) / 100)))
  const onWheel = (e: WheelEvent) => {
    if (!e.ctrlKey && !e.metaKey) return
    e.preventDefault()
    zoomBy(e.deltaY < 0 ? 1.15 : 1 / 1.15)
  }
  const percent = Math.round(scale * 100)
  return (
    <Dialog
      open={image !== undefined && image !== null}
      onOpenChange={(open) => {
        if (!open) onIndex(null)
        setZoom(null)
        setNatural(null)
      }}
    >
      <DialogContent
        hideClose
        className="flex h-[calc(100dvh-2rem)] w-[calc(100vw-2rem)] max-w-6xl flex-col gap-0 overflow-hidden p-0"
        // Focus on Close, not on a button with a tooltip (Escape would close the tooltip first).
        onOpenAutoFocus={(e) => {
          e.preventDefault()
          close.current?.focus()
        }}
        onKeyDown={(e) => {
          if (e.key === 'ArrowLeft') go(-1)
          if (e.key === 'ArrowRight') go(1)
          if (e.key === '+' || e.key === '=') zoomBy(1.25)
          if (e.key === '-') zoomBy(1 / 1.25)
          if (e.key === '0') setZoom(null)
        }}
      >
        {image && (
          <>
            <header className="flex h-12 shrink-0 items-center gap-2 border-b pr-2 pl-4">
              <div className="grid min-w-0">
                <DialogTitle className="truncate text-sm leading-tight">{image.name}</DialogTitle>
                <DialogDescription className="text-xs">
                  {image.detail}
                  {many && ` · ${index! + 1} of ${images.length}`}
                </DialogDescription>
              </div>
              <span className="ml-auto flex shrink-0 items-center">
                <Tooltip content="Zoom out (-)">
                  <Button variant="ghost" size="icon-sm" onClick={() => zoomBy(1 / 1.25)} disabled={scale <= MIN} aria-label="Zoom out">
                    <ZoomOut />
                  </Button>
                </Tooltip>
                <Tooltip content={zoom === null ? 'Fitted to the screen' : 'Fit to the screen (0)'}>
                  <Button variant="ghost" size="sm" className="h-8 w-14 px-1 text-xs tabular-nums" onClick={() => setZoom(null)} aria-label={`Zoom ${percent}%: fit to the screen`}>
                    {percent}%
                  </Button>
                </Tooltip>
                <Tooltip content="Zoom in (+)">
                  <Button variant="ghost" size="icon-sm" onClick={() => zoomBy(1.25)} disabled={scale >= MAX} aria-label="Zoom in">
                    <ZoomIn />
                  </Button>
                </Tooltip>
                <Tooltip content="Open in a new tab">
                  <Button variant="ghost" size="icon-sm" asChild>
                    <a href={image.src} target="_blank" rel="noopener" aria-label="Open in a new tab">
                      <ExternalLink />
                    </a>
                  </Button>
                </Tooltip>
                {image.download && (
                  <Tooltip content={`Download ${image.download.name}`}>
                    <Button variant="ghost" size="icon-sm" asChild>
                      <a href={image.download.href} download={image.download.name} aria-label={`Download ${image.download.name}`}>
                        <Download />
                      </a>
                    </Button>
                  </Tooltip>
                )}
                <DialogClose asChild>
                  <Button ref={close} variant="ghost" size="icon-sm" aria-label="Close">
                    <X />
                  </Button>
                </DialogClose>
              </span>
            </header>
            <div className="relative min-h-0 flex-1 bg-muted/40">
              <div ref={area} onWheel={onWheel} className={cn('absolute inset-0', zoom !== null ? 'overflow-auto' : 'flex items-center justify-center overflow-hidden p-4')}>
              <div className={cn(zoom !== null ? 'flex min-h-full w-max min-w-full items-center justify-center p-4' : 'h-full w-full')}>
                <button
                  type="button"
                  onClick={() => setZoom(zoom === null ? 1 : null)}
                  className={cn('outline-none focus-visible:ring-[3px] focus-visible:ring-ring', zoom !== null ? 'block cursor-zoom-out' : 'flex h-full w-full cursor-zoom-in items-center justify-center')}
                  aria-label={zoom !== null ? 'Fit to the screen' : 'Show at actual size'}
                >
                  <img
                    src={image.src}
                    alt={image.name}
                    onLoad={(e) => {
                      const own = { w: e.currentTarget.naturalWidth, h: e.currentTarget.naturalHeight }
                      setNatural(own)
                      // Fitted: smaller to fit the screen, never larger than it is.
                      if (area.current) setFit(Math.min(1, (area.current.clientWidth - 32) / own.w, (area.current.clientHeight - 32) / own.h))
                    }}
                    style={zoom !== null && natural ? { width: Math.round(natural.w * zoom) } : undefined}
                    className={cn(zoom !== null ? 'h-auto max-w-none' : 'mx-auto max-h-full max-w-full object-contain')}
                  />
                </button>
              </div>
              </div>
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
