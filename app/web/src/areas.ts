/** The areas of the app. Until an area is native, its page points to the service that does the job today. */
export interface Area {
  path: string
  title: string
  summary: string
  /** The plan phase that makes this area native (docs/enterprise/PLAN.md). */
  phase: number
  /** Subdomain of the service that covers this area until then. */
  legacy: { subdomain: string; name: string }
  /** Built into the app already. */
  native?: boolean
  /** Only shown to admins. */
  adminOnly?: boolean
}

export const areas: Area[] = [
  {
    path: '/chat',
    title: 'Chat',
    summary: 'Talk to the model, with Argus answering from your code.',
    phase: 3,
    legacy: { subdomain: 'chat', name: 'Open WebUI' },
  },
  {
    path: '/usage',
    title: 'Usage & cost',
    summary: 'Tokens (cache hit, cache miss, output) and cost, per person and over time.',
    phase: 2,
    legacy: { subdomain: 'grafana', name: 'Grafana' },
    native: true,
  },
  {
    path: '/dashboards',
    title: 'Dashboards',
    summary: 'Model performance, GPU and host resources, logs and alerts.',
    phase: 5,
    legacy: { subdomain: 'grafana', name: 'Grafana' },
  },
  {
    path: '/argus',
    title: 'Argus',
    summary: 'The code index: repositories, indexing status and knowledge packs.',
    phase: 4,
    legacy: { subdomain: 'admin', name: 'the admin panel' },
  },
  {
    path: '/admin',
    title: 'Admin',
    summary: 'People, the model, the code index and packs, services, settings and the audit log.',
    phase: 1,
    legacy: { subdomain: 'admin', name: 'the admin panel' },
    native: true,
    adminOnly: true,
  },
]

/** https://chat.llm.example.com when the app is served from https://llm.example.com. */
export function legacyUrl(area: Area, location: Pick<Location, 'protocol' | 'host'> = window.location): string {
  return `${location.protocol}//${area.legacy.subdomain}.${location.host}/`
}
