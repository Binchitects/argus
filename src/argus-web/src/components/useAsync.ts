import { useCallback, useEffect, useState } from "react";

/** Load something, expose error and a reload; optionally poll while `pollMs(value)` says so. */
export function useAsync<T>(load: () => Promise<T>, deps: unknown[], pollMs?: (value: T | null) => number | null) {
  const [value, setValue] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const reload = useCallback(async () => {
    try {
      setValue(await load());
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, deps);
  useEffect(() => {
    void reload();
  }, [reload]);
  const interval = pollMs ? pollMs(value) : null;
  useEffect(() => {
    if (!interval) return;
    const t = setInterval(() => void reload(), interval);
    return () => clearInterval(t);
  }, [interval, reload]);
  return { value, error, reload, setValue };
}
