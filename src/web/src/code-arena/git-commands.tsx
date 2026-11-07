import { useQuery } from '@tanstack/react-query'
import { useEffect, useRef } from 'react'
import { useCommands } from './commands'
import { useGit } from './git-actions'
import { gitStatusQuery } from './ide-api'
import { quickPick } from './quick-pick'
import { useRepositories } from './repositories'
import { useSettings } from './settings'

/** Fetch every three minutes when git.autofetch is on. */
const autofetchEvery = 3 * 60_000

/**
 * Git's commands in the palette and for keybindings.json, as VS Code names them (git.pull, git.push, git.sync…), and the
 * fetch git.autofetch asks for. With several repositories a command asks which, as VS Code's does (autofetch fetches
 * each). Drawn once, with nothing to show.
 */
export function GitCommands() {
  const git = useGit()
  const { repositories } = useRepositories()
  const list = repositories ?? []
  const repo = useQuery(gitStatusQuery).data?.repository === true || list.length > 0
  const autofetch = useSettings().get('git.autofetch') === true
  /** The repository a command works on: the only one, or the one chosen. */
  const which = async (): Promise<string | undefined> => {
    if (list.length <= 1) return list[0]?.path ?? '.'
    const picked = await quickPick({ title: 'Repository', placeholder: 'Select a repository', items: list.map((r) => ({ id: r.path, label: r.name, description: r.path === '.' ? 'the folder' : r.path })) })
    return picked?.id
  }
  const on = (run: (g: ReturnType<typeof git.in>) => unknown) => async () => {
    const r = await which()
    if (r !== undefined) await run(git.in(r))
  }
  useCommands([
    { id: 'git.fetch', category: 'Git', title: 'Fetch', enabled: () => repo, run: on((g) => g.fetch()) },
    { id: 'git.fetchAll', category: 'Git', title: 'Fetch From All Repositories', enabled: () => list.length > 1, run: async () => {
      for (const r of list) await git.in(r.path).fetch()
    } },
    { id: 'git.pull', category: 'Git', title: 'Pull', enabled: () => repo, run: on((g) => g.pull()) },
    { id: 'git.push', category: 'Git', title: 'Push', enabled: () => repo, run: on((g) => g.push()) },
    { id: 'git.pushForce', category: 'Git', title: 'Push (Force with Lease)', enabled: () => repo, run: on((g) => g.push(true)) },
    { id: 'git.publish', category: 'Git', title: 'Publish Branch', enabled: () => repo, run: on((g) => g.publish()) },
    { id: 'git.sync', category: 'Git', title: 'Sync', enabled: () => repo, run: on((g) => g.sync()) },
    { id: 'git.checkout', category: 'Git', title: 'Checkout to…', enabled: () => repo, run: on((g) => g.checkout()) },
    { id: 'git.branch', category: 'Git', title: 'Create Branch…', enabled: () => repo, run: on((g) => g.createBranch()) },
    { id: 'git.renameBranch', category: 'Git', title: 'Rename Branch…', enabled: () => repo, run: on((g) => g.renameBranch()) },
    { id: 'git.deleteBranch', category: 'Git', title: 'Delete Branch…', enabled: () => repo, run: on((g) => g.deleteBranch()) },
    { id: 'git.undoCommit', category: 'Git', title: 'Undo Last Commit', enabled: () => repo, run: on((g) => g.undoCommit()) },
  ])
  const paths = list.length > 1 ? list.map((r) => r.path).join('\n') : '.'
  // The actions are made again at each render: the timer reads the last ones, and is not set again for each.
  const fetchIn = useRef(git.in)
  useEffect(() => {
    fetchIn.current = git.in
  })
  useEffect(() => {
    if (!autofetch || !repo) return
    const timer = setInterval(() => {
      for (const r of paths.split('\n')) void fetchIn.current(r).fetch()
    }, autofetchEvery)
    return () => clearInterval(timer)
  }, [autofetch, repo, paths])
  return null
}
