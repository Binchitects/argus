import { useQueryClient } from '@tanstack/react-query'
import { api, meQuery } from '@/lib/api'

/** Ends the session here and everywhere that trusts it; the shell then shows the sign-in page. */
export function useSignOut() {
  const queryClient = useQueryClient()
  return async () => {
    await api('/api/auth/logout', { method: 'POST', body: {} }).catch(() => undefined)
    // Set "nobody" first, on the query the shell is watching (clearing the cache
    // would drop that query without telling the shell), then forget the rest.
    queryClient.setQueryData(meQuery.queryKey, null)
    queryClient.removeQueries({ predicate: (q) => q.queryKey[0] !== meQuery.queryKey[0] })
  }
}
