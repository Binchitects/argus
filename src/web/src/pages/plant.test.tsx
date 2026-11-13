import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, member, renderApp } from '@/test/utils'
import { areaTree, limits, type PlantArea, type PlantPoint } from './plant-api'

const kinds = { areas: ['area', 'line', 'room', 'floor'], assets: ['machine', 'sensor', 'camera'], types: ['number', 'boolean', 'text', 'state'], sources: ['manual', 'mqtt', 'opcua', 'modbus'] }
const site = { id: 's1', name: 'Plant north', description: null, timeZone: 'Europe/Berlin', groupIds: [], createdAt: '', updatedAt: '', assets: 2, points: 1 }
const area = (id: string, name: string, parentId: string | null = null, position = 0): PlantArea => ({ id, parentId, name, kind: 'line', position })
const asset = (id: string, name: string, areaId: string | null) => ({ id, siteId: 's1', areaId, name, kind: 'machine', vendor: null, model: null, serial: null, description: null, tags: [], x: null, y: null, points: 1, createdAt: '', updatedAt: '' })
const point: PlantPoint = { id: 'p1', assetId: 'a1', name: 'Outlet temperature', description: null, dataType: 'number', unit: '°C', source: 'opcua', address: 'ns=2;s=P1.T', lowLow: null, low: 10, high: 80, highHigh: null, deadband: null, states: null }

describe('plant', () => {
  it('areas make a tree: a parent before its children, an orphan at the top, no loop followed', () => {
    const tree = areaTree([area('b', 'Bay', 'a'), area('a', 'Hall'), area('c', 'Lost', 'gone'), area('x', 'Loop', 'y'), area('y', 'Pool', 'x')])
    expect(tree.map((t) => `${t.depth}:${t.area.name}`)).toEqual(['0:Hall', '1:Bay', '0:Lost'])
    expect(limits(point)).toBe('low 10 · high 80 °C')
    expect(limits({ ...point, low: null, high: null })).toBe('')
  })

  it('a manager adds a site and is taken to it', async () => {
    let sites: (typeof site)[] = []
    const calls = fakeApi(member, {
      'GET /api/plant': () => ({ json: { sites, mayManage: true, kinds } }),
      'POST /api/plant/sites': () => {
        sites = [site]
        return { status: 201, json: { id: 's1' } }
      },
      'GET /api/plant/sites/s1': () => ({ json: { site, areas: [], assets: [] } }),
    })
    renderApp('/plant')
    expect(await screen.findByText('No sites yet')).toBeInTheDocument()
    await userEvent.click(screen.getAllByRole('button', { name: /New site/ })[0])
    const dialog = await screen.findByRole('dialog', { name: 'New site' })
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Plant north')
    await userEvent.clear(within(dialog).getByLabelText('Time zone'))
    await userEvent.type(within(dialog).getByLabelText('Time zone'), 'Europe/Berlin')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ name: 'Plant north', description: null, timeZone: 'Europe/Berlin' }))
    expect(await screen.findByText('Nothing in this site yet')).toBeInTheDocument()
  })

  it('a site shows its areas with their assets; an asset opens its points; one who may not manage sees no buttons to change it', async () => {
    fakeApi(member, {
      'GET /api/plant': () => ({ json: { sites: [site], mayManage: false, kinds } }),
      'GET /api/plant/sites/s1': () => ({ json: { site, areas: [area('l1', 'Line 1')], assets: [asset('a1', 'Pump 1', 'l1'), asset('a2', 'Spare pump', null)] } }),
      'GET /api/plant/assets/a1': () => ({ json: { asset: asset('a1', 'Pump 1', 'l1'), points: [point] } }),
    })
    renderApp('/plant/s1')
    const nav = await screen.findByRole('navigation', { name: 'Areas and assets' })
    expect(within(nav).getByText('Line 1')).toBeInTheDocument()
    expect(within(nav).getByText('Outside any area')).toBeInTheDocument()
    await userEvent.click(within(nav).getByRole('button', { name: /Pump 1/ }))
    expect(await screen.findByText('Outlet temperature')).toBeInTheDocument()
    expect(screen.getByText('low 10 · high 80 °C')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Import a sheet/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Delete Pump 1' })).not.toBeInTheDocument()
  })

  it('a manager imports a sheet, and adds a point with its limits as numbers', async () => {
    const calls = fakeApi(member, {
      'GET /api/plant': () => ({ json: { sites: [site], mayManage: true, kinds } }),
      'GET /api/plant/sites/s1': () => ({ json: { site, areas: [], assets: [asset('a1', 'Pump 1', null)] } }),
      'GET /api/plant/assets/a1': () => ({ json: { asset: asset('a1', 'Pump 1', null), points: [] } }),
      'POST /api/plant/sites/s1/import': () => ({ json: { areas: 1, assets: 1, points: 1, sheetLines: 1 } }),
      'POST /api/plant/assets/a1/points': () => ({ status: 201, json: { id: 'p2' } }),
    })
    renderApp('/plant/s1')
    await userEvent.click(await screen.findByRole('button', { name: /Import a sheet/ }))
    const sheet = await screen.findByRole('dialog', { name: 'Import points from a sheet' })
    await userEvent.type(within(sheet).getByLabelText(/The sheet/), 'asset,point{Enter}Pump 2,Speed')
    await userEvent.click(within(sheet).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.find((c) => c.path.endsWith('/import'))?.body).toEqual({ csv: 'asset,point\nPump 2,Speed' }))

    await userEvent.click(await screen.findByRole('button', { name: /Pump 1/ }))
    await userEvent.click(await screen.findByRole('button', { name: /Point/ }))
    const form = await screen.findByRole('dialog', { name: 'New point' })
    await userEvent.type(within(form).getByLabelText('Name'), 'Pressure')
    await userEvent.type(within(form).getByLabelText('Low (optional)'), '1.5')
    await userEvent.type(within(form).getByLabelText('High (optional)'), '6')
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }))
    await waitFor(() =>
      expect(calls.find((c) => c.path.endsWith('/points'))?.body).toEqual({ name: 'Pressure', dataType: 'number', unit: null, source: 'manual', address: null, low: 1.5, high: 6 }),
    )
  })
})
