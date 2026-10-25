import { useQueryClient } from '@tanstack/react-query'
import { LogOut, ShieldCheck } from 'lucide-react'
import { TwoFactor } from '@/components/app/two-factor'
import { Button } from '@/components/ui/button'
import { meQuery, type Me } from '@/lib/api'
import { useSignOut } from './use-sign-out'

/**
 * The sign-in of someone the company requires a second factor of, who has none yet: they set it up here before anything
 * else (the API answers nothing else until then), and go on once its recovery codes are kept.
 */
export function SetUpTwoFactor({ me }: { me: Me }) {
  const queryClient = useQueryClient()
  const signOut = useSignOut()
  return (
    <main className="grid min-h-dvh place-items-center bg-background px-4 py-10">
      <div className="grid w-full max-w-lg gap-5">
        <div className="grid gap-2">
          <ShieldCheck className="size-8 text-primary" aria-hidden="true" />
          <h1 className="text-2xl font-semibold tracking-tight">Set up two-factor sign-in</h1>
          <p className="text-muted-foreground">
            Hello {me.displayName}. Your company asks for a code from your phone as well as your password, so a stolen password is not enough. It takes a minute, and you need it from now on.
          </p>
        </div>
        <TwoFactor enabled={false} onDone={() => void queryClient.invalidateQueries({ queryKey: meQuery.queryKey })} />
        <div>
          <Button variant="ghost" onClick={signOut}>
            <LogOut /> Sign out
          </Button>
        </div>
      </div>
    </main>
  )
}
