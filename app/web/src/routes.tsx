import type { RouteObject } from 'react-router'
import { areas } from './areas'
import { Shell } from './layout/Shell'
import { AreaPage } from './pages/AreaPage'
import { Home } from './pages/Home'
import { NotFound } from './pages/NotFound'

export const routes: RouteObject[] = [
  {
    element: <Shell />,
    children: [
      { index: true, element: <Home /> },
      ...areas.map((a) => ({ path: `${a.path}/*`, element: <AreaPage area={a} /> })),
      { path: '*', element: <NotFound /> },
    ],
  },
]
