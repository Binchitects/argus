import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { createBrowserRouter } from 'react-router'
import { RouterProvider } from 'react-router/dom'
import { Providers } from './app/providers'
import { routes } from './app/routes'
import { keepHandoff } from './lib/handoff'
import { registerServiceWorker } from './lib/pwa'
import './styles/index.css'

// A page or selection handed over by the browser extension (/ask#…), kept before anything routes.
keepHandoff()
const router = createBrowserRouter(routes)
registerServiceWorker()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Providers>
      <RouterProvider router={router} />
    </Providers>
  </StrictMode>,
)
