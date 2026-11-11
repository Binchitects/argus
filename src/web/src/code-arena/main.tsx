import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { Providers } from '@/app/providers'
import { WrapCode } from '@/pages/chat/code-block'
import '@/styles/index.css'
import { App } from './app'

// Code Arena's web interface (code-arena web): the IDE around this folder's
// agent. No router, no sign-in, no service worker; the chat's providers, styles
// and components around Code Arena's API.
// The page never scrolls: the workbench fills the window and each part scrolls itself. Something that still scrolls it (a
// menu Monaco puts under <body>, a focus at its edge) is put back at once.
document.documentElement.classList.add('overflow-hidden')
document.body.classList.add('overflow-hidden')
window.addEventListener('scroll', () => {
  if (window.scrollX !== 0 || window.scrollY !== 0) window.scrollTo(0, 0)
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Providers>
      {/* Nothing but the editor scrolls sideways: a code block in the chat wraps its lines (its button unwraps them). */}
      <WrapCode value>
        <App />
      </WrapCode>
    </Providers>
  </StrictMode>,
)
