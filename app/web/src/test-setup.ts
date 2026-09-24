import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'

afterEach(cleanup)

// As current Chrome: scroll methods return a Promise. jsdom has none, which hid
// an effect that returned it (React then crashed calling it as a cleanup).
Element.prototype.scrollIntoView = function () {
  return Promise.resolve() as unknown as void
}
