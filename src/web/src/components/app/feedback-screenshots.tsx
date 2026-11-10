import { screenshotUrls } from './screenshots'

/** The screenshots sent with feedback, small; each opens full size in a tab of its own. */
export function FeedbackScreenshots({ base, count }: { base: string; count: number }) {
  if (count <= 0) return null
  return (
    <ul aria-label="Screenshots" className="flex flex-wrap gap-2">
      {screenshotUrls(base, count).map((url, n) => (
        <li key={url}>
          <a href={url} target="_blank" rel="noreferrer" aria-label={`Screenshot ${n + 1}, full size`}>
            <img src={url} alt={`Screenshot ${n + 1}`} loading="lazy" className="h-20 w-32 rounded-md border object-cover hover:opacity-90" />
          </a>
        </li>
      ))}
    </ul>
  )
}
