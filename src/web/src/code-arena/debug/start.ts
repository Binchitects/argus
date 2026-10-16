import type { QueryClient } from '@tanstack/react-query'
import { toast } from '@/components/ui/toaster'
import { ApiError, errorMessage } from '@/lib/api'
import { inputBox, quickPick } from '../quick-pick'
import { askTrust } from '../trust'
import { debug } from './session'
import { listProcesses, runTask, startDebug, startRun, type RunContext, type Started, type TaskInfo } from './run-api'

/** What a start that was refused asked for: the inputs of ${input:…}, the commands that would run untrusted. */
interface Asked {
  inputs?: { id: string; type: string; description?: string; default?: string; options?: { label: string; value: string }[]; password?: boolean }[]
  commands?: string[]
  install?: string
  adapter?: string
  terminal?: unknown
}

/**
 * A start as VS Code does it, whatever is asked on the way: the folder's trust (listing what would run), the values
 * of ${input:…} (promptString, pickString), then the start again. Undefined when the person cancelled or it failed (a
 * message says why: a missing adapter says how to get it).
 */
async function withAnswers<T>(go: (inputs: Record<string, string>) => Promise<T>): Promise<T | undefined> {
  const inputs: Record<string, string> = {}
  for (let attempt = 0; attempt < 5; attempt++) {
    try {
      return await go(inputs)
    } catch (e) {
      if (!(e instanceof ApiError)) {
        toast.error(errorMessage(e))
        return undefined
      }
      const asked = (e.data ?? {}) as Asked
      if (e.status === 'untrusted') {
        const commands = asked.commands?.length ? `It would run: ${asked.commands.slice(0, 5).join('; ')}${asked.commands.length > 5 ? '…' : ''}.` : ''
        if (!(await askTrust(`The folder's own tasks and run configurations run only in a folder you trust. ${commands}`))) return undefined
        continue
      }
      if (e.status === 'needs_input' && asked.inputs) {
        for (const input of asked.inputs) {
          if (inputs[input.id] !== undefined) continue
          const value =
            input.type === 'pickProcess'
              ? (await pickProcess())
              : input.type === 'pickString'
              ? (await quickPick({ title: input.description ?? input.id, placeholder: input.description ?? `Choose ${input.id}`, items: (input.options ?? []).map((o) => ({ id: o.value, label: o.label })), active: input.default }))?.id
              : await inputBox({ title: input.description ?? input.id, placeholder: input.description ?? `Type ${input.id}`, value: input.default, password: input.password })
          if (value === undefined) return undefined
          inputs[input.id] = value
        }
        continue
      }
      if (e.status === 'no_adapter') {
        toast.error(e.message, { description: asked.install ? `To get it: ${asked.install}` : undefined, duration: 15_000 })
        return undefined
      }
      toast.error(e.message)
      return undefined
    }
  }
  return undefined
}

/** ${command:pickProcess}: one of the person's own processes, by its name, id or command line. */
async function pickProcess(): Promise<string | undefined> {
  let list
  try {
    list = await listProcesses()
  } catch (e) {
    toast.error(errorMessage(e))
    return undefined
  }
  const picked = await quickPick({
    title: 'Attach to a process',
    placeholder: 'Select the process to attach to (type a few letters of its name or command)',
    items: list.map((p) => ({ id: String(p.pid), label: p.name, description: String(p.pid), detail: p.command ?? undefined })),
  })
  return picked?.id
}

/** Starts a run configuration with its debugger, or without (Run Without Debugging): the terminal it runs in, or the session. */
export async function startConfiguration(name: string, noDebug: boolean, context: RunContext, queryClient: QueryClient): Promise<Started | 'debugging' | undefined> {
  void queryClient
  if (noDebug) return withAnswers((inputs) => startRun({ name, noDebug: true, context, inputs }))
  const started = await withAnswers((inputs) => startDebug({ name, noDebug: false, context, inputs }))
  if (!started) return undefined
  for (const note of started.notes) toast.info(note)
  try {
    await debug.open(started)
    return 'debugging'
  } catch (e) {
    toast.error(`The debugger did not start: ${errorMessage(e)}`)
    return undefined
  }
}

/** Runs a task (by label, or a group's default: build, test), after the trust and inputs it asks for. */
export async function startTask(which: { label?: string; group?: 'build' | 'test' }, context: RunContext, tasks?: TaskInfo[]) {
  const done = await withAnswers(async (inputs) => {
    try {
      return await runTask({ ...which, context, inputs })
    } catch (e) {
      // Several tasks in the group, none the default: the person picks one.
      if (e instanceof ApiError && e.status === 'pick') {
        const list = ((e.data as { tasks?: string[] } | undefined)?.tasks ?? []).map((label) => tasks?.find((t) => t.label === label) ?? { label, command: null })
        const picked = await quickPick({ title: `Run ${which.group} task`, placeholder: `Select the ${which.group} task to run`, items: list.map((t) => ({ id: t.label, label: t.label, description: t.command ?? undefined })) })
        if (!picked) return undefined
        return runTask({ label: picked.id, context, inputs })
      }
      throw e
    }
  })
  return done ?? undefined
}
