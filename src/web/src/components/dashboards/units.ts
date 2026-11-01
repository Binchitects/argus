import type { PanelDef } from './types'

/**
 * Each query's unit where its overrides give every one of its series the same (byName, or byRegexp, which Grafana tests
 * anywhere in a series' name): the folded line of a GPU's temperatures is an average in °C even when the panel's own unit is none.
 */
export function queryUnits(panel: PanelDef, results: { refId: string; series: { name: string }[] | null }[]): Record<string, string | undefined> {
  const out: Record<string, string | undefined> = {}
  for (const r of results) {
    const names = (r.series ?? []).map((x) => x.name)
    if (!names.length) continue
    const units = new Set(names.map((name) => {
      let unit: string | undefined
      for (const o of panel.fieldConfig?.overrides ?? []) {
        let hit = false
        if (o.matcher.id === 'byName') hit = o.matcher.options === name
        else if (o.matcher.id === 'byRegexp') {
          try {
            hit = new RegExp(String(o.matcher.options)).test(name)
          } catch {
            hit = false
          }
        }
        for (const p of hit ? o.properties : []) if (p.id === 'unit') unit = String(p.value)
      }
      return unit
    }))
    const [only] = [...units]
    if (units.size === 1 && only !== undefined) out[r.refId] = only
  }
  return out
}
