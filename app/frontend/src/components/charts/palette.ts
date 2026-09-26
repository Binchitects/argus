/**
 * Categorical series colours, in a fixed order (validated for colour-vision
 * deficiency against this app's light and dark card surfaces). A series keeps
 * its colour by name, not by rank, so a filter never repaints the survivors.
 */
export const categorical = {
  light: ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#008300', '#4a3aa7', '#e34948'],
  dark: ['#3987e5', '#d95926', '#199e70', '#c98500', '#d55181', '#008300', '#9085e9', '#e66767'],
}

export function seriesColors(names: string[], theme: 'light' | 'dark'): Map<string, string> {
  const colors = categorical[theme]
  return new Map(names.map((n, i) => [n, colors[i % colors.length]!]))
}

/** A CSS custom property of the page, resolved (text and grid colours follow the theme). */
export function cssColor(name: string, fallback: string): string {
  if (typeof document === 'undefined') return fallback
  const probe = document.createElement('span')
  probe.style.color = `var(${name})`
  probe.style.display = 'none'
  document.body.appendChild(probe)
  const c = getComputedStyle(probe).color
  probe.remove()
  return c || fallback
}
