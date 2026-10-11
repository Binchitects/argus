import { useEffect } from 'react'
import { useTheme } from '@/lib/theme'
import { useSetting } from './settings'
import { themeNamed } from './settings-schema'

/**
 * The colour theme the settings name: "workbench.colorTheme", or with "window.autoDetectColorScheme" the preferred light
 * or dark one as the system is. The page's own light or dark follows it (and the browser keeps it, for the next page's
 * first paint).
 */
export function useThemeSync() {
  const { setPreference, preference } = useTheme()
  const auto = useSetting<boolean>('window.autoDetectColorScheme')
  const name = useSetting<string>('workbench.colorTheme')
  const wanted = auto ? 'system' : themeNamed(name).kind
  useEffect(() => {
    if (preference !== wanted) setPreference(wanted)
  }, [wanted, preference, setPreference])
}

/** Monaco's theme for the settings and the page's light or dark now. */
export function useMonacoTheme(): string {
  const { resolved } = useTheme()
  const auto = useSetting<boolean>('window.autoDetectColorScheme')
  const name = useSetting<string>('workbench.colorTheme')
  const dark = useSetting<string>('workbench.preferredDarkColorTheme')
  const light = useSetting<string>('workbench.preferredLightColorTheme')
  return themeNamed(auto ? (resolved === 'dark' ? dark : light) : name).monaco
}
