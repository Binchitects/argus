import { useSyncExternalStore } from 'react'

/**
 * The installable app: its service worker (the app's shell offline, and the bell's news
 * pushed while no page is open), the browser's offer to install it, and this device's
 * push subscription.
 */

/** Registers /sw.js once the page has loaded. Not in development (Vite serves the files). */
export function registerServiceWorker() {
  if (import.meta.env.DEV || typeof navigator === 'undefined' || !('serviceWorker' in navigator)) return
  window.addEventListener('load', () => {
    navigator.serviceWorker.register('/sw.js').catch(() => {
      // An untrusted certificate, or a private window: the app works without it.
    })
  })
  listenForInstall()
}

interface InstallPrompt extends Event {
  prompt: () => Promise<void>
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>
}

let offer: InstallPrompt | null = null
const listeners = new Set<() => void>()
const tell = () => listeners.forEach((l) => l())

/** Keeps the browser's offer to install the app, to make it from a button instead of the address bar. */
export function listenForInstall() {
  window.addEventListener('beforeinstallprompt', (e) => {
    e.preventDefault()
    offer = e as InstallPrompt
    tell()
  })
  window.addEventListener('appinstalled', () => {
    offer = null
    tell()
  })
}

/** Whether the page runs as the installed app (its own window). */
export function isInstalled(): boolean {
  if (typeof window === 'undefined') return false
  return window.matchMedia?.('(display-mode: standalone)').matches === true || (navigator as Navigator & { standalone?: boolean }).standalone === true
}

/** The browser offers to install the app (Chrome, Edge, Android): `install()` asks the person. */
export function useInstall() {
  const canInstall = useSyncExternalStore(
    (l) => {
      listeners.add(l)
      return () => listeners.delete(l)
    },
    () => offer !== null,
    () => false,
  )
  const install = async (): Promise<boolean> => {
    if (!offer) return false
    const asked = offer
    await asked.prompt()
    const { outcome } = await asked.userChoice
    offer = null
    tell()
    return outcome === 'accepted'
  }
  return { canInstall, install, installed: isInstalled() }
}

export type PushSupport = 'supported' | 'unsupported' | 'blocked'

/** Whether this browser can get pushes here: a service worker, the Push API and notifications, not blocked. */
export function pushSupport(): PushSupport {
  if (typeof navigator === 'undefined' || !('serviceWorker' in navigator) || typeof PushManager === 'undefined' || typeof Notification === 'undefined') return 'unsupported'
  return Notification.permission === 'denied' ? 'blocked' : 'supported'
}

/** A base64url text (a VAPID key) as bytes, as pushManager.subscribe() takes it. */
export function base64UrlToBytes(text: string): Uint8Array<ArrayBuffer> {
  const b64 = text.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(text.length / 4) * 4, '=')
  const raw = atob(b64)
  const bytes = new Uint8Array(new ArrayBuffer(raw.length))
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i)
  return bytes
}

/** This device's push subscription, if it has one. */
export async function currentSubscription(): Promise<PushSubscription | null> {
  if (pushSupport() === 'unsupported') return null
  const registration = await navigator.serviceWorker.getRegistration()
  return (await registration?.pushManager.getSubscription()) ?? null
}

/**
 * Subscribes this device for pushes signed with `publicKey` (the browser asks the person
 * first), and returns the subscription to give the app. A subscription made with another
 * key (the app's key was made again) is replaced.
 */
export async function subscribe(publicKey: string): Promise<PushSubscriptionJSON> {
  if ((await Notification.requestPermission()) !== 'granted') throw new Error('The browser did not allow notifications for this site.')
  const registration = await navigator.serviceWorker.ready
  const key = base64UrlToBytes(publicKey)
  const old = await registration.pushManager.getSubscription()
  if (old) {
    const same = old.options.applicationServerKey && sameBytes(new Uint8Array(old.options.applicationServerKey), key)
    if (same) return old.toJSON()
    await old.unsubscribe()
  }
  const made = await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key })
  return made.toJSON()
}

/** Ends this device's subscription; its endpoint, for the app to forget it too. */
export async function unsubscribe(): Promise<string | null> {
  const sub = await currentSubscription()
  if (!sub) return null
  await sub.unsubscribe()
  return sub.endpoint
}

function sameBytes(a: Uint8Array, b: Uint8Array) {
  return a.length === b.length && a.every((v, i) => v === b[i])
}
