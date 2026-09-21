export interface AppInfo {
  name: string
  version: string
}

export async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(path, { signal, headers: { Accept: 'application/json' } })
  if (!res.ok) throw new Error(`${path}: HTTP ${res.status}`)
  return (await res.json()) as T
}

export const infoQuery = {
  queryKey: ['info'],
  queryFn: ({ signal }: { signal: AbortSignal }) => getJson<AppInfo>('/api/info', signal),
  staleTime: Infinity,
}
