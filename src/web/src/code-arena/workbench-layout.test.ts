import { describe, expect, it } from 'vitest'
import { allViews, defaults, layoutOf, reorderPanel, reorderSide, resetLocations, sideViews, toPanel, toSide, type Layout } from './workbench-layout'

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
    expect(l.panelTabs).toEqual(['problems', 'output', 'terminal', 'console'])
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
    expect(l.panelTabs).toEqual(['terminal', 'console', 'output', 'problems'])
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
    expect(l.panelTabs).toEqual(['terminal', 'console', 'search', 'output'])
    l = apply(l, reorderPanel(l, 'terminal'))
    expect(l.panelTabs).toEqual(['console', 'search', 'output', 'terminal'])
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
    expect(l).toMatchObject({ order: [...allViews], panelTabs: ['terminal', 'console', 'output'], sideAt: 'left', panelAt: 'bottom', panelTab: 'terminal', side: 300 })
  })
})
