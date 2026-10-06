import { act, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fakeApi, renderApp } from '@/test/utils'
import { CYCLE, eyeCap, FADE } from './eyes'

const assign = vi.fn()
beforeEach(() => {
  assign.mockReset()
  vi.stubGlobal('location', { ...window.location, assign, protocol: 'https:', host: 'llm.test', origin: 'https://llm.test' })
})

describe('sign-in', () => {
  it('signs in and goes where the person was headed', async () => {
    const calls = fakeApi(null, { 'POST /api/auth/login': () => ({ json: { status: 'ok', redirect: '/usage' } }) })
    renderApp('/login?rd=%2Fusage')
    await userEvent.type(await screen.findByLabelText('Username or email'), 'ada')
    await userEvent.type(screen.getByLabelText('Password'), 'correct horse')
    await userEvent.click(screen.getByRole('checkbox', { name: /Keep me signed in/ }))
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
    await waitFor(() => expect(assign).toHaveBeenCalledWith('/usage'))
    expect(calls.find((c) => c.path === '/api/auth/login')?.body).toEqual({ userName: 'ada', password: 'correct horse', remember: true, redirect: '/usage' })
  })

  it('says what is missing before sending anything', async () => {
    const calls = fakeApi(null)
    renderApp('/login')
    await userEvent.click(await screen.findByRole('button', { name: 'Sign in' }))
    expect(await screen.findByText('Enter your username or email.')).toBeInTheDocument()
    expect(screen.getByLabelText('Username or email')).toHaveAttribute('aria-invalid', 'true')
    expect(calls.some((c) => c.path === '/api/auth/login')).toBe(false)
  })

  it('shows the reason a sign-in failed', async () => {
    fakeApi(null, { 'POST /api/auth/login': () => ({ status: 400, json: { status: 'invalid', error: 'Wrong username or password.' } }) })
    renderApp('/login')
    await userEvent.type(await screen.findByLabelText('Username or email'), 'ada')
    await userEvent.type(screen.getByLabelText('Password'), 'nope')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Wrong username or password.')
  })

  it('asks for the second factor, and takes a recovery code', async () => {
    const calls = fakeApi(null, {
      'POST /api/auth/login': () => ({ json: { status: '2fa' } }),
      'POST /api/auth/login/2fa': () => ({ json: { status: 'ok', redirect: '/' } }),
    })
    renderApp('/login')
    await userEvent.type(await screen.findByLabelText('Username or email'), 'ada')
    await userEvent.type(screen.getByLabelText('Password'), 'pw')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(await screen.findByRole('heading', { name: 'Two-factor sign-in' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: /Use a recovery code/ }))
    await userEvent.type(screen.getByLabelText('Recovery code'), 'abcd-1234')
    await userEvent.click(screen.getByRole('button', { name: 'Verify' }))
    await waitFor(() => expect(assign).toHaveBeenCalledWith('/'))
    expect(calls.find((c) => c.path === '/api/auth/login/2fa')?.body).toMatchObject({ code: 'abcd-1234', recovery: true })
  })
})

describe('company sign-in', () => {
  it('offers the company button when it is set up, keeping where the person was headed', async () => {
    fakeApi(null, { 'GET /api/auth/company': () => ({ json: { label: 'Okta' } }) })
    renderApp('/login?rd=%2Fusage')
    const button = await screen.findByRole('link', { name: 'Sign in with Okta' })
    expect(button).toHaveAttribute('href', '/api/auth/company/start?rd=%2Fusage')
    await userEvent.click(screen.getByRole('checkbox', { name: /Keep me signed in/ }))
    expect(button).toHaveAttribute('href', '/api/auth/company/start?rd=%2Fusage&remember=true')
    // The password form stays, for local accounts.
    expect(screen.getByLabelText('Username or email')).toBeInTheDocument()
  })

  it('offers the same button for a SAML identity provider, which the app starts the same way', async () => {
    fakeApi(null, { 'GET /api/auth/company': () => ({ json: { label: 'Entra ID', protocol: 'saml' } }) })
    renderApp('/login?rd=%2Fchat')
    expect(await screen.findByRole('link', { name: 'Sign in with Entra ID' })).toHaveAttribute('href', '/api/auth/company/start?rd=%2Fchat')
    expect(screen.getByLabelText('Username or email')).toBeInTheDocument()
  })

  it('shows no company button while it is off', async () => {
    fakeApi(null, { 'GET /api/auth/company': () => ({ json: { label: null } }) })
    renderApp('/login')
    expect(await screen.findByLabelText('Username or email')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /Sign in with/ })).toBeNull()
  })

  it('words what the identity provider’s answer came to', async () => {
    fakeApi(null, { 'GET /api/auth/company': () => ({ json: { label: 'Okta' } }) })
    renderApp('/login?error=company_not_allowed&rd=%2Fchat')
    expect(await screen.findByRole('alert')).toHaveTextContent('Your company account is not allowed to sign in here. Ask an admin.')
    expect(await screen.findByRole('link', { name: 'Sign in with Okta' })).toHaveAttribute('href', '/api/auth/company/start?rd=%2Fchat')
  })
})

