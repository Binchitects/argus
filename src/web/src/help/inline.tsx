/** Help text: **words** are the page's own labels, in bold. */
export function Inline({ text }: { text: string }) {
  return <>{text.split(/\*\*(.+?)\*\*/g).map((part, i) => (i % 2 ? <strong key={i} className="font-medium text-foreground">{part}</strong> : part))}</>
}
