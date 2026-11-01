import { createContext, use, useCallback, useEffect, useLayoutEffect, useMemo, useState, type ReactNode } from 'react'
import { isColorTheme, type ColorThemeId } from './color-themes'

export type ThemePreference = 'light' | 'dark' | 'system'

interface ThemeState {
  preference: ThemePreference
  resolved: 'light' | 'dark'
  setPreference: (p: ThemePreference) => void
  /** The person's colour theme ("default": Arena's own); remembered per browser, and followed from their account (app/color-theme.ts). */
  colorTheme: ColorThemeId
  setColorTheme: (t: ColorThemeId) => void
  /** The colour theme shown now: the person's, or the one Code Arena's IDE shows while it is open. */
  shownColorTheme: ColorThemeId
  /** Shows a colour theme over the person's without keeping it (Code Arena's IDE); null shows theirs again. */
  showColorTheme: (t: ColorThemeId | null) => void
}

const ThemeContext = createContext<ThemeState | null>(null)

function read(): ThemePreference {
  try {
    const v = localStorage.getItem('theme')
    return v === 'light' || v === 'dark' ? v : 'system'
  } catch {
    return 'system'
  }
}

function readColorTheme(): ColorThemeId {
  try {
    const v = localStorage.getItem('color-theme')
    return isColorTheme(v) ? v : 'default'
  } catch {
    return 'default'
  }
}

const systemDark = () => typeof window !== 'undefined' && !!window.matchMedia?.('(prefers-color-scheme: dark)').matches

/**
 * Light, dark or the system's choice, and the colour theme; both remembered per browser (theme-init.js applies them
 * before the first paint). The colour theme is <html data-color-theme>, none for the default.
 */
export function ThemeProvider({ children }: { children: ReactNode }) {
  const [preference, setPref] = useState<ThemePreference>(read)
  const [dark, setDark] = useState(systemDark)
  const [colorTheme, setColor] = useState<ColorThemeId>(readColorTheme)
  const [shownOver, showColorTheme] = useState<ColorThemeId | null>(null)

  useEffect(() => {
    const mq = window.matchMedia?.('(prefers-color-scheme: dark)')
    if (!mq) return
    const on = () => setDark(mq.matches)
    mq.addEventListener('change', on)
    return () => mq.removeEventListener('change', on)
  }, [])

  const resolved: 'light' | 'dark' = preference === 'system' ? (dark ? 'dark' : 'light') : preference
  const shownColorTheme = shownOver ?? colorTheme

  // Before any effect of the page's (they run first otherwise): a chart that reads the colours then has the new ones.
  useLayoutEffect(() => {
    document.documentElement.classList.toggle('dark', resolved === 'dark')
    document.documentElement.style.colorScheme = resolved
  }, [resolved])

  useLayoutEffect(() => {
    if (shownColorTheme === 'default') delete document.documentElement.dataset.colorTheme
    else document.documentElement.dataset.colorTheme = shownColorTheme
  }, [shownColorTheme])

  const setPreference = useCallback((p: ThemePreference) => {
    setPref(p)
    try {
      localStorage.setItem('theme', p)
    } catch {
      // private window: the choice lasts for this page only
    }
  }, [])

  const setColorTheme = useCallback((t: ColorThemeId) => {
    setColor(t)
    try {
      localStorage.setItem('color-theme', t)
    } catch {
      // private window: the choice lasts for this page only
    }
  }, [])

  const value = useMemo(
    () => ({ preference, resolved, setPreference, colorTheme, setColorTheme, shownColorTheme, showColorTheme }),
    [preference, resolved, setPreference, colorTheme, setColorTheme, shownColorTheme],
  )
  return <ThemeContext value={value}>{children}</ThemeContext>
}

export function useTheme() {
  const ctx = use(ThemeContext)
  if (!ctx) throw new Error('useTheme needs <ThemeProvider>')
  return ctx
}
