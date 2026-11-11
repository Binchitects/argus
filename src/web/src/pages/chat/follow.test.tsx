import { fireEvent, render } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Thinking } from './parts'

/** The box's sizes as a browser would lay them out (jsdom lays out nothing). */
function sized(el: HTMLElement, height: number) {
  Object.defineProperty(el, 'scrollHeight', { configurable: true, get: () => height })
  Object.defineProperty(el, 'clientHeight', { configurable: true, get: () => 100 })
}

describe('a live thinking box', () => {
  it('follows its newest words while left at the bottom, stays where the person scrolled up to read, and follows again at the end', () => {
    const shown = render(<Thinking text="first" live ms={null} since={null} />)
    const box = shown.container.querySelector<HTMLElement>('.bidi-lines')!
    sized(box, 500)
    shown.rerender(<Thinking text="first second" live ms={null} since={null} />)
    expect(box.scrollTop).toBe(500)

    // Scrolled up to read: more words come, and it stays.
    box.scrollTop = 120
    fireEvent.scroll(box)
    sized(box, 800)
    shown.rerender(<Thinking text="first second third" live ms={null} since={null} />)
    expect(box.scrollTop).toBe(120)

    // Back at the end: it follows again.
    box.scrollTop = 700
    fireEvent.scroll(box)
    sized(box, 900)
    shown.rerender(<Thinking text="first second third fourth" live ms={null} since={null} />)
    expect(box.scrollTop).toBe(900)
  })
})
