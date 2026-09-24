import { MutationCache, QueryCache, QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { useState, type ReactNode } from 'react'
import { ConfirmProvider } from '@/components/ui/confirm'
import { Toaster, toast } from '@/components/ui/toaster'
import { TooltipProvider } from '@/components/ui/tooltip'
import { ApiError, meQuery } from '@/lib/api'
import { ThemeProvider } from '@/lib/theme'

/**
 * A 401 from any call while signed in means the session ended (idle timeout,
 * signed out elsewhere, disabled): forget who we are and the shell sends the
 * person to sign in, back to where they were.
 */
export function makeQueryClient() {
  const client: QueryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: (n, e) => !(e instanceof ApiError && e.http < 500) && n < 2,
        refetchOnWindowFocus: false,
      },
    },
    queryCache: new QueryCache({ onError: (e, q) => onUnauthorized(e, q.queryKey) }),
    mutationCache: new MutationCache({ onError: (e) => onUnauthorized(e) }),
  })
  function onUnauthorized(e: unknown, key?: readonly unknown[]) {
    if (!(e instanceof ApiError) || e.http !== 401 || key?.[0] === 'me') return
    if (client.getQueryData(meQuery.queryKey)) {
      toast.info('Your session ended. Sign in again to continue.')
      client.setQueryData(meQuery.queryKey, null)
    }
  }
  return client
}

export function Providers({ children, client }: { children: ReactNode; client?: QueryClient }) {
  const [queryClient] = useState(() => client ?? makeQueryClient())
  return (
    <ThemeProvider>
      <QueryClientProvider client={queryClient}>
        <TooltipProvider delayDuration={300}>
          <ConfirmProvider>
            {children}
            <Toaster />
          </ConfirmProvider>
        </TooltipProvider>
      </QueryClientProvider>
    </ThemeProvider>
  )
}
