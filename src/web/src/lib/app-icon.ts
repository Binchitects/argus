import { useEffect } from 'react'

/** The icon the app ships with. */
export const ownIcon = '/favicon.svg'

let current = ownIcon

/** The app's icon now: the one an admin chose (Settings → Branding), else the app's own. For what is not a component (a notification). */
export const appIcon = () => current

/**
 * The app's icon from /api/info, and the browser tab's icon kept to it: the one an admin chose, else the app's own.
 * Undefined while /api/info is read: the app's own until then.
 */
export function useAppIcon(chosen: string | null | undefined): string {
  const icon = chosen || ownIcon
  useEffect(() => {
    current = icon
    const link = document.querySelector<HTMLLinkElement>('link[rel="icon"]')
    if (link && link.getAttribute('href') !== icon) {
      link.href = icon
      // The type the browser goes by: an SVG of the app's own, the picture's own otherwise (it reads the file).
      if (icon === ownIcon) link.type = 'image/svg+xml'
      else link.removeAttribute('type')
    }
  }, [icon])
  return icon
}
