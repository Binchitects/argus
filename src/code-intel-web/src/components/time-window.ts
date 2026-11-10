// A report's time window (the overview's and people's spend), as the address keeps it: from and to, a preset's words
// (now-7d, now/M, now-1M/M, as Arena's reports have them) or two days. Plain functions: test/time-window.test.mjs.

/** The window as the address has it; none: the gateway's budget period. */
export interface TimeWindow {
  from: string;
  to: string;
}

/** The windows offered, the budget period first (no window). */
export const presets: { label: string; from: string; to: string }[] = [
  { label: "This budget period", from: "", to: "" },
  { label: "Today", from: "now/d", to: "now/d" },
  { label: "Last 7 days", from: "now-7d", to: "now" },
  { label: "Last 30 days", from: "now-30d", to: "now" },
  { label: "Last 90 days", from: "now-90d", to: "now" },
  { label: "This month", from: "now/M", to: "now" },
  { label: "Last month", from: "now-1M/M", to: "now-1M/M" },
];

/** The longest window the server reads at once. */
export const mostDays = 400;

const day = (d: Date) => d.toISOString().slice(0, 10);
const isDay = (t: string) => /^\d{4}-\d{2}-\d{2}$/.test(t) && !Number.isNaN(Date.parse(`${t}T00:00:00Z`));

/**
 * The window's first and last day (both in, UTC as the gateway counts), for the API's from and to; null for the budget
 * period, or a window it cannot read.
 */
export function days(w: TimeWindow, now: Date = new Date()): { from: string; to: string } | null {
  if (!w.from && !w.to) return null;
  const today = day(now);
  const shift = (days: number) => day(new Date(now.getTime() - days * 86_400_000));
  const monthStart = (back: number) => day(new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - back, 1)));
  const monthEnd = (back: number) => day(new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - back + 1, 0)));
  const first = (t: string): string | null => {
    if (isDay(t)) return t;
    if (t === "now/d") return today;
    if (t === "now/M") return monthStart(0);
    if (t === "now-1M/M") return monthStart(1);
    // The last N days: today and the N - 1 before it.
    const m = /^now-(\d+)d$/.exec(t);
    return m ? shift(Number(m[1]) - 1) : null;
  };
  const last = (t: string): string | null => (isDay(t) ? t : t === "now" || t === "now/d" ? today : t === "now-1M/M" ? monthEnd(1) : null);
  const f = first(w.from);
  const l = last(w.to);
  return f && l && f <= l ? { from: f, to: l } : null;
}

/** Why two days are no window: one missing, the first after the last, or too long. */
export function problem(from: string, to: string): string | null {
  if (!isDay(from) || !isDay(to)) return "Choose both days.";
  if (from > to) return "The first day comes before the last.";
  if ((Date.parse(to) - Date.parse(from)) / 86_400_000 >= mostDays) return `A window is at most ${mostDays} days.`;
  return null;
}

/** The window in words: a preset's name, or its two days. */
export function label(w: TimeWindow): string {
  const preset = presets.find((p) => p.from === w.from && p.to === w.to);
  if (preset) return preset.label;
  return w.from === w.to ? w.from : `${w.from} to ${w.to}`;
}
