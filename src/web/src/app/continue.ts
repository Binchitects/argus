/**
 * Where /continue?rd= goes on to once a step of signing in is done (the notice accepted, two-factor sign-in set up): the
 * request the app held back for it, another app's sign-in through this one. Only a path on this site, never another site;
 * null when this is not /continue.
 */
export function continueTo(location: { pathname: string; search: string }): string | null {
  if (location.pathname !== '/continue') return null
  const rd = new URLSearchParams(location.search).get('rd')
  return rd && rd.startsWith('/') && !rd.startsWith('//') && !rd.startsWith('/\\') ? rd : '/'
}
