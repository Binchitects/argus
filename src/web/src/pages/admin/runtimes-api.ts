import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useConfirm } from '@/components/ui/confirm'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage, type Me } from '@/lib/api'

export type RuntimeAction = 'reload' | 'restart' | 'reset'

/** An engine, a picture or video server, or a sandbox, as GET /api/admin/runtimes says. */
export interface Runtime {
  id: string
  kind: 'engine' | 'media' | 'sandbox'
  label: string
  /** reloading, restarting, resetting: asked, and not taken yet; loading: its models are on their way back. */
  state: 'up' | 'idle' | 'loading' | 'down' | 'reloading' | 'restarting' | 'resetting'
  since: string | null
  detail: string | null
  /** An engine's answers being written and waiting (this replica's), a sandbox's jobs. */
  busy: number
  waiting: number
  actions: RuntimeAction[]
  last: { action: RuntimeAction; by: string | null; at: string } | null
}

/** The states that pass by themselves: the list is asked again every 3 s while one is shown. */
export const passing = new Set<Runtime['state']>(['loading', 'reloading', 'restarting', 'resetting'])

/** How often the list is asked again: every 3 s while one of them is on its way back, else every 15 s. */
export const pollEvery = (list: Runtime[] | undefined) => (list?.some((r) => passing.has(r.state)) ? 3_000 : 15_000)

export const actionLabel: Record<RuntimeAction, string> = { reload: 'Reload', restart: 'Restart', reset: 'Reset' }

const done: Record<RuntimeAction, string> = { reload: 'reloaded', restart: 'restarted', reset: 'reset' }

export const plural = (n: number, what: string) => `${n} ${what}${n === 1 ? '' : 's'}`

/** Whether the person may ask it: Reload with models.load, Restart and Reset with runtimes.restart. */
export function mayAsk(me: Me | undefined, action: RuntimeAction): boolean {
  return !!me && (!!me.isAdmin || !!me.permissions?.includes(action === 'reload' ? 'models.load' : 'runtimes.restart'))
}

/** One to ask of: its counts unknown where the page has none (Admin → Models). */
type Asked = Pick<Runtime, 'id' | 'kind' | 'label'> & Partial<Pick<Runtime, 'busy' | 'waiting'>>

/** What the confirm says an action does to it. */
export function consequence(r: Omit<Asked, 'id'>, action: RuntimeAction): { title: string; description: string; confirm: string } {
  const title = `${actionLabel[action]} ${r.label}?`
  if (action === 'reset')
    return {
      title,
      description: `Stops ${plural(r.busy ?? 0, 'running job')} and gives back ${plural(r.waiting ?? 0, 'waiting job')}, each caller told why; then it starts again. For jobs that hang.`,
      confirm: 'Reset',
    }
  if (r.kind === 'media') return { title, description: 'Its server starts again with its model: a picture or video being made is lost.', confirm: 'Restart' }
  const cut =
    r.busy === undefined
      ? 'Answers being written on it are cut off; those waiting wait until it is back.'
      : `${r.busy === 1 ? '1 answer being written is' : `${r.busy} answers being written are`} cut off; ${r.waiting ?? 0} waiting wait until it is back.`
  return action === 'reload'
    ? { title, description: `Its models unload and load again. ${cut}`, confirm: 'Reload' }
    : { title, description: `Its process starts again, then the models kept loaded load again. ${cut}`, confirm: 'Restart' }
}

/** Asks an action of an engine or a sandbox, once confirmed; the lists are asked again after. */
export function useRuntimeAsk() {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const ask = useMutation({
    mutationFn: ({ id, action }: { id: string; action: RuntimeAction; label: string }) => api(`/api/admin/runtimes/${encodeURIComponent(id)}/${action}`, { body: {} }),
    onSuccess: (_, { action, label }) => {
      toast.success(`${label} is being ${done[action]}`)
      void queryClient.invalidateQueries({ queryKey: ['admin', 'runtimes'] })
      void queryClient.invalidateQueries({ queryKey: ['admin', 'models'] })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return {
    /** The one asked just now, while its request runs. */
    asking: ask.isPending ? ask.variables : undefined,
    run: async (r: Asked, action: RuntimeAction) => {
      if (await confirm({ ...consequence(r, action), destructive: action !== 'reload' })) ask.mutate({ id: r.id, action, label: r.label })
    },
  }
}
