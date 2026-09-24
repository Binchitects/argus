/**
 * Categorical series colours, in a fixed order (validated for colour-vision
 * deficiency against this app's light and dark card surfaces). A series keeps
 * its colour by name, not by rank, so a filter never repaints the survivors.
 */
const light = ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#008300', '#4a3aa7', '#e34948']
const dark = ['#3987e5', '#d95926', '#199e70', '#c98500', '#d55181', '#008300', '#9085e9', '#e66767']

export const isDark = () => typeof window !== 'undefined' && window.matchMedia?.('(prefers-color-scheme: dark)').matches === true

export function seriesColors(names: string[]): Map<string, string> {
  const colors = isDark() ? dark : light
  const out = new Map<string, string>()
  names.forEach((n, i) => out.set(n, colors[i % colors.length]))
  return out
}

/** Read a CSS custom property of the page (text and grid colours follow the theme). */
export function cssVar(name: string, fallback: string): string {
  if (typeof document === 'undefined') return fallback
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback
}
