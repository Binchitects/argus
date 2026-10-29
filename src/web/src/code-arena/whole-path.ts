/** A path outside the folder (in a folder added to it): whole, as the page holds it (/srv/x, C:/x). */
export const isOutside = (path: string) => path.startsWith('/') || /^[A-Za-z]:\//.test(path)

/** A file's whole path, as the person's system writes it: one in an added folder is whole already. */
export function wholePath(folder: string, path: string): string {
  if (isOutside(path)) return path
  // A Windows folder (C:\shop) gets its own separators.
  const windows = /^[A-Za-z]:\\/.test(folder) || (folder.includes('\\') && !folder.includes('/'))
  const base = folder.replace(/[\\/]+$/, '')
  return windows ? `${base}\\${path.replace(/\//g, '\\')}` : `${base}/${path}`
}
