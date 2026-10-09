import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'

// The help's code does not arrive (a new version was deployed, or the connection dropped)
// until `arrives` says so.
const load = vi.hoisted(() => ({ arrives: false, tries: 0 }))
vi.mock('./help-body', async (original) => {
  load.tries++
  if (!load.arrives) throw new TypeError('Failed to fetch dynamically imported module: /assets/help-body-old.js')
  return original()
})

describe('the help panel, when its code does not load', () => {
  it('says so in the panel, keeps the page, and loads on Try again', async () => {
    fakeApi(member)
    renderApp('/help')
    await userEvent.type(await screen.findByRole('searchbox', { name: 'Search the manual' }), 'draft')
    await userEvent.click(screen.getByRole('button', { name: 'Help for this page' }))
    const panel = await screen.findByRole('dialog')
    expect(await within(panel).findByRole('heading', { name: 'The help did not load' }, { timeout: 5000 })).toBeInTheDocument()
    // Tried twice: once, and once more half a second later.
    expect(load.tries).toBe(2)
    expect(within(panel).getByRole('button', { name: 'Reload the page' })).toBeInTheDocument()
    // The page behind it is still there, as it was (hidden from a screen reader while the panel is over it).
    expect(document.querySelector('aside[aria-label="Sidebar"]')).toBeInTheDocument()
    expect(screen.getByDisplayValue('draft')).toHaveAccessibleName('Search the manual')
    load.arrives = true
    await userEvent.click(within(panel).getByRole('button', { name: 'Try again' }))
    expect(await within(panel).findByRole('heading', { name: 'Manual' })).toBeInTheDocument()
  })
})
