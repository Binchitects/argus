// Where the workbench's parts are, as the person arranged them, and the moves that arrange them: the side bar on the
// left or the right (the chat on the other side), the panel below the editor or beside it, the views in the order the
// person dragged them, each in the side bar or in the panel. Kept by code-arena for every run (useLayout in app.tsx).

export type View = 'explorer' | 'search' | 'git' | 'debug' | 'extensions' | 'changes' | 'problems' | 'review' | 'plan' | 'memory' | 'chat'

export const allViews: readonly View[] = ['explorer', 'search', 'git', 'debug', 'extensions', 'changes', 'problems', 'review', 'plan', 'memory', 'chat']

/** The panel's own tabs: the terminals, the Debug Console, the Output (the language servers'). */
export type PanelPart = 'terminal' | 'console' | 'output'
export const panelParts: readonly PanelPart[] = ['terminal', 'console', 'output']

/** A tab of the panel: one of its own, or a view moved there. */
export type PanelTab = PanelPart | View

/** Where the panels are and how large, and which views are where. */
export interface Layout {
  view: View
  side: number
  chat: number
  panel: number
  sideOpen: boolean
  chatOpen: boolean
  panelOpen: boolean
  panelTab: PanelTab
  /** The side bar (and its activity bar): left or right; the chat is on the other side. */
  sideAt: 'left' | 'right'
  /** The panel: below the editor, or beside it. */
  panelAt: 'bottom' | 'left' | 'right'
  /** Every view, in the order the activity bar shows those in the side bar. */
  order: View[]
  /** The panel's tabs in their order: its own, and the views moved there (no longer in the activity bar). */
  panelTabs: PanelTab[]
}

export const defaults: Layout = {
  view: 'explorer',
  side: 264,
  chat: 440,
  panel: 280,
  sideOpen: true,
  chatOpen: true,
  panelOpen: false,
  panelTab: 'terminal',
  sideAt: 'left',
  panelAt: 'bottom',
  order: [...allViews],
  panelTabs: [...panelParts],
}

/** The panels' sizes, smallest and largest (the panel's largest also follows the window). */
export const bounds = { side: [180, 640], chat: [320, 960], panel: [100, 4000] } as const

const isView = (x: unknown): x is View => typeof x === 'string' && (allViews as readonly string[]).includes(x)
const isTab = (x: unknown): x is PanelTab => isView(x) || (typeof x === 'string' && (panelParts as readonly string[]).includes(x))

/** The layout saved, what of it still makes sense: the known keys, of their kind, the sizes within bounds, every view once. */
export function layoutOf(saved: Record<string, unknown> | undefined): Layout {
  const layout: Layout = { ...defaults, order: [...defaults.order], panelTabs: [...defaults.panelTabs] }
  if (!saved) return layout
  for (const k of ['side', 'chat', 'panel'] as const) {
    const size = saved[k]
    if (typeof size === 'number' && Number.isFinite(size)) layout[k] = Math.round(Math.min(bounds[k][1], Math.max(bounds[k][0], size)))
  }
  for (const k of ['sideOpen', 'chatOpen', 'panelOpen'] as const) {
    const open = saved[k]
    if (typeof open === 'boolean') layout[k] = open
  }
  if (saved.sideAt === 'left' || saved.sideAt === 'right') layout.sideAt = saved.sideAt
  if (saved.panelAt === 'bottom' || saved.panelAt === 'left' || saved.panelAt === 'right') layout.panelAt = saved.panelAt
  if (Array.isArray(saved.order)) {
    const kept = [...new Set(saved.order.filter(isView))]
    layout.order = [...kept, ...allViews.filter((v) => !kept.includes(v))]
  }
  if (Array.isArray(saved.panelTabs)) {
    const kept = [...new Set(saved.panelTabs.filter(isTab))]
    // The panel's own tabs are always there; a view moved there stays.
    layout.panelTabs = [...kept, ...panelParts.filter((p) => !kept.includes(p))]
  }
  // A view the side bar shows is one of its own; a tab the panel shows is one of its.
  const side = sideViews(layout)
  if (isView(saved.view) && side.includes(saved.view)) layout.view = saved.view
  else layout.view = side[0] ?? 'explorer'
  if (isTab(saved.panelTab) && layout.panelTabs.includes(saved.panelTab)) layout.panelTab = saved.panelTab
  if (side.length === 0) layout.sideOpen = false
  return layout
}

/** The views the side bar has, in the activity bar's order. */
export const sideViews = (l: Layout): View[] => l.order.filter((v) => !l.panelTabs.includes(v))

export const inPanel = (l: Layout, v: View) => l.panelTabs.includes(v)

/** A view moved into the panel (shown there); the side bar shows another, or closes when it has none left. */
export function toPanel(l: Layout, v: View, before?: PanelTab): Partial<Layout> {
  const tabs = place(l.panelTabs.filter((t) => t !== v), v, before)
  const left = l.order.filter((x) => !tabs.includes(x))
  const view = l.view === v ? (left[0] ?? l.view) : l.view
  return { panelTabs: tabs, panelTab: v, panelOpen: true, view, sideOpen: left.length > 0 && l.sideOpen }
}

/** A view moved back into the side bar (before another of its views, or last), and shown there. */
export function toSide(l: Layout, v: View, before?: View): Partial<Layout> {
  const panelTabs = l.panelTabs.filter((t) => t !== v)
  const order = place(l.order.filter((x) => x !== v), v, before)
  const panelTab = l.panelTab === v ? (panelTabs[0] ?? 'terminal') : l.panelTab
  return { panelTabs, order, view: v, sideOpen: true, panelTab }
}

/** A view of the activity bar moved before another (or to its end); a view in the panel goes back to the side bar so. */
export function reorderSide(l: Layout, v: View, before?: View): Partial<Layout> {
  return inPanel(l, v) ? toSide(l, v, before) : { order: place(l.order.filter((x) => x !== v), v, before) }
}

/** A tab of the panel moved before another (or to its end); a view from the side bar comes into the panel so. */
export function reorderPanel(l: Layout, t: PanelTab, before?: PanelTab): Partial<Layout> {
  if (isView(t) && !inPanel(l, t)) return toPanel(l, t, before)
  return { panelTabs: place(l.panelTabs.filter((x) => x !== t), t, before) }
}

/** Every view back where it started and in its first order; the side bar on the left, the panel below (sizes stay). */
export function resetLocations(l: Layout): Partial<Layout> {
  return {
    order: [...allViews],
    panelTabs: [...panelParts],
    sideAt: 'left',
    panelAt: 'bottom',
    panelTab: isView(l.panelTab) ? 'terminal' : l.panelTab,
  }
}

/** `item` put in `list` before `before` (absent: last). */
function place<T>(list: T[], item: T, before?: T): T[] {
  const at = before === undefined ? -1 : list.indexOf(before)
  return at < 0 ? [...list, item] : [...list.slice(0, at), item, ...list.slice(at)]
}
