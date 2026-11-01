import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useEffect } from 'react'
import { describe, expect, it } from 'vitest'
import { ThemeProvider, useTheme } from './theme'

function Probe() {
  const { colorTheme, shownColorTheme, setColorTheme, showColorTheme } = useTheme()
  return (
    <div>
      <p>
        chosen {colorTheme}, shown {shownColorTheme}
      </p>
      <button onClick={() => setColorTheme('copper')}>Copper</button>
      <button onClick={() => setColorTheme('default')}>Arena</button>
      <button onClick={() => showColorTheme('ocean')}>Show Ocean</button>
      <button onClick={() => showColorTheme(null)}>Show mine</button>
    </div>
  )
}

const html = document.documentElement

describe('the colour theme', () => {
  it('starts as this browser kept it, as theme-init.js painted it', () => {
    localStorage.setItem('color-theme', 'sakura')
    render(<ThemeProvider><Probe /></ThemeProvider>)
    expect(screen.getByText('chosen sakura, shown sakura')).toBeInTheDocument()
    expect(html.dataset.colorTheme).toBe('sakura')
  })

  it('is Arena’s own for anything it does not know', () => {
    localStorage.setItem('color-theme', 'neon')
    render(<ThemeProvider><Probe /></ThemeProvider>)
    expect(screen.getByText('chosen default, shown default')).toBeInTheDocument()
    expect(html.dataset.colorTheme).toBeUndefined()
  })

  it('a choice is kept; the default takes the attribute away', async () => {
    render(<ThemeProvider><Probe /></ThemeProvider>)
    await userEvent.click(screen.getByRole('button', { name: 'Copper' }))
    expect(html.dataset.colorTheme).toBe('copper')
    expect(localStorage.getItem('color-theme')).toBe('copper')
    await userEvent.click(screen.getByRole('button', { name: 'Arena' }))
    expect(html.dataset.colorTheme).toBeUndefined()
    expect(localStorage.getItem('color-theme')).toBe('default')
  })

  it('one shown over the person’s (Code Arena’s IDE) is not kept, and theirs comes back', async () => {
    localStorage.setItem('color-theme', 'forest')
    render(<ThemeProvider><Probe /></ThemeProvider>)
    await userEvent.click(screen.getByRole('button', { name: 'Show Ocean' }))
    expect(screen.getByText('chosen forest, shown ocean')).toBeInTheDocument()
    expect(html.dataset.colorTheme).toBe('ocean')
    expect(localStorage.getItem('color-theme')).toBe('forest')
    await userEvent.click(screen.getByRole('button', { name: 'Show mine' }))
    expect(html.dataset.colorTheme).toBe('forest')
  })

  it('is on the page before the effects of what is inside it run (a chart reads the colours then)', () => {
    const seen: (string | undefined)[] = []
    function Reader() {
      const { shownColorTheme } = useTheme()
      useEffect(() => {
        seen.push(html.dataset.colorTheme)
      }, [shownColorTheme])
      return null
    }
    function Chooser() {
      const { setColorTheme } = useTheme()
      return <button onClick={() => setColorTheme('midnight')}>Midnight</button>
    }
    render(<ThemeProvider><Reader /><Chooser /></ThemeProvider>)
    act(() => screen.getByRole('button', { name: 'Midnight' }).click())
    expect(seen).toEqual([undefined, 'midnight'])
  })
})
