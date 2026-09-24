import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach, vi } from 'vitest'

afterEach(() => {
  cleanup()
  try {
    localStorage.clear()
  } catch {
    // no storage in this environment
  }
  document.documentElement.className = ''
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
