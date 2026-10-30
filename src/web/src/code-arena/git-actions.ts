import { useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useConfirm } from '@/components/ui/confirm'
import { toast } from '@/components/ui/toaster'
import { ApiError, errorMessage } from '@/lib/api'
import { useEditor } from './editor-state'
import { gitAllBranches, gitCreateBranch, gitDeleteBranch, gitFetch, gitPull, gitPush, gitRenameBranch, gitStatusQuery, gitSwitch, gitSync, gitUndoCommit, type GitStatus } from './ide-api'
import { inputBox, quickPick } from './quick-pick'
import { useSettings } from './settings'

/** A branch name as git takes one, checked as the person types: git says the rest. */
const branchProblem = (v: string) =>
  !v.trim() ? 'Type the branch name' : /\s|\.\.|[~^:?*[\\]|^-|\/$|\.lock$|^@$/.test(v.trim()) ? 'A branch name has no spaces, "..", ~ ^ : ? * [ or \\, and does not start with -' : null

/**
 * What Source Control, the status bar and the commands do with git, as VS Code's: fetch, pull, push (a branch with no
 * upstream published), sync (pull, then push: asked first, git.confirmSync), branches made, renamed, deleted and checked
 * out (a remote's tracked, a tag's commit alone), and the last commit undone. Each says what went wrong as git said it.
 */
export function useGit() {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const settings = useSettings()
  const editor = useEditor()
  const [busy, setBusy] = useState<string | null>(null)
  const rebase = () => settings.get('git.rebaseWhenSync') === true
  const settle = async () => {
    // The files in the editor and the explorer as git left them.
    void editor.refresh()
    void queryClient.invalidateQueries({ queryKey: ['code', 'files'] })
    await queryClient.invalidateQueries({ queryKey: ['code', 'git'] })
  }
  const run = async (what: string, call: () => Promise<unknown>, done?: string) => {
    setBusy(what)
    try {
      await call()
      if (done) toast.success(done)
      return true
    } catch (e) {
      toast.error(errorMessage(e))
      return false
    } finally {
      setBusy(null)
      await settle()
    }
  }
  const status = () => queryClient.getQueryData<GitStatus>(gitStatusQuery.queryKey)

  const createBranch = async (from?: string) => {
    const name = await inputBox({ title: from ? `Create a branch from ${from}` : 'Create a branch', placeholder: 'The new branch\'s name', validate: branchProblem })
    if (name?.trim()) await run('branch', () => gitCreateBranch(name.trim(), from), `Switched to a new branch ${name.trim()}.`)
  }

  return {
    busy,
    rebase,
    fetch: () => run('fetch', gitFetch),
    pull: () => run('pull', () => gitPull(rebase())),
    push: (force = false) => run('push', () => gitPush(force)),
    publish: () => run('push', () => gitPush(), 'Published the branch.'),
    sync: async () => {
      const s = status()
      if (settings.get('git.confirmSync') !== false) {
        const ok = await confirm({
          title: 'Sync the branch?',
          description: `This pulls ${s?.behind ?? 0} ${s?.behind === 1 ? 'commit' : 'commits'} from its upstream${rebase() ? ' (rebasing on them)' : ''} and pushes ${s?.ahead ?? 0} to it. Turn this question off with "git.confirmSync".`,
          confirm: 'Sync',
        })
        if (!ok) return false
      }
      return run('sync', () => gitSync(rebase()), 'Synced.')
    },
    createBranch,
    /** Checkout to…: a new branch (from what is checked out, or from another), a local branch, a remote's, a tag. */
    checkout: async () => {
      let all
      try {
        all = await gitAllBranches()
      } catch (e) {
        toast.error(errorMessage(e))
        return
      }
      const kinds = { local: 'Branches', remote: 'Remote branches', tag: 'Tags' } as const
      const picked = await quickPick({
        title: 'Checkout to…',
        placeholder: 'Select a branch or tag to check out',
        items: [
          { id: 'new', label: '+ Create new branch…' },
          { id: 'from', label: '+ Create new branch from…' },
          ...all.filter((b) => !b.current).map((b) => ({ id: `${b.kind}:${b.name}`, label: b.name, group: kinds[b.kind], value: b })),
        ],
      })
      if (!picked) return
      if (picked.id === 'new') return createBranch()
      if (picked.id === 'from') {
        const from = await quickPick({ title: 'Create a branch from…', placeholder: 'Select the branch or tag to start from', items: all.map((b) => ({ id: `${b.kind}:${b.name}`, label: b.name, group: kinds[b.kind], value: b })) })
        return from?.value ? createBranch(from.value.name) : undefined
      }
      const b = picked.value!
      await run('switch', () => gitSwitch(b.name, b.kind))
    },
    renameBranch: async () => {
      const s = status()
      if (!s?.branch) return toast.error('No branch is checked out.')
      const to = await inputBox({ title: `Rename ${s.branch}`, placeholder: 'The branch\'s new name', value: s.branch, validate: branchProblem })
      if (to?.trim() && to.trim() !== s.branch) await run('branch', () => gitRenameBranch(to.trim()))
    },
    deleteBranch: async () => {
      let all
      try {
        all = (await gitAllBranches()).filter((b) => b.kind === 'local' && !b.current)
      } catch (e) {
        toast.error(errorMessage(e))
        return
      }
      if (all.length === 0) return toast.error('There is no other branch to delete.')
      const picked = await quickPick({ title: 'Delete a branch', placeholder: 'Select the branch to delete', items: all.map((b) => ({ id: b.name, label: b.name })) })
      if (!picked) return
      try {
        await gitDeleteBranch(picked.id)
        toast.success(`Deleted ${picked.id}.`)
      } catch (e) {
        if (!(e instanceof ApiError) || e.status !== 'not_merged') {
          toast.error(errorMessage(e))
        } else if (await confirm({ title: `Delete ${picked.id} anyway?`, description: `${e.message} This cannot be undone.`, confirm: 'Delete', destructive: true })) {
          await run('branch', () => gitDeleteBranch(picked.id, true), `Deleted ${picked.id}.`)
        }
      }
      await settle()
    },
    undoCommit: async () => {
      const ok = await confirm({ title: 'Undo the last commit?', description: 'The commit goes; what it changed stays, staged, to commit again.', confirm: 'Undo' })
      if (ok) await run('undo', gitUndoCommit, 'The last commit is undone: its changes are staged.')
    },
  }
}
