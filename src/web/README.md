# Web

The web at `https://<LLM_DOMAIN>`: React 19 + TypeScript, built by Vite and served
as static files by nginx in its own container (`web` in `deploy/docker-compose.yml`).
Traefik sends `/api`, `/connect` and `/.well-known` to the app and everything else
here. The chat is described in [docs/chat.md](../../docs/chat.md).

```
src/
  app/              shell, navigation, routes, command palette, providers
  components/ui/    the design system: Radix primitives + Tailwind, our own components
  components/app/   pieces shared by pages (page header, secrets shown once)
  pages/            one file per page; pages/chat/ is the chat (tree, live stream, markdown, files)
  code-arena/       Code Arena's web interface (code-arena web): the chat's components around its API,
                    built on its own (vite.code-arena.config.ts) into src/CodeArena/web, not part of npm run build
  help/             the manual (/help: docs/ built into the page, searched here) and each
                    page's help (the ? in the top bar): topics.ts, and route-help.ts for which page has which
  lib/              API client, theme, formatting, command-palette matching
  styles/index.css  design tokens (light and dark) and base styles
e2e/                Playwright: desktop and phone, both themes, axe accessibility
ci/                 Traefik config for the CI browser run
Dockerfile          build -> Alpine + nginx (non-root, read-only root); built from the
                    repository root, for the manual reads docs/
nginx.conf          security headers, caching, SPA fallback, /healthz
```

## Design system

- **Tokens** in `src/styles/index.css`: a cool grey and the brand blue, with light
  and dark values. Text on a tint of a colour (badges, callouts, selected items)
  uses the `*-ink` tokens; every pair was computed at 4.5:1 or better.
- **Components** in `src/components/ui/`, on Radix for behaviour and accessibility.
  `/design` shows every component in its states. The browser tests check that page
  with axe, in both themes.
- **Forms** use react-hook-form + zod, with `<Field>` for the label, hint and error.
  A control inside a `Field` takes its id and `aria-*` from it.
- **Status** is never shown by colour alone: badges and callouts have an icon and words.
- Everything is bundled: the fonts (Inter, JetBrains Mono) and the icons (Lucide).
  Nothing is fetched from a CDN, so air-gapped installs work, and the CSP stays
  `'self'` for scripts, fonts and connections.

## Develop

```bash
npm ci
npm run dev          # http://localhost:5173, API forwarded to API_URL (default https://llm.localhost)
npm test             # Vitest
npm run typecheck && npm run lint && npm run build
```

## Browser tests

```bash
# against the deployed stack
E2E_PASSWORD=<admin password> npm run e2e                 # https://llm.localhost
E2E_CHAT=1 E2E_PASSWORD=... npm run e2e                     # and the chat, against the real model
E2E_BASE_URL=https://llm.example.com E2E_PASSWORD=... npm run e2e
```

- `E2E_CHANNEL=chrome` uses the installed Chrome when Playwright's own browser
  can't be downloaded.
- `E2E_NO_GATEWAY=1` is for a stack without a model gateway, as in CI.
- CI (`frontend-e2e` in `.github/workflows/app.yml`) runs the real `web` and `app`
  images behind Traefik with the production path split.

## Installable app

- `public/sw.js` is the service worker: the app's shell when offline (pages
  network-first, the fingerprinted `/assets/` kept once fetched), never `/api`;
  and the bell's news pushed while no page is open. `src/lib/pwa.ts` registers
  it (not under `npm run dev`), keeps the browser's offer to install, and
  subscribes this device for pushes.
- The manifest comes from the app (`/api/app/manifest.webmanifest`), so it has
  the product's name from the Settings page.
- The icons in `public/icons/` are `favicon.svg` and `icons/maskable.svg` (the
  same, full-bleed, inside the maskable safe zone), drawn by headless Chrome:
  `google-chrome --headless=new --default-background-color=00000000 --window-size=512,512 --screenshot=icon-512.png page.html`,
  where the page shows the SVG at the window's size.
- `/ask` takes a page or a selection from the browser extension
  (`clients/browser-extension/`) in its fragment (`src/lib/handoff.ts`).
