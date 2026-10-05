import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { Providers } from '@/app/providers'
import '@/styles/index.css'
import { App } from './app'

// Code Arena's web interface (code-arena web): no router, no sign-in, no service
// worker; the chat's providers, styles and components around Code Arena's API.
createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Providers>
      <App />
    </Providers>
  </StrictMode>,
)
