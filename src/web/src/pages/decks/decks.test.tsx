import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { admin, fakeApi, member, renderApp } from '@/test/utils'
import type { Deck, DeckConfig } from './decks-api'

const deck: Deck = {
  id: 'd1', title: 'Our quarter', templateId: null, version: 1, createdAt: '', updatedAt: '2026-10-22T08:00:00Z',
  slides: [
    { layout: 'title', title: 'Our quarter', subtitle: 'What went well', notes: 'Welcome everyone.' },
    { layout: 'bullets', title: 'Highlights', bullets: ['Sales grew', 'Two new customers'], picture: { prompt: 'A rising arrow over a city' } },
    { layout: 'chart', title: 'Sales by region', chart: { type: 'bar', categories: ['North', 'South'], series: [{ name: 'Sales', values: [120, 80] }] } },
  ],
}

const config: DeckConfig = { pictures: true, templates: [{ id: 't1', name: 'Our brand', isDefault: true, theme: { colors: { accent1: '#C00000' } } }], sources: ['Shop'] }

afterEach(() => vi.restoreAllMocks())

describe('decks', () => {
  it('a deck is made from a few words, in the default template', async () => {
    const calls = fakeApi(member, {
      'GET /api/decks': () => ({ json: [] }),
      'GET /api/decks/config': () => ({ json: config }),
      'POST /api/decks': () => ({ status: 201, json: deck }),
      'GET /api/decks/d1': () => ({ json: deck }),
    })
    const { router } = renderApp('/decks')
    expect(await screen.findByText('No decks yet')).toBeInTheDocument()
    // Only those who may change the branding see the templates.
    expect(screen.queryByRole('button', { name: /Templates/ })).not.toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('Make a deck about…'), 'Our quarter for the board')
    expect(screen.getByRole('combobox', { name: 'Look' })).toHaveTextContent('Our brand')
    await userEvent.click(screen.getByRole('button', { name: /Make it/ }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/decks/d1'))
    expect(calls.find((c) => c.path === '/api/decks' && c.method === 'POST')?.body).toEqual({ about: 'Our quarter for the board' })
  })

  it('its pictures are made one by one, it changes by asking about the slide in view, and by hand', async () => {
    let current = deck
    const calls = fakeApi(member, {
      'GET /api/decks/config': () => ({ json: config }),
      'GET /api/decks/d1': () => ({ json: current }),
      'GET /api/decks': () => ({ json: [] }),
      'GET /api/decks/d1/versions': () => ({ json: [] }),
      'POST /api/decks/d1/slides/1/picture': () => {
        current = { ...current, slides: current.slides.map((s, i) => (i === 1 ? { ...s, picture: { prompt: s.picture!.prompt, fileId: 'f1' } } : s)) }
        return { json: current }
      },
      'POST /api/decks/d1/ask': () => {
        current = { ...current, version: 2, slides: [...current.slides, { layout: 'bullets', title: 'Costs', bullets: ['Rent'] }] }
        return { json: current }
      },
      'PUT /api/decks/d1': (body) => {
        current = { ...current, version: 3, slides: (body as { slides: Deck['slides'] }).slides }
        return { json: current }
      },
    })
    renderApp('/decks/d1')
    const slides = await screen.findByRole('list', { name: 'Slides' })
    expect(within(slides).getAllByRole('listitem')).toHaveLength(3)
    // The picture, made while the deck is open.
    await waitFor(() => expect(calls.some((c) => c.path === '/api/decks/d1/slides/1/picture')).toBe(true))
    await userEvent.click(within(slides).getByRole('button', { name: 'Slide 2: Highlights' }))
    const shown = screen.getByRole('region', { name: 'Slide 2' })
    expect(await within(shown).findByRole('img', { name: 'A rising arrow over a city' })).toHaveAttribute('src', '/api/chat/attachments/f1/content')
    expect(screen.getByRole('link', { name: /PowerPoint/ })).toHaveAttribute('href', '/api/decks/d1/pptx')

    await userEvent.type(screen.getByLabelText('Change it by asking'), 'Add a slide on costs{Enter}')
    await waitFor(() => expect(within(screen.getByRole('list', { name: 'Slides' })).getAllByRole('listitem')).toHaveLength(4))
    expect(calls.find((c) => c.path === '/api/decks/d1/ask')?.body).toEqual({ change: 'Add a slide on costs', slide: 1 })

    // By hand: the title of the slide in view.
    await userEvent.click(screen.getByRole('button', { name: /Change by hand/ }))
    const form = await screen.findByRole('dialog', { name: 'Slide 2' })
    await userEvent.clear(within(form).getByLabelText('Title'))
    await userEvent.type(within(form).getByLabelText('Title'), 'Our highlights')
    await userEvent.click(within(form).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls.some((c) => c.method === 'PUT')).toBe(true))
    const sent = calls.find((c) => c.method === 'PUT')!.body as { slides: Deck['slides']; note: string }
    expect(sent.note).toBe('Slide 2 changed by hand')
    expect(sent.slides[1]).toMatchObject({ layout: 'bullets', title: 'Our highlights', bullets: ['Sales grew', 'Two new customers'], picture: { prompt: 'A rising arrow over a city', fileId: 'f1' } })
  })

  it('presents a slide at a time, the arrows moving and Escape ending it', async () => {
    fakeApi(member, { 'GET /api/decks/config': () => ({ json: { ...config, pictures: false } }), 'GET /api/decks/d1': () => ({ json: deck }) })
    renderApp('/decks/d1')
    await userEvent.click(await screen.findByRole('button', { name: /Present/ }))
    expect(await screen.findByRole('dialog', { name: 'Presenting, slide 1 of 3' })).toBeInTheDocument()
    fireEvent.keyDown(window, { key: 'ArrowRight' })
    expect(await screen.findByRole('dialog', { name: 'Presenting, slide 2 of 3' })).toHaveTextContent('Highlights')
    fireEvent.keyDown(window, { key: 'Escape' })
    await waitFor(() => expect(screen.queryByRole('dialog', { name: /Presenting/ })).not.toBeInTheDocument())
    // It ends on the slide it was at.
    expect(screen.getByRole('region', { name: 'Slide 2' })).toBeInTheDocument()
  })

  it('an admin uploads the company template', async () => {
    let uploaded = false
    fakeApi(admin, {
      'GET /api/decks': () => ({ json: [] }),
      'GET /api/decks/config': () => ({ json: { ...config, templates: [] } }),
      'GET /api/admin/deck-templates': () => ({ json: uploaded ? config.templates : [] }),
      'POST /api/admin/deck-templates': () => {
        uploaded = true
        return { status: 201, json: config.templates[0] }
      },
    })
    renderApp('/decks')
    await userEvent.click(await screen.findByRole('button', { name: /Templates/ }))
    const dialog = await screen.findByRole('dialog', { name: 'Templates' })
    expect(within(dialog).getByText('None yet: decks have the built-in look.')).toBeInTheDocument()
    await userEvent.upload(within(dialog).getByLabelText(/Upload a template/), new File(['pk'], 'brand.pptx', { type: 'application/vnd.openxmlformats-officedocument.presentationml.presentation' }))
    expect(await within(dialog).findByText('Our brand')).toBeInTheDocument()
    expect(within(dialog).getByText('Default')).toBeInTheDocument()
  })
})
