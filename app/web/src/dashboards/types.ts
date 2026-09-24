export interface GridPos {
  x: number
  y: number
  w: number
  h: number
}

export interface Override {
  matcher: { id: string; options: string }
  properties: { id: string; value: unknown }[]
}

export interface PanelDef {
  key: number
  type: string
  title?: string
  description?: string
  gridPos?: GridPos
  supported: boolean
  datasources: string[]
  fieldConfig?: {
    defaults?: { unit?: string; decimals?: number; min?: number; max?: number; custom?: { stacking?: { mode?: string }; drawStyle?: string } }
    overrides?: Override[]
  }
  options?: { reduceOptions?: { calcs?: string[] }; content?: string; mode?: string }
}

export interface DashboardDef {
  uid: string
  title: string
  time?: { from: string; to: string }
  refresh?: string
  panels: PanelDef[]
}

export interface Column {
  name: string
  type: string
}

export interface TargetResult {
  refId: string
  format: string
  table: { columns: Column[]; rows: unknown[][]; capped: boolean } | null
  series: { name: string; points: (number | null)[][] }[] | null
  error: string | null
}

export interface PanelData {
  intervalMs: number
  results: TargetResult[]
}
