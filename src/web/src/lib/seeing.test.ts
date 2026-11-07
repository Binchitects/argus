import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'
import { applySeeing, reducedMotion, savedSeeing, seeingDefaults, seeingOf, setSeeing } from './seeing'

const root = document.documentElement
const attributes = () => Object.fromEntries(['contrast', 'text', 'scale', 'focus', 'motion', 'colors'].flatMap((k) => (root.dataset[k] ? [[k, root.dataset[k]]] : [])))

afterEach(() => {
  setSeeing(seeingDefaults)
  localStorage.clear()
})

describe('easier to see', () => {
  it('reads what was saved, keeping what makes sense', () => {
    expect(seeingOf(null)).toEqual(seeingDefaults)
    expect(seeingOf({ contrast: 'more', text: 125, scale: 113, focus: 'strong', motion: 'reduce', colors: 'safe', other: 1 })).toEqual({ contrast: 'more', text: 125, scale: 100, focus: 'strong', motion: 'reduce', colors: 'safe' })
    expect(seeingOf({ contrast: 'max', text: '125' })).toEqual(seeingDefaults)
  })

  it('sets an attribute on the page for each choice that is not the default, and keeps them on this browser', () => {
    setSeeing({ contrast: 'more', text: 150, motion: 'reduce' })
    expect(attributes()).toEqual({ contrast: 'more', text: '150', motion: 'reduce' })
    expect(JSON.parse(localStorage.getItem('seeing')!)).toMatchObject({ contrast: 'more', text: 150, motion: 'reduce' })
    expect(reducedMotion()).toBe(true)
    setSeeing({ text: 100, scale: 125, colors: 'safe', focus: 'strong' })
    expect(attributes()).toEqual({ contrast: 'more', scale: '125', motion: 'reduce', colors: 'safe', focus: 'strong' })
    expect(savedSeeing()).toMatchObject({ text: 100, scale: 125 })
    // Back to the defaults: none at all, the page as it always was.
    applySeeing(seeingDefaults)
    expect(attributes()).toEqual({})
    expect(reducedMotion()).toBe(false)
  })

  it('is on the page before its first paint (theme-init.js), as the browser kept it', () => {
    localStorage.setItem('seeing', JSON.stringify({ contrast: 'more', text: 112, scale: 150, focus: 'strong', motion: 'reduce', colors: 'safe' }))
    new Function(readFileSync(join(process.cwd(), 'public/theme-init.js'), 'utf8'))()
    expect(attributes()).toEqual({ contrast: 'more', text: '112', scale: '150', focus: 'strong', motion: 'reduce', colors: 'safe' })
    // Nonsense kept: nothing set.
    applySeeing(seeingDefaults)
    localStorage.setItem('seeing', '{"text": 999, "contrast": "max"')
    new Function(readFileSync(join(process.cwd(), 'public/theme-init.js'), 'utf8'))()
    expect(attributes()).toEqual({})
  })
})
