import { fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Splitter } from './splitter'

afterEach(() => {
  document.body.style.cursor = ''
  document.body.style.userSelect = ''
})

function renderSplitter() {
  const onChange = vi.fn()
  const view = render(<Splitter label="Resize the terminal" orientation="horizontal" value={200} min={100} max={400} grow={-1} onChange={onChange} />)
  const edge = screen.getByRole('separator', { name: 'Resize the terminal' })
  fireEvent.pointerDown(edge, { button: 0, pointerId: 1, clientY: 500 })
  return { ...view, edge, onChange }
}

describe('Splitter', () => {
  it('stops the page selecting text while dragged, and lets it again when let go', () => {
    const { edge, onChange } = renderSplitter()
    expect(document.body.style.userSelect).toBe('none')
    fireEvent.pointerMove(edge, { pointerId: 1, clientY: 460 })
    expect(onChange).toHaveBeenLastCalledWith(240)
    fireEvent.pointerUp(edge, { pointerId: 1 })
    expect(document.body.style.userSelect).toBe('')
    expect(document.body.style.cursor).toBe('')
  })

  it('lets the page select text again when the drag is cancelled or the pointer taken away', () => {
    const { edge, onChange } = renderSplitter()
    fireEvent.pointerCancel(edge, { pointerId: 1 })
    expect(document.body.style.userSelect).toBe('')
    fireEvent.pointerMove(edge, { pointerId: 1, clientY: 400 })
    expect(onChange).not.toHaveBeenCalled()

    fireEvent.pointerDown(edge, { button: 0, pointerId: 2, clientY: 500 })
    expect(document.body.style.userSelect).toBe('none')
    fireEvent(edge, new Event('lostpointercapture', { bubbles: true }))
    expect(document.body.style.userSelect).toBe('')
  })

  it('lets the page select text again when it goes away mid-drag (the panel hidden by its key)', () => {
    const { unmount } = renderSplitter()
    expect(document.body.style.cursor).toBe('row-resize')
    unmount()
    expect(document.body.style.userSelect).toBe('')
    expect(document.body.style.cursor).toBe('')
  })
})
