import { useEffect, useMemo, useSyncExternalStore } from 'react'
import { colorThemeOf } from '@/lib/color-themes'
import { applySeeing, savedSeeing, type Seeing } from '@/lib/seeing'
import { useTheme } from '@/lib/theme'
import { useMedia } from '@/lib/use-media'
import { ensureTheme, setIconTheme, themeColors, themeReady, useExtensionsVersion } from './extensions'
import { ownPage } from './http'
import { useSetting } from './settings'
import { arenaMonacoTheme, themeNamed } from './settings-schema'
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

/** Whether the system asks for more contrast, or forces its own colours (Windows' contrast themes). */
function useSystemContrast() {
  const more = useMedia('(prefers-contrast: more)')
  const forced = useMedia('(forced-colors: active)')
  return more || forced
}

/**
 * The colour theme shown now: the one previewed, else "workbench.colorTheme", or with "window.autoDetectColorScheme"
 * the preferred light or dark one as the page is; High Contrast when the system asks for more contrast
 * ("window.autoDetectHighContrast", as VS Code).
 */
export function useShownTheme(): string {
  const { resolved } = useTheme()
  const auto = useSetting<boolean>('window.autoDetectColorScheme')
  const name = useSetting<string>('workbench.colorTheme')
  const dark = useSetting<string>('workbench.preferredDarkColorTheme')
  const light = useSetting<string>('workbench.preferredLightColorTheme')
  const detectContrast = useSetting<boolean>('window.autoDetectHighContrast') !== false
  const systemContrast = useSystemContrast()
  const highContrast = detectContrast && systemContrast
  const shown = usePreview()
  useExtensionsVersion()
  if (shown) return shown
  const chosen = auto ? (resolved === 'dark' ? dark : light) : name
  if (highContrast && !themeNamed(chosen).name.includes('High Contrast')) return themeNamed(chosen).kind === 'light' ? 'Default High Contrast Light' : 'Default High Contrast'
  return chosen
}

/**
 * Code Arena's accessibility settings on the page while the IDE is open: "window.zoomLevel" (each step 20%, as VS
 * Code's), and over any theme more contrast, larger text, strong focus rings, less motion ("workbench.reduceMotion") and
 * colour-blind safe colours (lib/seeing.ts's attributes; index.css draws them). Closed, the page is as the person's own
 * choices have it again (Your account → Appearance, in Code Studio).
 */
export function useSeeingSync() {
  const zoom = useSetting<number>('window.zoomLevel')
  const contrast = useSetting<string>('codeArena.accessibility.contrast')
  const text = useSetting<string>('codeArena.accessibility.textSize')
  const focus = useSetting<string>('codeArena.accessibility.focusRings')
  const motion = useSetting<string>('workbench.reduceMotion')
  const colors = useSetting<boolean>('codeArena.accessibility.colorBlindColors')
  useEffect(() => {
    const size = Number.parseInt(text ?? '100', 10)
    const seeing: Seeing = {
      ...savedSeeing(),
      contrast: contrast === 'more' ? 'more' : 'standard',
      text: size === 112 || size === 125 || size === 150 ? size : 100,
      // The zoom sizes the page itself (below), not by one of the four steps.
      scale: 100,
      focus: focus === 'strong' ? 'strong' : 'standard',
      motion: motion === 'on' ? 'reduce' : 'system',
      colors: colors ? 'safe' : 'standard',
    }
    applySeeing(seeing)
    const level = typeof zoom === 'number' && Number.isFinite(zoom) ? Math.min(9, Math.max(-5, zoom)) : 0
    const root = document.documentElement.style
    if (level === 0) root.removeProperty('font-size')
    else root.fontSize = `${Math.round(100 * 1.2 ** level * 100) / 100}%`
  }, [zoom, contrast, text, focus, motion, colors])
  useEffect(
    () => () => {
      applySeeing(savedSeeing())
      document.documentElement.style.removeProperty('font-size')
    },
    [],
  )
}

/**
 * The colour theme the settings name: "workbench.colorTheme", or with "window.autoDetectColorScheme" the preferred light
 * or dark one as the system is. The page's own light or dark follows it (and the browser keeps it, for the next page's
 * first paint), and so do its colours: one of the app's colour themes (Sakura...) while the IDE is open, kept by the
 * browser only on code-arena's own page (Code Studio's IDE is in the person's own page, whose colour theme is theirs);
 * an extension's theme is read, and its workbench colours become the page's. The file icon theme too.
 */
export function useThemeSync() {
  const { setPreference, preference, showColorTheme, setColorTheme, resolved } = useTheme()
  const auto = useSetting<boolean>('window.autoDetectColorScheme')
  const name = useSetting<string>('workbench.colorTheme')
  const dark = useSetting<string>('workbench.preferredDarkColorTheme')
  const light = useSetting<string>('workbench.preferredLightColorTheme')
  const shown = useShownTheme()
  const previewing = usePreview()
  const theme = themeNamed(shown)
  const wanted = previewing ? theme.kind : auto ? 'system' : themeNamed(name).kind
  useEffect(() => {
    if (preference !== wanted) setPreference(wanted)
  }, [wanted, preference, setPreference])

  const family = theme.family ?? 'default'
  useEffect(() => showColorTheme(family), [family, showColorTheme])
  useEffect(() => () => showColorTheme(null), [showColorTheme])
  // On code-arena's own page nothing else keeps a colour theme: the settings' (not one previewed) is kept, so theme-init.js
  // paints the next page in it at once rather than in Arena's own first.
  const settled = themeNamed(auto ? (resolved === 'dark' ? dark : light) : name).family ?? 'default'
  useEffect(() => {
    if (ownPage()) setColorTheme(settled)
  }, [settled, setColorTheme])

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
  if (theme.extension && !themeReady(theme.name)) return arenaMonacoTheme('default', theme.kind)
  return theme.monaco
}

/** The terminal's colours over its ANSI palette: an extension's theme's, else the page's own as the colour theme shown has them. */
export function useTerminalColors(): Record<string, string> {
  const theme = themeNamed(useShownTheme())
  const colors = theme.extension ? themeColors(theme.name) : null
  const page = colorThemeOf(theme.family ?? 'default')[theme.kind]
  return useMemo(() => {
    const own = { background: page.background, foreground: page.foreground, cursor: page.primary, cursorAccent: page.background, selectionBackground: `${page.primary}${theme.kind === 'light' ? '40' : '4d'}` }
    return colors ? { ...own, ...terminalColors(colors) } : own
  }, [colors, page, theme.kind])
}
