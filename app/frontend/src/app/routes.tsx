import type { ComponentType } from 'react'
import type { RouteObject } from 'react-router'
import { HomePage } from '@/pages/home'
import { NotFoundPage } from '@/pages/not-found'
import { NotYetPage } from '@/pages/not-yet'
import { RouteErrorPage } from '@/pages/route-error'
import { navigation } from './nav'
import { RequireAdmin } from './require-admin'
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
          { path: 'usage', ...lazy(() => import('@/pages/usage').then((m) => ({ Component: m.UsagePage }))) },
          {
            path: 'admin',
            element: <RequireAdmin />,
            children: [
              { index: true, ...lazy(() => import('@/pages/admin/overview').then((m) => ({ Component: m.OverviewPage }))) },
              { path: 'people', ...lazy(() => import('@/pages/admin/people').then((m) => ({ Component: m.PeoplePage }))) },
              { path: 'people/:id', ...lazy(() => import('@/pages/admin/person').then((m) => ({ Component: m.PersonPage }))) },
              { path: 'sign-in', ...lazy(() => import('@/pages/admin/sign-in').then((m) => ({ Component: m.SignInPage }))) },
              { path: 'model', ...lazy(() => import('@/pages/admin/model').then((m) => ({ Component: m.ModelPage }))) },
              { path: 'settings', ...lazy(() => import('@/pages/admin/settings').then((m) => ({ Component: m.SettingsPage }))) },
              { path: 'audit', ...lazy(() => import('@/pages/admin/audit').then((m) => ({ Component: m.AuditPage }))) },
              { path: 'indexing', ...lazy(() => import('@/pages/admin/argus').then((m) => ({ Component: m.IndexingPage }))) },
              { path: 'packs', ...lazy(() => import('@/pages/admin/argus').then((m) => ({ Component: m.PacksPage }))) },
              { path: 'explore', ...lazy(() => import('@/pages/admin/argus').then((m) => ({ Component: m.ExplorePage }))) },
              { path: 'monitoring', ...lazy(() => import('@/pages/admin/monitoring').then((m) => ({ Component: m.MonitoringPage }))) },
              { path: '*', element: <NotFoundPage /> },
            ],
          },
          ...notYet,
          { path: '*', element: <NotFoundPage /> },
        ],
      },
    ],
  },
]
