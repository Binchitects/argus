import { useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect } from 'react'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { isColorTheme, type ColorThemeId } from '@/lib/color-themes'
import { useTheme } from '@/lib/theme'

/** The person's colour theme follows them: the one their account keeps (me.colorTheme) is taken as it loads, on every device. */
export function useFollowColorTheme(saved: string | undefined) {
  const { setColorTheme } = useTheme()
  useEffect(() => {
    if (isColorTheme(saved)) setColorTheme(saved)
  }, [saved, setColorTheme])
}

/**
 * Chooses a colour theme: shown at once, kept in this browser, and saved to the person's account for their other
 * devices. Who they are (me) is left as it was loaded: a reply that comes late never takes a newer choice back.
 */
export function useChooseColorTheme() {
  const { setColorTheme } = useTheme()
  const queryClient = useQueryClient()
  return useCallback(
    (t: ColorThemeId) => {
      setColorTheme(t)
      api<unknown>('/api/account/preferences', { method: 'PUT', body: { colorTheme: t } }).then(
        (prefs) => queryClient.setQueryData(['account', 'preferences'], prefs),
        (e: unknown) => toast.error(errorMessage(e)),
      )
    },
    [setColorTheme, queryClient],
  )
}
