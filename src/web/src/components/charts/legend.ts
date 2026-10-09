/**
 * The widest a legend name may draw on a chart this wide: two names always
 * share the legend's line, beside the pager when there are more to page to, so
 * two series never page and the pager never cuts a name in half. A longer name
 * ends in "…" (whole on hover and in the tooltip).
 */
export function legendNameWidth(chartWidth: number, count: number): number {
  // Per name: its swatch (12) and the gap after it (5); the legend's padding (2 × 5) and the gap between two (10).
  // Past two names the legend may page: its pager (a gap, two arrows and "1/4") takes about 70. No more is
  // left out than that, so the next page's first name starts about where the pager does, not halfway before it.
  const pager = count > 2 ? 70 : 0
  return Math.max(48, Math.floor((chartWidth - 20 - pager) / 2) - 17)
}
