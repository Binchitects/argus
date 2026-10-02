/**
 * Desktop notifications: the browser's own, for news that arrives while the app's tab
 * is hidden. Off until the person turns them on (the browser asks them once); they can
 * turn them off here again without going into the browser's settings.
 */
const KEY = 'desktop-notifications'

export type DesktopState = 'on' | 'off' | 'blocked' | 'unsupported'

export function desktopState(): DesktopState {
  if (typeof Notification === 'undefined') return 'unsupported'
  if (Notification.permission === 'denied') return 'blocked'
  try {
    return Notification.permission === 'granted' && localStorage.getItem(KEY) === 'on' ? 'on' : 'off'
  } catch {
    return 'off'
  }
}

export async function setDesktop(on: boolean): Promise<DesktopState> {
  if (typeof Notification === 'undefined') return 'unsupported'
  if (on && Notification.permission === 'default') await Notification.requestPermission()
  try {
    localStorage.setItem(KEY, on ? 'on' : 'off')
  } catch {
    // Private windows: on for this page only, through the permission.
  }
  return desktopState()
}

/** Says it on the desktop when the tab is hidden and they are on; a click brings the tab back and opens what it is about. */
export function tellDesktop(title: string, body: string | null | undefined, tag: string, open?: () => void) {
  if (typeof document === 'undefined' || !document.hidden || desktopState() !== 'on') return
  try {
    const n = new Notification(title, { body: body?.slice(0, 200) ?? undefined, tag, icon: '/favicon.svg' })
    n.onclick = () => {
      window.focus()
      open?.()
      n.close()
    }
  } catch {
    // Some browsers allow them only from a service worker (Android Chrome): the bell still has it.
  }
}
