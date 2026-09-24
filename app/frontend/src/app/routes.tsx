import type { ComponentType } from 'react'
import type { RouteObject } from 'react-router'
import { HomePage } from '@/pages/home'
import { NotFoundPage } from '@/pages/not-found'
import { NotYetPage } from '@/pages/not-yet'
import { RouteErrorPage } from '@/pages/route-error'
import { navigation } from './nav'
import { Shell } from './shell'

const lazy = (load: () => Promise<{ Component: ComponentType }>) => ({ lazy: load })

/** Pages this web does not have yet show where they are meanwhile. */
const notYet: RouteObject[] = navigation
  .flatMap((s) => s.items)
  .filter((i) => !i.ready)
  .map((i) => ({ path: i.path.slice(1) + '/*', element: <NotYetPage /> }))

export const routes: RouteObject[] = [
  { path: '/login', ...lazy(() => import('@/pages/login').then((m) => ({ Component: m.LoginPage }))), errorElement: <RouteErrorPage /> },
  {
    path: '/',
    element: <Shell />,
    errorElement: <RouteErrorPage />,
    children: [
      {
        errorElement: <RouteErrorPage />,
        children: [
          { index: true, element: <HomePage /> },
          { path: 'account', ...lazy(() => import('@/pages/account').then((m) => ({ Component: m.AccountPage }))) },
          { path: 'design', ...lazy(() => import('@/pages/design').then((m) => ({ Component: m.DesignPage }))) },
          ...notYet,
          { path: '*', element: <NotFoundPage /> },
        ],
      },
    ],
  },
]
