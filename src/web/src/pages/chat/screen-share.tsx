import { MonitorUp, MonitorX } from 'lucide-react'
import { useEffect, useRef } from 'react'
import { Button } from '@/components/ui/button'
import { Tooltip } from '@/components/ui/tooltip'
import { canShareScreen, type ScreenShare } from './screen'

/** The box's button: share a screen, or stop sharing it. Not shown where the browser cannot share one. */
export function ScreenButton({ share }: { share: ScreenShare }) {
  if (!canShareScreen()) return null
  const label = share.sharing ? 'Stop sharing your screen' : 'Share your screen: each question takes a picture of it'
  return (
    <Tooltip content={label}>
      <Button type="button" variant={share.sharing ? 'secondary' : 'ghost'} size="icon-sm" aria-label={label} aria-pressed={share.sharing} onClick={() => (share.sharing ? share.stop() : void share.start())}>
        {share.sharing ? <MonitorX /> : <MonitorUp />}
      </Button>
    </Tooltip>
  )
}

/** Above the box while a screen is shared: what, a live view of it, whether the model sees it, and Stop. */
export function ScreenBar({ share, sees }: { share: ScreenShare; sees: boolean }) {
  const preview = useRef<HTMLVideoElement | null>(null)
  useEffect(() => {
    const p = preview.current
    if (p && share.video.current) {
      p.srcObject = share.video.current.srcObject
      void p.play().catch(() => undefined)
    }
  }, [share.sharing, share.video])
  if (!share.sharing) return null
  return (
    <section className="mb-2 flex items-center gap-3 rounded-xl border bg-card px-3 py-2 text-sm shadow-sm" aria-label="Sharing your screen">
      <video ref={preview} muted playsInline className="h-10 w-16 shrink-0 rounded border bg-muted object-cover" aria-hidden="true" />
      <output className="min-w-0 flex-1" aria-live="polite">
        <span className="block truncate font-medium">Sharing {share.label}</span>
        <span className="block text-xs text-muted-foreground">
          {sees ? 'Each question takes a picture of it now.' : 'This chat’s model cannot see pictures: choose one that can, or it gets their names only.'}
        </span>
      </output>
      <Button type="button" variant="outline" size="sm" className="h-7 gap-1 px-2" onClick={share.stop}>
        <MonitorX /> Stop
      </Button>
    </section>
  )
}
