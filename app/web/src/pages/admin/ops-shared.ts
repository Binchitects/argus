/** What an Argus index run's exit code means (src/argus/cli.py). */
export const indexExit: Record<string, string> = {
  '0': 'completed',
  '1': 'ran, but at least one repository is unhealthy; the log names it',
  '3': 'could not reach GitLab, or the token cannot list every repository',
  '4': 'preflight failed: ctags is missing or not Universal Ctags, or the include graph could not be rebuilt',
  '-1': 'Argus could not start the run at all',
}

export const host = (sub: string) => `${window.location.protocol}//${sub}.${window.location.host}`
