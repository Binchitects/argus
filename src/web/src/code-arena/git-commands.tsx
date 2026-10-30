import { useQuery } from '@tanstack/react-query'
import { useEffect } from 'react'
import { useCommands } from './commands'
import { useGit } from './git-actions'
import { gitStatusQuery } from './ide-api'
import { useSettings } from './settings'

/** Fetch every three minutes when git.autofetch is on. */
const autofetchEvery = 3 * 60_000

/**
 * Git's commands in the palette and for keybindings.json, as VS Code names them (git.pull, git.push, git.sync…), and the
 * fetch git.autofetch asks for. Drawn once, with nothing to show.
 */
export function GitCommands() {
  const git = useGit()
  const repo = useQuery(gitStatusQuery).data?.repository === true
  const autofetch = useSettings().get('git.autofetch') === true
  useCommands([
    { id: 'git.fetch', category: 'Git', title: 'Fetch', enabled: () => repo, run: () => git.fetch() },
    { id: 'git.pull', category: 'Git', title: 'Pull', enabled: () => repo, run: () => git.pull() },
    { id: 'git.push', category: 'Git', title: 'Push', enabled: () => repo, run: () => git.push() },
    { id: 'git.pushForce', category: 'Git', title: 'Push (Force with Lease)', enabled: () => repo, run: () => git.push(true) },
    { id: 'git.publish', category: 'Git', title: 'Publish Branch', enabled: () => repo, run: () => git.publish() },
    { id: 'git.sync', category: 'Git', title: 'Sync', enabled: () => repo, run: () => git.sync() },
    { id: 'git.checkout', category: 'Git', title: 'Checkout to…', enabled: () => repo, run: () => git.checkout() },
    { id: 'git.branch', category: 'Git', title: 'Create Branch…', enabled: () => repo, run: () => git.createBranch() },
    { id: 'git.renameBranch', category: 'Git', title: 'Rename Branch…', enabled: () => repo, run: () => git.renameBranch() },
    { id: 'git.deleteBranch', category: 'Git', title: 'Delete Branch…', enabled: () => repo, run: () => git.deleteBranch() },
    { id: 'git.undoCommit', category: 'Git', title: 'Undo Last Commit', enabled: () => repo, run: () => git.undoCommit() },
  ])
  const { fetch } = git
  useEffect(() => {
    if (!autofetch || !repo) return
    const timer = setInterval(() => void fetch(), autofetchEvery)
    return () => clearInterval(timer)
  }, [autofetch, repo, fetch])
  return null
}
