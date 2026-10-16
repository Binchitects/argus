import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { rankFiles } from './editor-state'
import { inputBox, quickPick } from './quick-pick'
import { QuickPickHost } from './quick-pick-host'

describe('the quick pick', () => {
  it("takes what is typed in an input box, though the value it had holds it, and an empty answer", async () => {
    render(<QuickPickHost />)
    // A breakpoint's condition made narrower: "n > 10" is in "n > 100".
    const narrower = inputBox({ title: 'Breakpoint condition', placeholder: 'Breaks when the expression is true (empty: always)', value: 'n > 100' })
    const box = within(await screen.findByRole('dialog', { name: 'Breakpoint condition' })).getByRole('combobox')
    expect(box).toHaveValue('n > 100')
    await userEvent.type(box, '{Backspace}{Enter}')
    expect(await narrower).toBe('n > 10')
    // Emptied: no condition.
    const emptied = inputBox({ title: 'Breakpoint condition', placeholder: 'Breaks when the expression is true (empty: always)', value: 'n > 100' })
    await userEvent.clear(within(await screen.findByRole('dialog', { name: 'Breakpoint condition' })).getByRole('combobox'))
    await userEvent.keyboard('{Enter}')
    expect(await emptied).toBe('')
    // A default taken as it is.
    const kept = inputBox({ title: 'Port', placeholder: 'The port', value: '10' })
    await screen.findByRole('dialog', { name: 'Port' })
    await userEvent.keyboard('{Enter}')
    expect(await kept).toBe('10')
  })

  it('draws a long list ranked and cut as it is typed, never all of it', async () => {
    render(<QuickPickHost />)
    const files = Array.from({ length: 20_000 }, (_, i) => `src/module${i}/file${i}.ts`)
    const picked = quickPick({ title: 'Compare with', placeholder: 'Select a file', items: [], match: (typed) => rankFiles(files, typed).map((p) => ({ id: p, label: p.slice(p.lastIndexOf('/') + 1), description: p })) })
    const box = within(await screen.findByRole('dialog', { name: 'Compare with' })).getByRole('combobox')
    expect(screen.getAllByRole('option').length).toBeLessThanOrEqual(50)
    await userEvent.type(box, 'file12345')
    await waitFor(() => expect(screen.getAllByRole('option')[0]).toHaveTextContent('file12345.ts'))
    await userEvent.keyboard('{Enter}')
    expect((await picked)?.id).toBe('src/module12345/file12345.ts')
  })

  it('draws at most 200 of the items a pick narrows, and says how many more there are', async () => {
    render(<QuickPickHost />)
    const picked = quickPick({ title: 'Pick one', placeholder: 'Select', items: Array.from({ length: 1000 }, (_, i) => ({ id: String(i), label: `item ${i}` })) })
    await screen.findByRole('dialog', { name: 'Pick one' })
    expect(screen.getAllByRole('option')).toHaveLength(200)
    expect(screen.getByText('800 more: type to narrow the list.')).toBeInTheDocument()
    await userEvent.keyboard('{Escape}')
    expect(await picked).toBeUndefined()
  })
})
