import { useQueryClient } from '@tanstack/react-query'
import { ShieldAlert } from 'lucide-react'
import { useSyncExternalStore } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { toast } from '@/components/ui/toaster'
import { errorMessage } from '@/lib/api'
import { trustWorkspace, workspaceStateQuery } from './ide-api'
import { answerTrust, onTrustAsk, trustAsked } from './trust'

/** Trust this folder? As VS Code's workspace trust: what would run, and the choice kept on this computer. */
export function TrustDialog({ folder }: { folder: string }) {
  const ask = useSyncExternalStore(onTrustAsk, trustAsked)
  const queryClient = useQueryClient()
  if (!ask) return null
  const decide = async (trusted: boolean) => {
    try {
      queryClient.setQueryData(workspaceStateQuery.queryKey, await trustWorkspace(trusted))
      void queryClient.invalidateQueries({ queryKey: ['code', 'formatters'] })
      answerTrust(trusted)
    } catch (e) {
      toast.error(errorMessage(e))
      answerTrust(false)
    }
  }
  return (
    <Dialog open onOpenChange={(o) => !o && answerTrust(false)}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <ShieldAlert className="size-5 text-warning" aria-hidden="true" /> Trust the folder {folder}?
          </DialogTitle>
          <DialogDescription>{ask.why}</DialogDescription>
        </DialogHeader>
        <p className="text-sm text-muted-foreground">
          Trusted, the folder&apos;s own programs may run here: the formatters it holds (node_modules, a .venv), the commands of .vscode/tasks.json and launch.json, and language servers with its settings. Trust only a folder whose code you
          trust. The choice is kept on this computer, never in the project: Manage Workspace Trust changes it.
        </p>
        <DialogFooter>
          <Button variant="outline" onClick={() => void decide(false)}>
            Keep it restricted
          </Button>
          <Button onClick={() => void decide(true)}>Trust the folder</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
