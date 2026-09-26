import '@testing-library/jest-dom/vitest'
import { cleanup, configure } from '@testing-library/react'
import { afterEach, vi } from 'vitest'

// findBy* and waitFor wait up to 5 s, not 1: a page's first render (its code
// loaded on demand) can take longer than a second on a busy CI runner.
configure({ asyncUtilTimeout: 5000 })

afterEach(() => {
  cleanup()
  try {
    localStorage.clear()
  } catch {
    // no storage in this environment
  }
  document.documentElement.className = ''
  delete document.documentElement.dataset.width
})

// What jsdom lacks and Radix, cmdk and the theme use.
if (!window.matchMedia) {
  window.matchMedia = (query: string) =>
    ({ matches: false, media: query, onchange: null, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false }) as MediaQueryList
}
globalThis.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as unknown as typeof ResizeObserver
Element.prototype.hasPointerCapture ??= () => false
Element.prototype.setPointerCapture ??= () => {}
Element.prototype.releasePointerCapture ??= () => {}
// Chrome returns a Promise from scrollIntoView; a bare `() => el.scrollIntoView()`
// effect would return it to React and crash on cleanup. Behave like Chrome.
Element.prototype.scrollIntoView = function () {
  return Promise.resolve() as unknown as void
}
vi.stubGlobal('scrollTo', () => {})
Element.prototype.scrollTo ??= function () {} as Element['scrollTo']
