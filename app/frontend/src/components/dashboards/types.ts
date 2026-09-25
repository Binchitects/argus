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

export interface Threshold {
  color: string
  value: number | null
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
    defaults?: {
      unit?: string
      decimals?: number
      min?: number
      max?: number
      noValue?: string
      color?: { mode?: string; fixedColor?: string }
      thresholds?: { mode?: string; steps?: Threshold[] }
      custom?: { stacking?: { mode?: string }; drawStyle?: string }
    }
    overrides?: Override[]
  }
  options?: {
    reduceOptions?: { calcs?: string[]; values?: boolean }
    content?: string
    mode?: string
    colorMode?: string
    graphMode?: string
    textMode?: string
    showTime?: boolean
    showLabels?: boolean
    wrapLogMessage?: boolean
    sortOrder?: string
    enableLogDetails?: boolean
  }
  transformations?: { id: string; options?: { excludeByName?: Record<string, boolean>; renameByName?: Record<string, string>; indexByName?: Record<string, number> } }[]
}

export interface VariableDef {
  name: string
  label: string
  type: string
  multi: boolean
  includeAll: boolean
  current: string[]
}

export interface DashboardDef {
  uid: string
  title: string
  time?: { from: string; to: string }
  refresh?: string
  panels: PanelDef[]
  variables?: VariableDef[]
}

export interface LogLine {
  time: number
  nanos: string
  labels: Record<string, string>
  line: string
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
  logs?: LogLine[] | null
}

export interface PanelData {
  intervalMs: number
  results: TargetResult[]
}
