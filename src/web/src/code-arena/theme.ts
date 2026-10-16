import { useEffect, useMemo, useSyncExternalStore } from 'react'
import { useTheme } from '@/lib/theme'
import { ensureTheme, setIconTheme, themeColors, themeReady, useExtensionsVersion } from './extensions'
import { useSetting } from './settings'
import { themeNamed } from './settings-schema'
import { pageColors, pageColorVariables, terminalColors } from './vscode-theme'

// The theme previewed while the Color Theme picker is open (the one chosen in it), over the settings' until it closes.
let preview: string | null = null
const previewListeners = new Set<() => void>()

/** Shows a theme while it is chosen in the picker; null goes back to the settings' theme. */
export function previewTheme(name: string | null) {
  if (preview === name) return
  preview = name
  for (const l of previewListeners) l()
}

function usePreview() {
  return useSyncExternalStore(
    (f) => {
      previewListeners.add(f)
      return () => {
        previewListeners.delete(f)
      }
    },
    () => preview,
  )
}

/** The colour theme shown now: the one previewed, else "workbench.colorTheme", or with "window.autoDetectColorScheme" the preferred light or dark one as the page is. */
export function useShownTheme(): string {
  const { resolved } = useTheme()
  const auto = useSetting<boolean>('window.autoDetectColorScheme')
  const name = useSetting<string>('workbench.colorTheme')
  const dark = useSetting<string>('workbench.preferredDarkColorTheme')
  const light = useSetting<string>('workbench.preferredLightColorTheme')
  const shown = usePreview()
  useExtensionsVersion()
  return shown ?? (auto ? (resolved === 'dark' ? dark : light) : name)
}

/**
 * The colour theme the settings name: "workbench.colorTheme", or with "window.autoDetectColorScheme" the preferred light
 * or dark one as the system is. The page's own light or dark follows it (and the browser keeps it, for the next page's
 * first paint); an extension's theme is read, and its workbench colours become the page's. The file icon theme too.
 */
export function useThemeSync() {
  const { setPreference, preference } = useTheme()
  const auto = useSetting<boolean>('window.autoDetectColorScheme')
  const name = useSetting<string>('workbench.colorTheme')
  const shown = useShownTheme()
  const previewing = usePreview()
  const theme = themeNamed(shown)
  const wanted = previewing ? theme.kind : auto ? 'system' : themeNamed(name).kind
  useEffect(() => {
    if (preference !== wanted) setPreference(wanted)
  }, [wanted, preference, setPreference])

  useEffect(() => {
    if (theme.extension) void ensureTheme(theme.name)
  }, [theme])
  // An extension's workbench colours over the page's own; Code Arena's themes take them away again.
  const colors = theme.extension ? themeColors(theme.name) : null
  useEffect(() => {
    const root = document.documentElement.style
    const set = colors ? pageColors(colors) : {}
    for (const v of pageColorVariables) {
      if (set[v]) root.setProperty(v, set[v])
      else root.removeProperty(v)
    }
  }, [colors])

  const icons = useSetting<string>('workbench.iconTheme')
  useEffect(() => setIconTheme(icons, theme.kind === 'light'), [icons, theme.kind])
}

/** Monaco's theme for the theme shown now: an extension's once it is read (Code Arena's of its kind meanwhile). */
export function useMonacoTheme(): string {
  const theme = themeNamed(useShownTheme())
  if (theme.extension && !themeReady(theme.name)) return theme.kind === 'light' ? 'arena-light' : 'arena-dark'
  return theme.monaco
}

/** The terminal's colours an extension's theme gives (none of Code Arena's own). */
export function useTerminalColors(): Record<string, string> | null {
  const theme = themeNamed(useShownTheme())
  const colors = theme.extension ? themeColors(theme.name) : null
  return useMemo(() => (colors ? terminalColors(colors) : null), [colors])
}