describe('behind the form', () => {
  // jsdom has no Web Animations: each animation started is recorded instead.
  let started: { el: Element; options: KeyframeAnimationOptions; animation: { startTime: number | null; cancel: () => void; onfinish?: () => void } }[] = []
  beforeEach(() => {
    started = []
    Element.prototype.animate = function (this: Element, _frames: Keyframe[] | PropertyIndexedKeyframes | null, options?: number | KeyframeAnimationOptions) {
      const animation: (typeof started)[number]['animation'] = { startTime: null, cancel: vi.fn() }
      started.push({ el: this, options: options as KeyframeAnimationOptions, animation })
      return animation as unknown as Animation
    }
  })
  afterEach(() => {
    delete (Element.prototype as Partial<Element>).animate
    vi.unstubAllGlobals()
  })

  it('the logo appears, then its eyes fill the screen, all in step, and none of it is in the way', async () => {
    fakeApi(null)
    renderApp('/login')
    const name = await screen.findByLabelText('Username or email')
    const backdrop = document.querySelector('.eyes-backdrop')!
    expect(backdrop).toHaveAttribute('aria-hidden', 'true')
    await waitFor(() => expect(backdrop.querySelectorAll('.watch-eye').length).toBeGreaterThan(20))
    expect(backdrop.querySelectorAll('.watch-eye').length).toBeLessThanOrEqual(eyeCap(window.innerWidth))

    const logo = document.querySelector('img.eyes-logo')!
    expect(logo).toHaveAttribute('src', '/favicon.svg')
    await waitFor(() => expect(started.some((s) => s.el === logo)).toBe(true))
    const eyes = backdrop.querySelectorAll('.watch-eye').length
    // One turn for the logo and every eye, started at the same moment: they keep in step.
    const turns = started.filter((s) => s.options.duration === CYCLE)
    expect(turns.map((s) => s.el)).toEqual(expect.arrayContaining([logo, ...backdrop.querySelectorAll('.watch-eye')]))
    expect(turns).toHaveLength(eyes + 1)
    for (const s of turns) expect(s.options.iterations).toBe(Infinity)
    expect(new Set(turns.map((s) => s.animation.startTime)).size).toBe(1)
    // Each eye blinks and looks about, on its own time, and fades in as it comes.
    const blinks = started.filter((s) => s.el.classList.contains('watch-eye-lid') && s.options.iterations === Infinity)
    expect(blinks).toHaveLength(eyes)
    expect(started.filter((s) => s.el.classList.contains('watch-eye-iris'))).toHaveLength(eyes)
    expect(new Set(blinks.map((s) => s.options.duration)).size).toBeGreaterThan(eyes / 2)
    expect(started.filter((s) => s.el.classList.contains('watch-eye-lid') && s.options.duration === FADE)).toHaveLength(eyes)

    // The form is not behind it, and works as ever.
    expect(backdrop.contains(name)).toBe(false)
    expect(name.closest('[data-keep-clear]')).not.toBeNull()
    await userEvent.type(name, 'ada')
    expect(name).toHaveValue('ada')
  })

  it('when the form grows, the eyes beside it fade out and go, and every other stays put', async () => {
    // jsdom lays nothing out: the form is put on the screen by hand, and what watches the eyes' stage told.
    const watching: { on: () => void; what: Element[] }[] = []
    vi.stubGlobal('ResizeObserver', class {
      watch: { on: () => void; what: Element[] }
      constructor(on: () => void) {
        this.watch = { on, what: [] }
        watching.push(this.watch)
      }
      observe(e: Element) {
        this.watch.what.push(e)
      }
      unobserve() {}
      disconnect() {}
    })
    fakeApi(null)
    renderApp('/login')
    const form = (await screen.findByLabelText('Username or email')).closest('[data-keep-clear]')!
    const backdrop = document.querySelector('.eyes-backdrop')!
    await waitFor(() => expect(backdrop.querySelectorAll('.watch-eye').length).toBeGreaterThan(20))
    const place = () => [...backdrop.querySelectorAll('.watch-eye')].map((e) => e.getAttribute('style')).sort()
    const before = place()

    vi.spyOn(form, 'getBoundingClientRect').mockReturnValue(DOMRect.fromRect({ x: 560, y: 180, width: 380, height: 420 }))
    act(() => watching.find((w) => w.what.includes(backdrop))!.on())
    let going: typeof started = []
    await waitFor(() => {
      going = started.filter((s) => s.options.fill === 'forwards')
      expect(going.length).toBeGreaterThan(0)
    })
    expect(going.every((s) => s.options.duration === FADE && s.el.classList.contains('watch-eye-lid'))).toBe(true)
    // Still drawn while they fade; gone when they have.
    expect(place()).toEqual(before)
    act(() => going.forEach((s) => s.animation.onfinish!()))
    const after = place()
    expect(after).toHaveLength(before.length - going.length)
    expect(after.every((style) => before.includes(style))).toBe(true)
  })

  it('with reduced motion, only the logo, still', async () => {
    vi.stubGlobal('matchMedia', (query: string) => ({ matches: query.includes('reduce'), media: query, addEventListener() {}, removeEventListener() {} }) as unknown as MediaQueryList)
    fakeApi(null)
    renderApp('/login')
    await screen.findByLabelText('Username or email')
    expect(document.querySelector('img.eyes-logo')).toBeInTheDocument()
    await new Promise((r) => setTimeout(r, 50))
    expect(document.querySelectorAll('.watch-eye')).toHaveLength(0)
    expect(started).toEqual([])
  })
})
