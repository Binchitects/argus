import { useQueryClient, type QueryClient } from '@tanstack/react-query'
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

// The colour theme chosen last here, and whether a save of one is on its way: one at a time, so quick choices (an arrow
// key held on the swatches) never cross on the way and the account ends with the last of them.
let wanted: ColorThemeId | null = null
let sending = false

function save(t: ColorThemeId, queryClient: QueryClient) {
  sending = true
  void api<unknown>('/api/account/preferences', { method: 'PUT', body: { colorTheme: t } })
    .then(
      (prefs) => {
        if (wanted === t) queryClient.setQueryData(['account', 'preferences'], prefs)
      },
      (e: unknown) => {
        if (wanted === t) toast.error(errorMessage(e))
      },
    )
    .finally(() => {
      sending = false
      if (wanted && wanted !== t) save(wanted, queryClient)
    })
}

/**
 * Chooses a colour theme: shown at once, kept in this browser, and saved to the person's account for their other
 * devices. Who they are (me) is left as it was loaded: a reply that comes late never takes a newer choice back. Choices
 * made while one is being saved wait for it, and only the last of them is sent.
 */
export function useChooseColorTheme() {
  const { setColorTheme } = useTheme()
  const queryClient = useQueryClient()
  return useCallback(
    (t: ColorThemeId) => {
      setColorTheme(t)
      wanted = t
      if (!sending) save(t, queryClient)
    },
    [setColorTheme, queryClient],
  )
}
