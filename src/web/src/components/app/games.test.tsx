import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { makeQueryClient, Providers } from '@/app/providers'
import { ToolCard } from '@/pages/chat/parts'
import { admin, fakeApi, renderApp } from '@/test/utils'

const making = (name: string) => (
  <Providers client={makeQueryClient()}>
    <ToolCard call={{ id: 'c1', function: { name, arguments: '{"prompt":"a lighthouse"}' } }} live progress={{ since: Date.now() - 12_000 }} />
  </Providers>
)

describe('easter eggs', () => {
  it('five quick clicks on a picture being made open Snake, which plays and keeps its best score', async () => {
    render(making('make_image'))
    const card = screen.getByRole('button', { name: /Drawing the picture/ })
    for (let i = 0; i < 4; i++) await userEvent.click(card)
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    await userEvent.click(card)
    const game = await screen.findByRole('dialog', { name: 'Snake' })
    expect(game).toHaveTextContent('Best in this browser: 0.')
    await userEvent.click(screen.getByRole('button', { name: 'Start' }))
    expect(screen.getByRole('application', { name: 'Snake' })).toBeInTheDocument()
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('five quick clicks on a video being made open the star game', async () => {
    render(making('make_video'))
    const card = screen.getByRole('button', { name: /Making the video/ })
    for (let i = 0; i < 5; i++) await userEvent.click(card)
    expect(await screen.findByRole('dialog', { name: 'Catch the stars' })).toBeInTheDocument()
  })

  it('the old cheat code, typed anywhere but a text box, throws confetti and says so', async () => {
    fakeApi(admin, { 'GET /api/chat/conversations': () => ({ json: [] }) })
    renderApp('/settings')
    await screen.findAllByRole('navigation')
    for (const key of ['ArrowUp', 'ArrowUp', 'ArrowDown', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'ArrowLeft', 'ArrowRight', 'b', 'a']) fireEvent.keyDown(window, { key })
    expect(await screen.findByText('Cheat code accepted')).toBeInTheDocument()
  })

  it('five quick clicks on a page that is not there open Snake', async () => {
    fakeApi(admin)
    renderApp('/no-such-page')
    const words = await screen.findByRole('button', { name: 'The address may be mistyped, or the page has moved.' })
    for (let i = 0; i < 5; i++) await userEvent.click(words)
    expect(await screen.findByRole('dialog', { name: 'Snake' })).toBeInTheDocument()
  })
})
