import { ApiError } from '../api'

export function ErrorText({ error }: { error: unknown }) {
  if (!error) return null
  const message = error instanceof ApiError || error instanceof Error ? error.message : 'Something went wrong.'
  return (
    <p className="error" role="alert">
      {message}
    </p>
  )
}
