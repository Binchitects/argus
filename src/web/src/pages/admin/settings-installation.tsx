import { Copy, Terminal } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { toast } from '@/components/ui/toaster'
import { installationTitle, type InstallSettingView } from './settings-model'

/**
 * What the installation sets (.env, through the installer) rather than the app: each with its value where the app sees
 * one (a secret only as set or not) and the one command that changes it on the host. Nothing here is saved by the app.
 */
export function InstallationPanel({ rows, searching }: { rows: InstallSettingView[]; searching?: boolean }) {
  const groups = [...new Set(rows.map((r) => r.group))]
  const copy = async (text: string) => {
    try {
      await navigator.clipboard.writeText(text)
      toast.success('Copied')
    } catch {
      toast.error('Copy it by hand: the browser did not let the page copy.')
    }
  }
  return (
    <Card id="set-by-the-installation" className="scroll-mt-20" aria-labelledby="set-by-the-installation-title">
      <CardHeader>
        <CardTitle id="set-by-the-installation-title" className="flex items-center gap-2">
          <Terminal className="size-4" aria-hidden /> {installationTitle}
        </CardTitle>
        {!searching && (
          <CardDescription>
            The address, the models&apos; folder and engines, GitLab, backups, power and heat, the sandboxes&apos; size, scale and the database are the installation&apos;s: changed on the host
            with the command beside each, which starts again what they touch and waits until every service is healthy. A secret is read from a file, never typed on the command line.
          </CardDescription>
        )}
      </CardHeader>
      <CardContent className="grid gap-5">
        {groups.map((g) => (
          <section key={g} aria-label={g} className="grid gap-1">
            <h3 className="text-sm font-medium">{g}</h3>
            <ul className="divide-y rounded-md border">
              {rows
                .filter((r) => r.group === g)
                .map((r) => (
                  <li key={r.env} className="grid gap-1.5 px-3 py-2.5 text-sm md:grid-cols-[minmax(0,14rem)_1fr] md:gap-4">
                    <div className="grid gap-0.5">
                      <span className="font-mono text-xs break-all">{r.env}</span>
                      <span className="text-xs">
                        {r.secret ? (
                          <Badge variant={r.isSet ? 'success' : 'outline'}>{r.isSet ? 'set' : 'not set'}</Badge>
                        ) : r.value ? (
                          <span className="font-mono break-all">{r.value}</span>
                        ) : (
                          <span className="text-muted-foreground">{r.default ? `the default: ${r.default}` : 'not set'}</span>
                        )}
                      </span>
                    </div>
                    <div className="grid min-w-0 gap-1">
                      <p className="text-muted-foreground">{r.what}</p>
                      <div className="flex min-w-0 items-center gap-1">
                        <code className="min-w-0 flex-1 overflow-x-auto rounded bg-muted px-2 py-1 font-mono text-xs whitespace-nowrap">{r.command}</code>
                        <Button size="icon-sm" variant="ghost" aria-label={`Copy the command for ${r.env}`} onClick={() => void copy(r.command)}>
                          <Copy />
                        </Button>
                      </div>
                    </div>
                  </li>
                ))}
            </ul>
          </section>
        ))}
      </CardContent>
    </Card>
  )
}
