import { createContext, use, useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'

export type ThemePreference = 'light' | 'dark' | 'system'

interface ThemeState {
  preference: ThemePreference
  resolved: 'light' | 'dark'
  setPreference: (p: ThemePreference) => void
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

const systemDark = () => typeof window !== 'undefined' && !!window.matchMedia?.('(prefers-color-scheme: dark)').matches

/** Light, dark or the system's choice; remembered per browser (theme-init.js applies it before the first paint). */
export function ThemeProvider({ children }: { children: ReactNode }) {
  const [preference, setPref] = useState<ThemePreference>(read)
  const [dark, setDark] = useState(systemDark)

  useEffect(() => {
    const mq = window.matchMedia?.('(prefers-color-scheme: dark)')
    if (!mq) return
    const on = () => setDark(mq.matches)
    mq.addEventListener('change', on)
    return () => mq.removeEventListener('change', on)
  }, [])

  const resolved: 'light' | 'dark' = preference === 'system' ? (dark ? 'dark' : 'light') : preference

  useEffect(() => {
    document.documentElement.classList.toggle('dark', resolved === 'dark')
    document.documentElement.style.colorScheme = resolved
  }, [resolved])

  const setPreference = useCallback((p: ThemePreference) => {
    setPref(p)
    try {
      localStorage.setItem('theme', p)
    } catch {
      // private window: the choice lasts for this page only
    }
  }, [])

  const value = useMemo(() => ({ preference, resolved, setPreference }), [preference, resolved, setPreference])
  return <ThemeContext value={value}>{children}</ThemeContext>
}

export function useTheme() {
  const ctx = use(ThemeContext)
  if (!ctx) throw new Error('useTheme needs <ThemeProvider>')
  return ctx
}
