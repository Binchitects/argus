import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { TooltipProvider } from '@/components/ui/tooltip'
import { ChatTimeButton } from './header'

describe('ChatTimeButton', () => {
  it("shows the chat's time and, opened, the model's, the tools' and the person's with their shares", async () => {
    render(
      <TooltipProvider>
        <ChatTimeButton time={{ modelMs: 60_000, toolMs: 30_000, humanMs: 30_000 }} />
      </TooltipProvider>,
    )
    const button = screen.getByRole('button', { name: 'Time: 2 min 0 s' })
    await userEvent.click(button)
    const card = await screen.findByRole('dialog', { name: "This chat's time" })
    expect(card).toHaveTextContent('The model1 min 0 s50%')
    expect(card).toHaveTextContent('Tools30 s25%')
    expect(card).toHaveTextContent('You30 s25%')
  })
})
