import { useCallback, useEffect, useRef, useState } from "react";

/**
 * Load something, expose error and a reload; optionally poll while `pollMs(value)` says so. Only the latest load's answer
 * is kept: an earlier one that comes in after it (a slower search, a poll) is dropped.
 */
export function useAsync<T>(load: () => Promise<T>, deps: unknown[], pollMs?: (value: T | null) => number | null) {
  const [value, setValue] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const latest = useRef(0);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const reload = useCallback(async () => {
    const mine = ++latest.current;
    try {
      const loaded = await load();
      if (mine !== latest.current) return;
      setValue(loaded);
      setError(null);
    } catch (e) {
      if (mine !== latest.current) return;
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
