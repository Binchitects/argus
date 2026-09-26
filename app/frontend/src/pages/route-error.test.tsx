import { render, screen } from '@testing-library/react'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it } from 'vitest'
import { RouteErrorPage } from './route-error'

function renderError(error: Error) {
  const router = createMemoryRouter([
    {
      path: '/',
      loader: () => {
        throw error
      },
      errorElement: <RouteErrorPage />,
      element: null,
    },
  ])
  render(<RouterProvider router={router} />)
}

describe('route error page', () => {
  it('a page whose code or styles did not arrive says to reload', async () => {
    renderError(new Error('Unable to preload CSS for /assets/chat-page-C_OyUF2y.css'))
    expect(await screen.findByRole('heading', { level: 1, name: 'This page did not load' })).toBeInTheDocument()
    expect(screen.getByText(/The connection dropped/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reload the page' })).toBeInTheDocument()
  })

  it('any other error says so', async () => {
    renderError(new Error('boom'))
    expect(await screen.findByText(/This page hit an error/)).toBeInTheDocument()
  })
})
