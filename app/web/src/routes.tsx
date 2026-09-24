import type { RouteObject } from 'react-router'
import { areas } from './areas'
import { Shell } from './layout/Shell'
import { Account } from './pages/Account'
import { AdminLayout } from './pages/admin/AdminLayout'
import { Audit } from './pages/admin/Audit'
import { People } from './pages/admin/People'
import { PersonPage } from './pages/admin/PersonPage'
import { SignInSettings } from './pages/admin/SignInSettings'
import { AreaPage } from './pages/AreaPage'
import { Home } from './pages/Home'
import { Login } from './pages/Login'
import { NotFound } from './pages/NotFound'

export const routes: RouteObject[] = [
  { path: '/login', element: <Login /> },
  {
    element: <Shell />,
    children: [
      { index: true, element: <Home /> },
      { path: '/account', element: <Account /> },
      {
        path: '/admin',
        element: <AdminLayout />,
        children: [
          { index: true, element: <People /> },
          { path: 'people/:id', element: <PersonPage /> },
          { path: 'audit', element: <Audit /> },
          { path: 'sign-in', element: <SignInSettings /> },
        ],
      },
      ...areas.filter((a) => !a.native).map((a) => ({ path: `${a.path}/*`, element: <AreaPage area={a} /> })),
      { path: '*', element: <NotFound /> },
    ],
  },
]
