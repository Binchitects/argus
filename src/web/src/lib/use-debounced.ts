import { useEffect, useState } from 'react'

/** A value that follows another once it has stopped changing for `ms` (typing, say), for what asks the server. */
export function useDebounced<T>(value: T, ms: number): T {
  const [v, setV] = useState(value)
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms)
    return () => clearTimeout(t)
  }, [value, ms])
  return v
}
