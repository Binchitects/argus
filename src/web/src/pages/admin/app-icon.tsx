import { useQuery, useQueryClient } from '@tanstack/react-query'
import { ImageUp, RotateCcw } from 'lucide-react'
import { useRef, useState } from 'react'
import { useOutletContext } from 'react-router'
import { Button } from '@/components/ui/button'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage, holds, infoQuery, type Me } from '@/lib/api'
import { ownIcon } from '@/lib/app-icon'

/** The most an icon may weigh, as the API takes it (BrandingEndpoints.MostIconBytes). */
const mostBytes = 512 * 1024

/**
 * The app's icon beside its name (Settings → Branding): a picture an admin chooses, shown in the sidebar, on the sign-in
 * page, in the browser's tab, in notifications and on a phone's home screen; or the app's own again.
 */
export function AppIconPanel() {
  const info = useQuery(infoQuery)
  const queryClient = useQueryClient()
  const mayChange = holds(useOutletContext<Me>(), 'settings.branding')
  const input = useRef<HTMLInputElement>(null)
  const [busy, setBusy] = useState(false)
  const chosen = info.data?.icon ?? null
  const done = (what: string) => {
    toast.success(what)
    void queryClient.invalidateQueries({ queryKey: infoQuery.queryKey })
  }
  const upload = async (file: File | undefined) => {
    if (!file) return
    if (file.size > mostBytes) {
      toast.error('That picture is too big', { description: `An icon is at most ${mostBytes / 1024} KB: a picture for a small square.` })
      return
    }
    setBusy(true)
    try {
      const form = new FormData()
      form.append('file', file, file.name)
      await api('/api/admin/branding/icon', { method: 'PUT', body: form })
      done('The new icon is in use')
    } catch (e) {
      toast.error('The icon was not changed', { description: errorMessage(e) })
    } finally {
      setBusy(false)
      if (input.current) input.current.value = ''
    }
  }
  const reset = async () => {
    setBusy(true)
    try {
      await api('/api/admin/branding/icon', { method: 'DELETE' })
      done("The app's own icon is back")
    } catch (e) {
      toast.error('The icon was not changed', { description: errorMessage(e) })
    } finally {
      setBusy(false)
    }
  }
  return (
    <section aria-labelledby="app-icon-title" className="grid gap-3 py-4 last:pb-0 md:grid-cols-[minmax(0,1fr)_minmax(0,1.1fr)] md:gap-6">
      <div className="min-w-0">
        <h3 id="app-icon-title" className="text-sm font-medium">
          Icon
        </h3>
        <p className="mt-1 text-sm text-muted-foreground">
          Shown beside the product name: in the sidebar, on the sign-in page, in the browser's tab, in notifications and on a phone's home screen. A
          square picture (PNG, JPEG, WebP or SVG, at most {mostBytes / 1024} KB); it applies at once.
        </p>
      </div>
      <div className="flex flex-wrap items-center gap-3">
        <img src={chosen ?? ownIcon} alt={chosen ? 'The icon in use' : "The app's own icon"} className="size-14 rounded-lg border bg-background object-contain p-1" />
        {mayChange ? (
          <>
            <input ref={input} type="file" accept="image/png,image/jpeg,image/webp,image/svg+xml" className="sr-only" aria-label="Icon picture" onChange={(e) => void upload(e.target.files?.[0])} />
            <Button variant="outline" onClick={() => input.current?.click()} loading={busy}>
              <ImageUp /> Choose a picture
            </Button>
            {chosen && (
              <Button variant="ghost" onClick={() => void reset()} disabled={busy}>
                <RotateCcw /> Use the app's own
              </Button>
            )}
          </>
        ) : (
          <p className="text-sm text-muted-foreground">Changing it needs the branding permission.</p>
        )}
      </div>
    </section>
  )
}
