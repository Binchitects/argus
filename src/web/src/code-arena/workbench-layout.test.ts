import { describe, expect, it } from 'vitest'
import { allViews, barViews, defaults, keepWithheld, layoutOf, reorderPanel, reorderSide, resetLocations, setHidden, sideViews, toPanel, toSide, withhold, type Layout } from './workbench-layout'

const apply = (l: Layout, c: Partial<Layout>): Layout => ({ ...l, ...c })

describe('the workbench layout', () => {
  it('reads what was saved, keeping what makes sense: every view once, the panel its own tabs, sizes in bounds', () => {
    const l = layoutOf({
      side: 9999, sideAt: 'right', panelAt: 'left', view: 'problems', panelTab: 'problems',
      order: ['search', 'search', 'nope', 'explorer'], panelTabs: ['problems', 'output', 42], panelOpen: true,
    })
    expect(l.side).toBe(640)
    expect(l.panelAt).toBe('left')
    expect(l.sideAt).toBe('right')
    // Search first as dragged, the views left out after it, none twice.
    expect(l.order.slice(0, 2)).toEqual(['search', 'explorer'])
    expect([...l.order].sort()).toEqual([...allViews].sort())
    // Problems moved to the panel: its tab there (shown), the panel's own tabs kept; the side bar shows one of its own.
    expect(l.panelTabs).toEqual(['problems', 'output', 'terminal', 'console', 'ports'])
    expect(l.panelTab).toBe('problems')
    expect(l.view).toBe('search')
    expect(sideViews(l)).not.toContain('problems')
    // Nothing saved, or nonsense: the defaults.
    expect(layoutOf(undefined)).toEqual(defaults)
    expect(layoutOf({ sideAt: 'up', panelAt: 'middle', order: 'x' })).toEqual(defaults)
  })

  it('moves a view into the panel and back, the side bar showing another meanwhile', () => {
    let l = apply(defaults, { view: 'problems' })
    l = apply(l, toPanel(l, 'problems'))
    expect(l.panelTabs).toEqual(['terminal', 'console', 'output', 'ports', 'problems'])
    expect(l.panelOpen).toBe(true)
    expect(l.panelTab).toBe('problems')
    expect(l.view).toBe('explorer')
    expect(sideViews(l)).not.toContain('problems')

    // Back before Search: in the side bar there, shown; the panel back on one of its own tabs.
    l = apply(l, toSide(l, 'problems', 'search'))
    expect(sideViews(l).slice(0, 3)).toEqual(['explorer', 'problems', 'search'])
    expect(l.sideOpen).toBe(true)
    expect(l.view).toBe('problems')
    expect(l.panelTab).toBe('terminal')
  })

  it('reorders by dragging: within the activity bar, within the panel, and from one into the other', () => {
    let l = defaults
    l = apply(l, reorderSide(l, 'git', 'explorer'))
    expect(sideViews(l).slice(0, 2)).toEqual(['git', 'explorer'])
    l = apply(l, reorderSide(l, 'git'))
    expect(sideViews(l).at(-1)).toBe('git')
    // Dropped on the panel's Output tab: a view comes in before it.
    l = apply(l, reorderPanel(l, 'search', 'output'))
    expect(l.panelTabs).toEqual(['terminal', 'console', 'search', 'output', 'ports'])
    l = apply(l, reorderPanel(l, 'terminal'))
    expect(l.panelTabs).toEqual(['console', 'search', 'output', 'ports', 'terminal'])
    // Dropped on the activity bar: back in the side bar.
    l = apply(l, reorderSide(l, 'search', 'explorer'))
    expect(sideViews(l)[0]).toBe('search')
    expect(l.panelTabs).not.toContain('search')
  })

  it('the last view leaving the side bar closes it; resetting puts everything back but the sizes', () => {
    let l = apply(defaults, { side: 300 })
    for (const v of allViews) l = apply(l, toPanel(l, v))
    expect(sideViews(l)).toEqual([])
    expect(l.sideOpen).toBe(false)
    l = apply(apply(l, { sideAt: 'right', panelAt: 'right' }), resetLocations(l))
    expect(l).toMatchObject({ order: [...allViews], panelTabs: ['terminal', 'console', 'output', 'ports'], sideAt: 'left', panelAt: 'bottom', panelTab: 'terminal', side: 300 })
  })

  it('hides a view from the activity bar (it shows there while it is the view shown), and keeps where the bar is', () => {
    let l = apply(defaults, { view: 'explorer' })
    l = apply(l, setHidden(l, 'review', true))
    l = apply(l, setHidden(l, 'review', true))
    expect(l.hidden).toEqual(['review'])
    expect(barViews(l)).not.toContain('review')
    // Shown by its command: its button is there meanwhile, as VS Code's is.
    expect(barViews(apply(l, { view: 'review' }))).toContain('review')
    expect(barViews(apply(l, { view: 'review', sideOpen: false }))).not.toContain('review')
    l = apply(l, setHidden(l, 'review', false))
    expect(barViews(l)).toEqual(sideViews(l))

    const saved = layoutOf({ hidden: ['skills', 'skills', 'nope', 3], activityBar: 'top' })
    expect(saved.hidden).toEqual(['skills'])
    expect(saved.activityBar).toBe('top')
    expect(layoutOf({ activityBar: 'sideways' }).activityBar).toBe('default')
    expect(resetLocations(apply(saved, {}))).toMatchObject({ hidden: [], activityBar: 'default' })
  })

  it('withholds a view the person may not use, and keeps it where it was in what is saved', () => {
    const saved = apply(layoutOf({}), { view: 'memory', sideOpen: true })
    const shown = withhold(saved, ['memory'])
    expect(shown.order).not.toContain('memory')
    expect(shown.view).toBe('explorer')
    expect(barViews(shown)).not.toContain('memory')
    expect(withhold(saved, [])).toBe(saved)
    // In the panel too: its tab goes, the panel shows the terminal.
    const inPanel = apply(saved, toPanel(saved, 'memory'))
    expect(withhold(inPanel, ['memory'])).toMatchObject({ panelTab: 'terminal' })
    expect(withhold(inPanel, ['memory']).panelTabs).not.toContain('memory')
    // A move made while it is withheld: memory stays in the saved order.
    const moved = keepWithheld(saved, reorderSide(shown, 'search'), ['memory'])
    expect([...moved.order!].sort()).toEqual([...allViews].sort())
    expect(moved.order!.at(-2)).toBe('search')
  })
})
